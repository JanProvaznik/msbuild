// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;

// Run using the tested SDK's MSBuild.deps.json and MSBuild.runtimeconfig.json.
// This is an experiment, not a general-purpose MSBuild up-to-date check.
return args switch
{
    ["analyze", var binlog, var output] => Analyze(binlog, output),
    ["gate", var configuration] => Gate(configuration),
    _ => throw new ArgumentException("Expected analyze <binlog> <json>, or gate <config.json>."),
};

static int Analyze(string binlog, string output)
{
    var replay = new BinaryLogReplayEventSource();
    var tasks = new Dictionary<string, Timing>(StringComparer.Ordinal);
    var targets = new Dictionary<string, Timing>(StringComparer.Ordinal);
    var taskStarts = new Dictionary<(int, int, int), TaskStartedEventArgs>();
    var targetStarts = new Dictionary<(int, int, int), TargetStartedEventArgs>();
    var evaluationStarts = new Dictionary<(int, int), DateTime>();
    var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    double evaluationMs = 0;
    int evaluations = 0, skippedTargets = 0, errors = 0, warnings = 0;
    DateTime start = default, finish = default;
    bool succeeded = false;
    replay.BuildStarted += (_, e) => start = e.Timestamp;
    replay.BuildFinished += (_, e) => { finish = e.Timestamp; succeeded = e.Succeeded; };
    replay.ProjectStarted += (_, e) => projects.Add(e.ProjectFile ?? "");
    replay.ErrorRaised += (_, _) => errors++;
    replay.WarningRaised += (_, _) => warnings++;
    replay.AnyEventRaised += (_, e) =>
    {
        if (e is ProjectEvaluationStartedEventArgs)
        {
            evaluationStarts[(e.BuildEventContext!.NodeId, e.BuildEventContext.EvaluationId)] = e.Timestamp;
        }
        else if (e is ProjectEvaluationFinishedEventArgs)
        {
            evaluations++;
            evaluationMs += (e.Timestamp - evaluationStarts[(e.BuildEventContext!.NodeId, e.BuildEventContext.EvaluationId)]).TotalMilliseconds;
        }
        else if (e is TargetSkippedEventArgs)
        {
            skippedTargets++;
        }
    };
    replay.TaskStarted += (_, e) =>
    {
        var c = e.BuildEventContext!;
        taskStarts.Add((c.NodeId, c.ProjectContextId, c.TaskId), e);
    };
    replay.TaskFinished += (_, e) =>
    {
        var c = e.BuildEventContext!;
        var begin = taskStarts[(c.NodeId, c.ProjectContextId, c.TaskId)];
        Add(tasks, e.TaskName!, (e.Timestamp - begin.Timestamp).TotalMilliseconds);
    };
    replay.TargetStarted += (_, e) =>
    {
        var c = e.BuildEventContext!;
        targetStarts.Add((c.NodeId, c.ProjectContextId, c.TargetId), e);
    };
    replay.TargetFinished += (_, e) =>
    {
        var c = e.BuildEventContext!;
        var begin = targetStarts[(c.NodeId, c.ProjectContextId, c.TargetId)];
        Add(targets, e.TargetName!, (e.Timestamp - begin.Timestamp).TotalMilliseconds);
    };
    replay.Replay(binlog);
    var result = new
    {
        binlog, succeeded, errors, warnings,
        buildSeconds = (finish - start).TotalSeconds,
        projects = projects.Count, evaluations, evaluationCumulativeSeconds = evaluationMs / 1000,
        tasks = tasks.Values.Sum(t => t.Count), targets = targets.Values.Sum(t => t.Count), skippedTargets,
        nonOrchestrationTaskSeconds = tasks.Where(t => t.Key is not ("MSBuild" or "CallTarget")).Sum(t => t.Value.Milliseconds) / 1000,
        taskTimings = tasks.OrderByDescending(t => t.Value.Milliseconds).Select(t => new { name = t.Key, t.Value.Count, t.Value.Milliseconds }),
        targetTimings = targets.OrderByDescending(t => t.Value.Milliseconds).Select(t => new { name = t.Key, t.Value.Count, t.Value.Milliseconds }),
    };
    File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { result.succeeded, result.buildSeconds, result.projects, result.evaluations, result.evaluationCumulativeSeconds, result.tasks, result.targets, result.skippedTargets, result.nonOrchestrationTaskSeconds }));
    return succeeded ? 0 : 1;
}

static void Add(Dictionary<string, Timing> timings, string name, double milliseconds)
{
    if (!timings.TryGetValue(name, out var timing))
    {
        timing = new Timing();
        timings.Add(name, timing);
    }
    timing.Count++;
    timing.Milliseconds += milliseconds;
}

