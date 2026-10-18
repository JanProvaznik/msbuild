// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Microsoft.Build.Aot.TaskCatalogGenerator;

/// <summary>
/// Scans an SDK layout for the task assemblies its targets can name and the SDK resolvers its manifests name, and
/// generates what the Native AOT MSBuild needs to compile them in:
/// <list type="bullet">
/// <item>a C# file registering every task class (<c>Task.RegisterPrecompiledTask</c>) and resolver
/// (<c>SdkResolver.RegisterPrecompiled</c>) keyed by the module version id of the exact file it came from, and</item>
/// <item>the list of assemblies (task/resolver assemblies plus their dependency closure) to reference.</item>
/// </list>
/// An assembly whose dependency closure cannot be found in the layout is left out, so at run time it is loaded
/// out of process exactly like any task assembly that is not part of the SDK.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Task assemblies deliberately left out of the image even though they would compile, with the reason. They still
    /// work: a UsingTask naming them runs out of process like any other task assembly.
    /// </summary>
    private static readonly Dictionary<string, string> s_excludedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        // Package validation only; its dependency closure is the Roslyn compiler, which would dwarf the rest of the image.
        ["Microsoft.DotNet.ApiCompat.Task"] = "pulls the Roslyn compiler into the image; used only for package validation",
    };

    /// <summary>
    /// Dependencies that are compiled only as far as the compiler can see them used, instead of whole. Every other
    /// dependency is rooted because SDK task code commonly reflects over library types (JSON serialization of NuGet and
    /// SDK models). These are large, reached only through statically visible calls, and do not reflect over themselves.
    /// </summary>
    private static readonly string[] s_unrootedDependencies =
    [
        "Microsoft.CodeAnalysis",               // Roslyn: reached from Microsoft.NET.Build.Tasks only through package validation
        "Microsoft.DotNet.ApiCompat",           // package validation (ApiCompat.Task, ApiCompatibility)
        "Microsoft.DotNet.ApiSymbolExtensions", // package validation
        "Microsoft.DotNet.PackageValidation",   // package validation
        "FSharp.Core",                          // F# runtime library: huge when compiled whole; FSharp.Build calls it statically
        "System.CommandLine",
        "System.Reflection.MetadataLoadContext",
        "System.Security.Cryptography",
        "System.Configuration.ConfigurationManager",
        "System.Diagnostics.EventLog",
        "System.Formats.Nrbf",
        "System.Resources.Extensions",
    ];

    /// <summary>
    /// The engine assemblies whose dependencies are taken from the layout.
    /// </summary>
    private static readonly string[] s_engineAssemblyFiles =
    [
        "MSBuild.dll",
        "Microsoft.Build.dll",
        "Microsoft.Build.Framework.dll",
        "Microsoft.Build.Tasks.Core.dll",
        "Microsoft.Build.Utilities.Core.dll",
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("usage: TaskCatalogGenerator <sdk directory> <shared framework directory> <output .cs> <output references file>");
            return 1;
        }

        string sdkDirectory = Path.GetFullPath(args[0]);
        string frameworkDirectory = Path.GetFullPath(args[1]);
        string outputSource = args[2];
        string outputReferences = args[3];

        HashSet<string> frameworkAssemblies = new(
            Directory.EnumerateFiles(frameworkDirectory, "*.dll").Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.OrdinalIgnoreCase);

        Layout layout = Layout.Scan(sdkDirectory, frameworkAssemblies);
        string[] runtimeAssemblies = Directory.GetFiles(frameworkDirectory, "*.dll");

        List<CatalogAssembly> catalog = [];
        StringBuilder report = new();

        // MSBuild's own task assembly is built from source into the host; a JIT MSBuild binds it by name, whatever file a
        // UsingTask names, so its tasks are registered to match by name (Guid.Empty).
        if (layout.TryGetFile(Path.Combine(sdkDirectory, "Microsoft.Build.Tasks.Core.dll"), out AssemblyFile? engineTasks)
            && Inspect(engineTasks, layout, runtimeAssemblies, report, isEngine: true) is CatalogAssembly engineEntry)
        {
            catalog.Add(engineEntry);
        }

        // Task assemblies: any assembly in the layout with a public, constructible ITask class.
        foreach (AssemblyFile candidate in layout.TaskAssemblyCandidates())
        {
            CatalogAssembly? entry = Inspect(candidate, layout, runtimeAssemblies, report);
            if (entry is not null)
            {
                catalog.Add(entry);
            }
        }

        // SDK resolvers: the assemblies the SdkResolvers manifests point at.
        foreach (string resolverPath in layout.ResolverManifestAssemblies())
        {
            if (layout.TryGetFile(resolverPath, out AssemblyFile? resolverFile)
                && !catalog.Any(c => c.File.Path.Equals(resolverFile.Path, StringComparison.Ordinal)))
            {
                CatalogAssembly? entry = Inspect(resolverFile, layout, runtimeAssemblies, report);
                if (entry is not null && !catalog.Any(c => c.File.Name.Equals(entry.File.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    catalog.Add(entry);
                }
            }
        }

        catalog = SelectOneBuildPerAssemblyName(catalog, report);

        // The engine itself is built from source, but some of its dependencies are private package references that do not
        // flow to this project (for example Microsoft.VisualStudio.SolutionPersistence for .slnx). A JIT MSBuild loads them
        // from the SDK directory, so take the same files from the layout.
        List<AssemblyFile> engineDependencies = [];
        foreach (string engineAssembly in s_engineAssemblyFiles)
        {
            if (layout.TryGetFile(Path.Combine(sdkDirectory, engineAssembly), out AssemblyFile? engineFile))
            {
                if (layout.TryGetClosure(engineFile, out List<AssemblyFile> engineClosure, out string? missingDependency))
                {
                    engineDependencies.AddRange(engineClosure.Where(f => !f.Path.Equals(engineFile.Path, StringComparison.Ordinal)));
                }
                else
                {
                    report.AppendLine($"WARN {engineFile.Path}: dependency '{missingDependency}' is not in the layout.");
                }
            }
        }

        // The union of all dependency closures, one file per assembly name.
        Dictionary<string, AssemblyFile> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (AssemblyFile dependency in catalog.SelectMany(c => c.Closure).Concat(engineDependencies))
        {
            if (!references.TryGetValue(dependency.Name, out AssemblyFile? existing) || dependency.Version > existing.Version)
            {
                references[dependency.Name] = dependency;
            }
        }

        WriteReferences(outputReferences, catalog, references);
        WriteSource(outputSource, catalog);
        File.WriteAllText(Path.ChangeExtension(outputReferences, ".report.txt"), report.ToString());

        Console.WriteLine($"TaskCatalogGenerator: {catalog.Sum(c => c.Tasks.Count)} tasks and {catalog.Sum(c => c.Resolvers.Count)} SDK resolvers from {catalog.Count} assemblies; {references.Count} referenced assemblies.");
        return 0;
    }

    /// <summary>
    /// Reflects (metadata only) over one assembly and, if it has tasks or resolvers and its dependency closure is
    /// complete, returns its catalog entry.
    /// </summary>
    private static CatalogAssembly? Inspect(AssemblyFile file, Layout layout, string[] runtimeAssemblies, StringBuilder report, bool isEngine = false)
    {
        List<AssemblyFile> closure = [];
        string? missing = null;
        if (!isEngine && !layout.TryGetClosure(file, out closure, out missing))
        {
            report.AppendLine($"SKIP {file.Path}: dependency '{missing}' is not in the layout (runs out of process instead).");
            return null;
        }

        if (s_excludedAssemblies.TryGetValue(file.Name, out string? reason))
        {
            report.AppendLine($"SKIP {file.Path}: {reason} (runs out of process instead).");
            return null;
        }

        // One context per inspected assembly so that same-named assemblies elsewhere in the layout cannot collide.
        string[] contextAssemblies = [.. runtimeAssemblies, .. closure.Select(c => c.Path), .. layout.EngineAssemblyPaths, file.Path];
        using MetadataLoadContext context = new(new PathAssemblyResolver(contextAssemblies));
        Assembly assembly = context.LoadFromAssemblyPath(file.Path);

        // A .NET Framework build of a task assembly (for example Roslyn's binfx copy) is for MSBuild.exe, not this runtime.
        string? targetFramework = assembly.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute")?
            .ConstructorArguments.FirstOrDefault().Value as string;
        if (targetFramework?.StartsWith(".NETFramework", StringComparison.Ordinal) == true)
        {
            report.AppendLine($"SKIP {file.Path}: built for {targetFramework}.");
            return null;
        }

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = [.. e.Types.Where(t => t is not null)!];
        }

        List<string> tasks = [];
        List<string> resolvers = [];
        foreach (Type type in types)
        {
            try
            {
                if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
                {
                    continue;
                }

                // Matches TypeLoader.IsDesiredType for tasks, plus constructibility (a parameterless or TaskEnvironment
                // constructor), which in-proc task creation needs as well.
                if (type.GetInterface("Microsoft.Build.Framework.ITask") is not null && IsConstructible(type, allowTaskEnvironment: true))
                {
                    tasks.Add(type.FullName!);
                }
                else if (DerivesFrom(type, "Microsoft.Build.Framework.SdkResolver") && IsConstructible(type, allowTaskEnvironment: false))
                {
                    resolvers.Add(type.FullName!);
                }
            }
            catch (Exception e) when (e is FileNotFoundException or TypeLoadException)
            {
                report.AppendLine($"  type {type.FullName} in {file.Name} skipped: {e.Message}");
            }
        }

        if (tasks.Count == 0 && resolvers.Count == 0)
        {
            return null;
        }

        report.AppendLine($"ADD  {file.Path}: {tasks.Count} tasks, {resolvers.Count} resolvers, {closure.Count} assemblies in closure.");
        return new CatalogAssembly(file, tasks, resolvers, closure, isEngine);

        static bool IsConstructible(Type type, bool allowTaskEnvironment)
            => type.GetConstructors().Any(c =>
            {
                ParameterInfo[] parameters = c.GetParameters();
                return parameters.Length == 0
                    || (allowTaskEnvironment && parameters.Length == 1 && parameters[0].ParameterType.FullName == "Microsoft.Build.Framework.TaskEnvironment");
            });

        static bool DerivesFrom(Type type, string baseTypeFullName)
        {
            for (Type? t = type.BaseType; t is not null; t = t.BaseType)
            {
                if (t.FullName == baseTypeFullName)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The registry holds one build (module version id) per assembly name. When a layout ships different builds of
    /// the same task assembly in several places, compile in the newest; the others are honored out of process.
    /// </summary>
    private static List<CatalogAssembly> SelectOneBuildPerAssemblyName(List<CatalogAssembly> catalog, StringBuilder report)
    {
        List<CatalogAssembly> selected = [];
        foreach (IGrouping<string, CatalogAssembly> group in catalog.GroupBy(c => c.File.Name, StringComparer.OrdinalIgnoreCase))
        {
            CatalogAssembly[] builds = [.. group.DistinctBy(c => c.File.ModuleVersionId).OrderByDescending(c => c.File.Version).ThenBy(c => c.File.Path.Length)];
            selected.Add(builds[0]);
            foreach (CatalogAssembly other in builds.Skip(1))
            {
                report.AppendLine($"DUP  {other.File.Path}: a different build than {builds[0].File.Path} (runs out of process instead).");
            }
        }

        return selected;
    }

    private static void WriteReferences(string path, List<CatalogAssembly> catalog, Dictionary<string, AssemblyFile> references)
    {
        Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < catalog.Count; i++)
        {
            if (!catalog[i].IsEngine)
            {
                aliases[catalog[i].File.Name] = Alias(i);
            }
        }

        // One line per reference: <path>;<extern alias or empty>;<"root" to compile the whole assembly, or empty>.
        IEnumerable<string> lines = references.Values
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r =>
            {
                bool isCatalogAssembly = aliases.TryGetValue(r.Name, out string? alias);
                bool root = isCatalogAssembly || !s_unrootedDependencies.Any(prefix => r.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                return $"{r.Path};{alias ?? string.Empty};{(root ? "root" : string.Empty)}";
            });
        WriteIfChanged(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static void WriteSource(string path, List<CatalogAssembly> catalog)
    {
        StringBuilder source = new();
        source.AppendLine("// <auto-generated>Generated by TaskCatalogGenerator from the SDK layout this MSBuild was built for.</auto-generated>");
        for (int i = 0; i < catalog.Count; i++)
        {
            if (!catalog[i].IsEngine)
            {
                source.AppendLine($"extern alias {Alias(i)};");
            }
        }

        source.AppendLine();
        source.AppendLine("namespace Microsoft.Build.Aot;");
        source.AppendLine();
        source.AppendLine("internal static partial class PrecompiledToolset");
        source.AppendLine("{");
        source.AppendLine("    static partial void RegisterGenerated()");
        source.AppendLine("    {");
        for (int i = 0; i < catalog.Count; i++)
        {
            CatalogAssembly entry = catalog[i];
            string alias = entry.IsEngine ? "global" : Alias(i);
            string mvid = $"m{i}";
            source.AppendLine($"        // {entry.File.Name} {entry.File.Version}");
            source.AppendLine($"        global::System.Guid {mvid} = new(\"{(entry.IsEngine ? Guid.Empty : entry.File.ModuleVersionId)}\");");
            foreach (string task in entry.Tasks)
            {
                source.AppendLine($"        global::Microsoft.Build.Utilities.Task.RegisterPrecompiledTask<{alias}::{CSharpName(task)}>({mvid});");
            }

            foreach (string resolver in entry.Resolvers)
            {
                source.AppendLine($"        RegisterResolver(new {alias}::{CSharpName(resolver)}(), \"{entry.File.Name}\", {mvid});");
            }
        }

        source.AppendLine("    }");
        source.AppendLine("}");
        WriteIfChanged(path, source.ToString());

        // Nested types are written with '+' in metadata names.
        static string CSharpName(string fullName) => fullName.Replace('+', '.');
    }

    private static string Alias(int index) => $"pc{index}";

    private static void WriteIfChanged(string path, string contents)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, contents);
        }
    }

    private sealed record CatalogAssembly(AssemblyFile File, List<string> Tasks, List<string> Resolvers, List<AssemblyFile> Closure, bool IsEngine);
}

