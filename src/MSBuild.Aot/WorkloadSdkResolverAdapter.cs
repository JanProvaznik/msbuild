// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Aot;

/// <summary>
/// Runs the SDK's workload resolver (<c>Microsoft.NET.Sdk.WorkloadMSBuildSdkResolver.WorkloadSdkResolver</c>) for a
/// Native AOT process.
/// </summary>
/// <remarks>
/// <para>
/// Some builds of that resolver find the SDK directory only from <c>typeof(DotnetFiles).Assembly.Location</c>, which is
/// always empty in a Native AOT image, so they resolve nothing. (Newer builds read the <c>Microsoft.DotNet.Sdk.Root</c>
/// AppContext value, which <see cref="PrecompiledToolset"/> publishes, and need no adapter.) This adapter performs the
/// resolver's own steps - the same internal <c>CachingWorkloadResolver</c>, user profile directory and global.json
/// lookup - with the SDK directory this executable lives in.
/// </para>
/// <para>
/// It binds to those internals by name over the compiled-in (whole-rooted) resolver assembly, and is used only when
/// every expected member exists with the expected shape; otherwise the original resolver is registered unchanged.
/// </para>
/// </remarks>
internal sealed class WorkloadSdkResolverAdapter : SdkResolver
{
    internal const string ResolverTypeName = "Microsoft.NET.Sdk.WorkloadMSBuildSdkResolver.WorkloadSdkResolver";

    private readonly SdkResolver _resolver;
    private readonly string _sdkDirectory;
    private readonly Func<object> _createCachingResolver;
    private readonly MethodInfo _resolve;
    private readonly MethodInfo _toSdkResult;
    private readonly Func<string?> _getUserProfileDirectory;
    private readonly MethodInfo _getGlobalJsonPath;

    private WorkloadSdkResolverAdapter(SdkResolver resolver, string sdkDirectory, Func<object> createCachingResolver, MethodInfo resolve, MethodInfo toSdkResult, Func<string?> getUserProfileDirectory, MethodInfo getGlobalJsonPath)
    {
        _resolver = resolver;
        _sdkDirectory = sdkDirectory;
        _createCachingResolver = createCachingResolver;
        _resolve = resolve;
        _toSdkResult = toSdkResult;
        _getUserProfileDirectory = getUserProfileDirectory;
        _getGlobalJsonPath = getGlobalJsonPath;
    }

    public override string Name => _resolver.Name;

    public override int Priority => _resolver.Priority;

    /// <summary>
    /// Returns an adapter for <paramref name="resolver"/> if it is a workload resolver build that cannot locate the SDK
    /// under Native AOT and its internals have the expected shape; otherwise <paramref name="resolver"/> itself.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The resolver assemblies are rooted whole (TrimmerRootAssembly), so their internal members are present.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The resolver assemblies are rooted whole (TrimmerRootAssembly), so their internal members are present.")]
    internal static SdkResolver AdaptIfNeeded(SdkResolver resolver, string sdkDirectory)
    {
        Type resolverType = resolver.GetType();
        if (resolverType.FullName != ResolverTypeName)
        {
            return resolver;
        }

        Assembly resolverAssembly = resolverType.Assembly;
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        // A build that already honors the AppContext SDK root needs no adapter.
        Type? sdkPaths = resolverAssembly.GetType("Microsoft.DotNet.Cli.Utils.SdkPaths");
        if (sdkPaths is not null)
        {
            return resolver;
        }

        Type? cachingResolverType = resolverAssembly.GetType("Microsoft.NET.Sdk.WorkloadMSBuildSdkResolver.CachingWorkloadResolver");
        ConstructorInfo? cachingResolverConstructor = cachingResolverType?.GetConstructor(Any, Type.EmptyTypes);
        MethodInfo? resolve = cachingResolverType?.GetMethod("Resolve", Any, [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string)]);
        MethodInfo? toSdkResult = resolve?.ReturnType.GetMethod("ToSdkResult", Any, [typeof(SdkReference), typeof(SdkResultFactory)]);
        Type? profileCalculatorType = resolverAssembly.GetType("Microsoft.DotNet.Configurer.CliFolderPathCalculatorCore");
        MethodInfo? getUserProfile = profileCalculatorType?.GetMethod("GetDotnetUserProfileFolderPath", Any, Type.EmptyTypes);
        ConstructorInfo? profileCalculatorConstructor = profileCalculatorType?.GetConstructor(Any, Type.EmptyTypes);
        MethodInfo? getGlobalJsonPath = Type.GetType("Microsoft.NET.Sdk.WorkloadManifestReader.SdkDirectoryWorkloadManifestProvider, Microsoft.NET.Sdk.WorkloadManifestReader")?
            .GetMethod("GetGlobalJsonPath", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);

        if (cachingResolverConstructor is null || resolve is null || toSdkResult is null || getUserProfile is null || getGlobalJsonPath is null
            || (!getUserProfile.IsStatic && profileCalculatorConstructor is null))
        {
            return resolver;
        }

        // Like the resolver, read the user profile directory per resolution: a reused node re-applies each build's environment.
        string? GetUserProfileDirectory() => (string?)getUserProfile.Invoke(getUserProfile.IsStatic ? null : profileCalculatorConstructor!.Invoke(null), null);
        return new WorkloadSdkResolverAdapter(resolver, sdkDirectory, () => cachingResolverConstructor.Invoke(null), resolve, toSdkResult, GetUserProfileDirectory, getGlobalJsonPath);
    }

    public override SdkResult? Resolve(SdkReference sdkReference, SdkResolverContext resolverContext, SdkResultFactory factory)
    {
        // Mirrors WorkloadSdkResolver.Resolve: one CachingWorkloadResolver and global.json lookup per resolver context.
        if (resolverContext.State is not CachedState state)
        {
            state = new CachedState(
                _createCachingResolver(),
                Directory.GetParent(_sdkDirectory)?.Parent?.FullName,
                Path.GetFileName(_sdkDirectory),
                (string?)_getGlobalJsonPath.Invoke(null, [GetGlobalJsonStartDirectory(resolverContext)]));
            resolverContext.State = state;
        }

        if (state.DotnetRoot is null)
        {
            return null;
        }

        object? result = _resolve.Invoke(state.CachingResolver, [sdkReference.Name, state.DotnetRoot, state.SdkVersion, _getUserProfileDirectory(), state.GlobalJsonPath]);
        return result is null ? null : (SdkResult?)_toSdkResult.Invoke(result, [sdkReference, factory]);
    }

    private static string? GetGlobalJsonStartDirectory(SdkResolverContext context)
        => !string.IsNullOrWhiteSpace(context.SolutionFilePath) ? Path.GetDirectoryName(context.SolutionFilePath)
            : !string.IsNullOrWhiteSpace(context.ProjectFilePath) ? Path.GetDirectoryName(context.ProjectFilePath)
            : Environment.CurrentDirectory;

    private sealed record CachedState(object CachingResolver, string? DotnetRoot, string SdkVersion, string? GlobalJsonPath);
}
