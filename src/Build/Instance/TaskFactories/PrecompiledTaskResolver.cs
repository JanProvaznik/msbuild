// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;

namespace Microsoft.Build.BackEnd;

/// <summary>
/// Resolves the assembly and type a <c>&lt;UsingTask&gt;</c> names to a task class the host compiled in
/// (see <see cref="PrecompiledTaskRegistry"/>), without loading the assembly.
/// </summary>
internal static class PrecompiledTaskResolver
{
    /// <summary>
    /// Module version ids of task assembly files, keyed by full path and invalidated when the file's size or
    /// timestamp changes (a reused node can outlive a rebuild of a task assembly).
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, long Length, Guid ModuleVersionId)> s_moduleVersionIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Attempts to resolve <paramref name="typeName"/> in the assembly described by <paramref name="loadInfo"/> to a
    /// compiled-in task class, using the same type-name matching rules as <see cref="TypeLoader"/>.
    /// </summary>
    /// <returns><see langword="true"/> if the assembly is the compiled-in build and it contains a matching task class.</returns>
    internal static bool TryResolve(AssemblyLoadInfo loadInfo, string typeName, [NotNullWhen(true)] out LoadedType? loadedType)
    {
        loadedType = null;

        if (!PrecompiledTaskRegistry.HasRegistrations
            || !TryGetPrecompiledAssembly(loadInfo, out PrecompiledTaskAssembly? assembly)
            || !TryFindTask(assembly, typeName, out PrecompiledTask? task))
        {
            return false;
        }

        loadedType = new LoadedType(task.TaskType, loadInfo, task.TaskType.Assembly, typeof(ITaskItem));
        return true;
    }

    private static bool TryGetPrecompiledAssembly(AssemblyLoadInfo loadInfo, [NotNullWhen(true)] out PrecompiledTaskAssembly? assembly)
    {
        assembly = null;

        if (loadInfo.AssemblyFile is string assemblyFile)
        {
            // The UsingTask names a file: it is the compiled-in assembly only if it is the same build.
            if (!PrecompiledTaskRegistry.TryGetAssembly(Path.GetFileNameWithoutExtension(assemblyFile), out assembly))
            {
                return false;
            }

            return assembly.ModuleVersionId == Guid.Empty
                || (TryGetModuleVersionId(assemblyFile, out Guid moduleVersionId) && moduleVersionId == assembly.ModuleVersionId);
        }

        if (loadInfo.AssemblyName is string assemblyNameString)
        {
            // The UsingTask names an assembly identity, which is bound by name (and version, when given).
            AssemblyName assemblyName;
            try
            {
                assemblyName = new AssemblyName(assemblyNameString);
            }
            catch (Exception e) when (e is ArgumentException or FileLoadException)
            {
                return false;
            }

            return assemblyName.Name is not null
                && PrecompiledTaskRegistry.TryGetAssembly(assemblyName.Name, out assembly)
                && (assemblyName.Version is null || assemblyName.Version.Equals(assembly.Version));
        }

        return false;
    }

    private static bool TryFindTask(PrecompiledTaskAssembly assembly, string typeName, [NotNullWhen(true)] out PrecompiledTask? task)
    {
        if (assembly.TryGetByFullName(typeName, out task))
        {
            return true;
        }

        foreach (PrecompiledTask candidate in assembly.Tasks)
        {
            if (typeName.Length == 0 || TypeLoader.IsPartialTypeNameMatch(candidate.FullName, typeName))
            {
                task = candidate;
                return true;
            }
        }

        task = null;
        return false;
    }

    /// <summary>
    /// Reads the module version id of an assembly file without loading it.
    /// </summary>
    internal static bool TryGetModuleVersionId(string assemblyFile, out Guid moduleVersionId)
    {
        moduleVersionId = Guid.Empty;

        try
        {
            string fullPath = Path.GetFullPath(assemblyFile);
            FileInfo file = new(fullPath);
            if (!file.Exists)
            {
                return false;
            }

            DateTime lastWriteTimeUtc = file.LastWriteTimeUtc;
            long length = file.Length;
            if (s_moduleVersionIds.TryGetValue(fullPath, out var cached) && cached.LastWriteTimeUtc == lastWriteTimeUtc && cached.Length == length)
            {
                moduleVersionId = cached.ModuleVersionId;
                return true;
            }

            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
            {
                return false;
            }

            MetadataReader metadataReader = peReader.GetMetadataReader();
            moduleVersionId = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);
            s_moduleVersionIds[fullPath] = (lastWriteTimeUtc, length, moduleVersionId);
            return true;
        }
        catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e) || e is BadImageFormatException)
        {
            return false;
        }
    }
}