/// <summary>
/// A managed assembly file in the SDK layout.
/// </summary>
internal sealed record AssemblyFile(string Path, string Name, Version Version, Guid ModuleVersionId, string[] References)
{
    internal static AssemblyFile? TryRead(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            MetadataReader reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            AssemblyDefinition definition = reader.GetAssemblyDefinition();
            string[] references = [.. reader.AssemblyReferences.Select(r => reader.GetString(reader.GetAssemblyReference(r).Name))];
            return new AssemblyFile(
                path,
                reader.GetString(definition.Name),
                definition.Version,
                reader.GetGuid(reader.GetModuleDefinition().Mvid),
                references);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The managed assemblies of an SDK layout, and how a task assembly's references bind within it.
/// </summary>
internal sealed class Layout
{
    private readonly string _sdkDirectory;
    private readonly HashSet<string> _frameworkAssemblies;
    private readonly Dictionary<string, AssemblyFile> _filesByPath;
    private readonly Dictionary<string, List<AssemblyFile>> _filesByName;

    private Layout(string sdkDirectory, HashSet<string> frameworkAssemblies, List<AssemblyFile> files)
    {
        _sdkDirectory = sdkDirectory;
        _frameworkAssemblies = frameworkAssemblies;
        _filesByPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        _filesByName = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        EngineAssemblyPaths = [.. files.Where(f => IsEngineAssembly(f.Name) && System.IO.Path.GetDirectoryName(f.Path) == sdkDirectory).Select(f => f.Path)];
    }

    /// <summary>
    /// The SDK's copies of the MSBuild engine assemblies, used only to resolve metadata.
    /// </summary>
    internal string[] EngineAssemblyPaths { get; }

    internal static Layout Scan(string sdkDirectory, HashSet<string> frameworkAssemblies)
    {
        List<AssemblyFile> files = [];
        foreach (string path in Directory.EnumerateFiles(sdkDirectory, "*.dll", SearchOption.AllDirectories))
        {
            // .NET Framework builds (tools/net472/...) and satellite resource assemblies never run on this runtime.
            string relative = System.IO.Path.GetRelativePath(sdkDirectory, path);
            if (relative.Split(System.IO.Path.DirectorySeparatorChar).Any(IsNetFrameworkDirectory)
                || path.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AssemblyFile.TryRead(path) is AssemblyFile file)
            {
                files.Add(file);
            }
        }

        return new Layout(sdkDirectory, frameworkAssemblies, files);

        static bool IsNetFrameworkDirectory(string segment)
            => segment.StartsWith("net4", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("net3", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("net2", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("netframework", StringComparison.OrdinalIgnoreCase);
    }

    internal bool TryGetFile(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AssemblyFile? file)
        => _filesByPath.TryGetValue(System.IO.Path.GetFullPath(path), out file);

    /// <summary>
    /// Assemblies that reference Microsoft.Build.Framework and are not the engine itself - the only ones that can
    /// declare task classes.
    /// </summary>
    internal IEnumerable<AssemblyFile> TaskAssemblyCandidates()
        => _filesByPath.Values
            .Where(f => !IsEngineAssembly(f.Name) && f.References.Contains("Microsoft.Build.Framework", StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Path, StringComparer.Ordinal);

    /// <summary>
    /// The resolver assemblies named by the layout's SdkResolvers manifests.
    /// </summary>
    internal IEnumerable<string> ResolverManifestAssemblies()
    {
        string resolversDirectory = System.IO.Path.Combine(_sdkDirectory, "SdkResolvers");
        if (!Directory.Exists(resolversDirectory))
        {
            yield break;
        }

        foreach (string manifest in Directory.EnumerateFiles(resolversDirectory, "*.xml", SearchOption.AllDirectories))
        {
            string? relativePath = System.Xml.Linq.XDocument.Load(manifest).Root?.Element("Path")?.Value;
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                yield return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(manifest)!, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            }
        }
    }

    /// <summary>
    /// Resolves the transitive references of <paramref name="root"/> within the layout. A reference binds to the
    /// shared framework or the engine when it is one of their assemblies; otherwise to the copy next to the referencing
    /// assembly, else the copy in the SDK directory, else the newest copy anywhere in the layout.
    /// </summary>
    internal bool TryGetClosure(AssemblyFile root, out List<AssemblyFile> closure, out string? missing)
    {
        closure = [];
        missing = null;
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { root.Name };
        Queue<AssemblyFile> queue = new([root]);
        while (queue.Count > 0)
        {
            AssemblyFile current = queue.Dequeue();
            closure.Add(current);
            foreach (string reference in current.References)
            {
                if (!visited.Add(reference) || _frameworkAssemblies.Contains(reference) || IsEngineAssembly(reference))
                {
                    continue;
                }

                if (!_filesByName.TryGetValue(reference, out List<AssemblyFile>? candidates))
                {
                    missing = reference;
                    return false;
                }

                string currentDirectory = System.IO.Path.GetDirectoryName(current.Path)!;
                AssemblyFile dependency =
                    candidates.FirstOrDefault(c => System.IO.Path.GetDirectoryName(c.Path) == currentDirectory)
                    ?? candidates.FirstOrDefault(c => System.IO.Path.GetDirectoryName(c.Path) == _sdkDirectory)
                    ?? candidates.OrderByDescending(c => c.Version).First();
                queue.Enqueue(dependency);
            }
        }

        return true;
    }

    private static bool IsEngineAssembly(string name) => name is not null && s_engineAssemblyNames.Contains(name);

    private static readonly HashSet<string> s_engineAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.Build",
        "Microsoft.Build.Framework",
        "Microsoft.Build.Tasks.Core",
        "Microsoft.Build.Utilities.Core",
        "Microsoft.NET.StringTools",
        "MSBuild",
    };
}