static int Gate(string configuration)
{
    var config = JsonSerializer.Deserialize<GateConfig>(File.ReadAllText(configuration))
        ?? throw new ArgumentException("Gate configuration cannot be null.");
    if (!config.AssumePureFileBuild)
    {
        throw new InvalidOperationException("The experimental gate requires explicit AssumePureFileBuild consent. Arbitrary tasks can depend on undeclared external state.");
    }
    config.Validate();
    var total = Stopwatch.StartNew();
    using var gateLock = new FileStream(config.StateFile + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    Directory.SetCurrentDirectory(config.Root);
    var sw = Stopwatch.StartNew();
    // This deliberately reevaluates the complete real graph, including globs and imports, on every invocation.
    int exitCode = Run(config, config.EvaluationBinlog is null
        ? ["-graphBuild:NoBuild"]
        : ["-graphBuild:NoBuild", "-bl:" + config.EvaluationBinlog]);
    double evaluationSeconds = sw.Elapsed.TotalSeconds;
    if (exitCode != 0)
    {
        File.Delete(config.StateFile);
        return exitCode;
    }
    sw.Restart();
    string key = ConfigurationKey(config);
    GateState? previous = File.Exists(config.StateFile)
        ? JsonSerializer.Deserialize<GateState>(File.ReadAllText(config.StateFile))
        : null;
    double manifestReadSeconds = sw.Elapsed.TotalSeconds;
    string[] packageRoots = previous?.PackageRoots ?? [];
    var current = Capture(config, packageRoots);
    if (previous is null)
    {
        packageRoots = DiscoverPackages(config);
        current = Capture(config, packageRoots);
    }
    double fileScanSeconds = sw.Elapsed.TotalSeconds - manifestReadSeconds;
    string? difference = previous is null ? "No successful baseline"
        : previous.Key != key ? "Configuration, environment, or probe changed"
        : Difference(previous.Files, current);
    double snapshotSeconds = sw.Elapsed.TotalSeconds;
    bool hit = difference is null;
    double buildSeconds = 0;
    if (!hit)
    {
        var inputsBefore = Inputs(config, current);
        Console.WriteLine($"Gate miss: {difference}");
        // Never leave a successful-looking cache entry after an interrupted or failed build.
        File.Delete(config.StateFile);
        sw.Restart();
        exitCode = Run(config, []);
        buildSeconds = sw.Elapsed.TotalSeconds;
        if (exitCode == 0)
        {
            packageRoots = DiscoverPackages(config);
            var after = Capture(config, packageRoots);
            string? changedDuringBuild = Difference(inputsBefore, Inputs(config, after));
            if (changedDuringBuild is not null)
            {
                Console.WriteLine($"Not caching: an input changed during the build: {changedDuringBuild}");
            }
            else
            {
                string temporary = config.StateFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new GateState(key, after, packageRoots)));
                File.Move(temporary, config.StateFile, overwrite: true);
            }
        }
    }
    var result = new
    {
        timestamp = DateTimeOffset.UtcNow, scenario = "experimental-file-gate",
        cacheHit = hit, reason = difference, files = current.Count,
        evaluationSeconds, snapshotSeconds, manifestReadSeconds, fileScanSeconds, buildSeconds,
        seconds = total.Elapsed.TotalSeconds, exitCode,
        // A hit ran only graph construction; it didn't invoke a target or task.
        tasksOnHit = hit ? 0 : (int?)null,
    };
    string json = JsonSerializer.Serialize(result);
    File.AppendAllText(config.ResultsFile, json + Environment.NewLine);
    Console.WriteLine(json);
    return exitCode;
}

static int Run(GateConfig config, string[] extra)
{
    var start = new ProcessStartInfo(config.Dotnet) { UseShellExecute = false };
    start.Environment["DOTNET_HOST_PATH"] = config.Dotnet;
    start.ArgumentList.Add(Path.Combine(config.Sdk, "MSBuild.dll"));
    start.ArgumentList.Add(config.Project);
    start.ArgumentList.Add("-nologo");
    start.ArgumentList.Add("-v:q");
    start.ArgumentList.Add("-m:8");
    foreach (string property in config.Properties)
    {
        start.ArgumentList.Add("-p:" + property);
    }
    foreach (string argument in extra)
    {
        start.ArgumentList.Add(argument);
    }
    if (extra.Length == 0 && config.MissBinlog is not null)
    {
        start.ArgumentList.Add("-bl:" + config.MissBinlog);
    }
    using var process = Process.Start(start)!;
    process.WaitForExit();
    return process.ExitCode;
}

