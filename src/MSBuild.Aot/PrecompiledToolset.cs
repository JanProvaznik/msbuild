// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Build.Aot;

/// <summary>
/// Registers the task and SDK-resolver classes compiled into this executable, and supplies the environment a JIT
/// MSBuild gets from its <c>dotnet</c> host.
/// </summary>
internal static partial class PrecompiledToolset
{
    /// <remarks>
    /// MSBuild's own task factories are found by name in Microsoft.Build.Tasks.Core and constructed by the engine, so
    /// they are kept explicitly; the rest of that assembly is trimmed normally (its task classes are rooted by their
    /// registrations).
    /// </remarks>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties, typeof(Microsoft.Build.Tasks.RoslynCodeTaskFactory))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties, "Microsoft.Build.Tasks.CodeTaskFactory", "Microsoft.Build.Tasks.Core")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties, "Microsoft.Build.Tasks.XamlTaskFactory", "Microsoft.Build.Tasks.Core")]
    internal static void Initialize()
    {
        // Generated from the SDK layout by TaskCatalogGenerator: one registration per task/resolver class, keyed by
        // the module version id of the file it was compiled from.
        RegisterGenerated();

        PublishSdkRoot();
        EnsureDotnetHostPath();
    }

    /// <summary>
    /// SDK components locate the SDK directory from their own assembly location, which is empty in a Native AOT
    /// image. They honor the <c>Microsoft.DotNet.Sdk.Root</c> AppContext value (the contract an AOT <c>dotnet</c> host
    /// uses) first; this executable lives in that directory.
    /// </summary>
    private static void PublishSdkRoot()
    {
        const string SdkRootDataName = "Microsoft.DotNet.Sdk.Root";
        if (AppContext.GetData(SdkRootDataName) is null && Path.GetDirectoryName(Environment.ProcessPath) is string sdkDirectory)
        {
            AppContext.SetData(SdkRootDataName, sdkDirectory);
        }
    }

    static partial void RegisterGenerated();

    /// <summary>
    /// Registers a compiled-in SDK resolver to stand in for the manifest naming <paramref name="assemblyName"/>,
    /// adapting it first if that build of the resolver cannot run unmodified in a Native AOT process.
    /// </summary>
    private static void RegisterResolver(Microsoft.Build.Framework.SdkResolver resolver, string assemblyName, Guid moduleVersionId)
    {
        string sdkDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        Microsoft.Build.Framework.SdkResolver.RegisterPrecompiled(WorkloadSdkResolverAdapter.AdaptIfNeeded(resolver, sdkDirectory), assemblyName, moduleVersionId);
    }

    /// <summary>
    /// Tasks that launch managed tools (Csc, the .NET task host, ...) find the <c>dotnet</c> host through
    /// <c>DOTNET_HOST_PATH</c>, falling back to the current process when it is <c>dotnet</c>. A JIT MSBuild always
    /// runs under <c>dotnet</c>; this process does not, so point them at the host that owns this toolset
    /// (<c>&lt;dotnet root&gt;/sdk/&lt;version&gt;/</c> -> <c>&lt;dotnet root&gt;/dotnet</c>), as <c>dotnet build</c> does.
    /// </summary>
    private static void EnsureDotnetHostPath()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")))
        {
            return;
        }

        string? sdkDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        string? dotnetRoot = sdkDirectory is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(sdkDirectory));
        if (dotnetRoot is null)
        {
            return;
        }

        string dotnetHost = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(dotnetHost))
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", dotnetHost);
        }
    }
}