static string[] DiscoverPackages(GateConfig config)
{
    var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (string assets in Directory.EnumerateFiles(config.Root, "project.assets.json", SearchOption.AllDirectories))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assets));
        var packageFolders = doc.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name).ToArray();
        foreach (var library in doc.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
            {
                continue;
            }
            string package = library.Value.GetProperty("path").GetString()!;
            string? directory = packageFolders.Select(p => Path.GetFullPath(Path.Combine(p, package))).FirstOrDefault(Directory.Exists);
            if (directory is null)
            {
                throw new DirectoryNotFoundException($"Referenced package is missing: {package}");
            }
            roots.Add(directory);
        }
    }
    return roots.Order(StringComparer.OrdinalIgnoreCase).ToArray();
}

static Dictionary<string, FileStamp> Capture(GateConfig config, string[] packageRoots)
{
    var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
    Scan(config.Root);
    // Asset files themselves are in the workspace snapshot. Rediscover the package closure on a miss.
    // Every hit still enumerates all files in each previously referenced package directory.
    Scan(config.Sdk);
    foreach (string root in config.ExternalRoots.Concat(packageRoots))
    {
        Scan(root);
    }
    return result;

    void Scan(string root)
    {
        string marker = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (result.ContainsKey(marker) || !Directory.Exists(root))
        {
            return;
        }
        var directory = new DirectoryInfo(root);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new NotSupportedException($"Reparse points need an explicit tracking contract: {root}");
        }
        result.Add(marker, new FileStamp(0, 0));
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new NotSupportedException($"Reparse points need an explicit tracking contract: {child.FullName}");
            }
            if (child is DirectoryInfo subdirectory)
            {
                Scan(subdirectory.FullName);
            }
            else
            {
                var file = (FileInfo)child;
                result.Add(file.FullName, new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks));
            }
        }
    }
}

static Dictionary<string, FileStamp> Inputs(GateConfig config, Dictionary<string, FileStamp> snapshot)
{
    var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
    string workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Root)) + Path.DirectorySeparatorChar;
    foreach (var entry in snapshot)
    {
        string path = entry.Key;
        string relative = Path.GetRelativePath(workspace, path);
        bool generated = path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) &&
            relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");
        if (!generated || Path.GetFileName(path) == "project.assets.json" ||
            path.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(path, entry.Value);
        }
    }
    return result;
}

static string ConfigurationKey(GateConfig config)
{
    var variables = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
        .OrderBy(e => (string)e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}");
    string value = JsonSerializer.Serialize(config) + string.Join('\n', variables) +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GateConfig).Assembly.Location))) +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config.Dotnet)));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

static string? Difference(Dictionary<string, FileStamp> oldFiles, Dictionary<string, FileStamp> newFiles)
{
    foreach (var file in oldFiles)
    {
        if (!newFiles.TryGetValue(file.Key, out var stamp))
        {
            return $"Removed: {file.Key}";
        }
        if (file.Value != stamp)
        {
            return $"Changed: {file.Key}";
        }
    }
    foreach (string file in newFiles.Keys)
    {
        if (!oldFiles.ContainsKey(file))
        {
            return $"Added: {file}";
        }
    }
    return null;
}

sealed class Timing
{
    public int Count { get; set; }
    public double Milliseconds { get; set; }
}

sealed record FileStamp(long Length, long LastWriteUtcTicks);
sealed record GateState(string Key, Dictionary<string, FileStamp> Files, string[] PackageRoots);

sealed class GateConfig
{
    public required string Root { get; init; }
    public required string Project { get; init; }
    public required string Dotnet { get; init; }
    public required string Sdk { get; init; }
    public required string StateFile { get; init; }
    public required string ResultsFile { get; init; }
    public string? MissBinlog { get; init; }
    public string? EvaluationBinlog { get; init; }
    public bool AssumePureFileBuild { get; init; }
    public string[] Properties { get; init; } = [];
    public string[] ExternalRoots { get; init; } = [];

    public void Validate()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This experiment's path identity is Windows-specific.");
        }
        foreach (string path in new[] { Root, Sdk }.Concat(ExternalRoots))
        {
            if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            {
                throw new ArgumentException($"Expected an existing absolute directory: {path}");
            }
        }
        foreach (string path in new[] { StateFile, ResultsFile }
            .Concat(MissBinlog is null ? [] : new[] { MissBinlog })
            .Concat(EvaluationBinlog is null ? [] : new[] { EvaluationBinlog }))
        {
            if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("State and result files must be absolute and outside the observed workspace.");
            }
        }
        if (!File.Exists(Dotnet) || !File.Exists(Path.Combine(Sdk, "MSBuild.dll")) || !File.Exists(Path.Combine(Root, Project)))
        {
            throw new ArgumentException("Dotnet, SDK, and project must exist.");
        }
    }
}
