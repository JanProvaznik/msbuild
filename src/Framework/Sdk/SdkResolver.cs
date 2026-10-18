// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace Microsoft.Build.Framework
{
    /// <summary>
    /// An abstract interface for classes that can resolve a Software Development Kit (SDK).
    /// </summary>
    public abstract class SdkResolver
    {
        /// <summary>
        /// Gets the name of the <see cref="SdkResolver"/> to be displayed in build output log.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Gets the self-described resolution priority order. MSBuild will sort resolvers
        /// by this value.
        /// </summary>
        public abstract int Priority { get; }

        /// <summary>
        /// Resolves the specified SDK reference.
        /// </summary>
        /// <param name="sdkReference">A <see cref="SdkReference" /> containing the referenced SDKs be resolved.</param>
        /// <param name="resolverContext">Context for resolving the SDK.</param>
        /// <param name="factory">Factory class to create an <see cref="SdkResult" /></param>
        /// <returns>
        /// An <see cref="SdkResult" /> containing the resolved SDKs or associated error / reason
        /// the SDK could not be resolved.  Return <see langword="null"/> if the resolver is not
        /// applicable for a particular <see cref="SdkReference"/>.
        /// </returns>
        /// <remarks>
        /// Note: You must use <see cref="SdkResultFactory"/> to return a result.
        /// </remarks>
        public abstract SdkResult? Resolve(SdkReference sdkReference,
                                          SdkResolverContext resolverContext,
                                          SdkResultFactory factory);

        /// <summary>
        /// The set of resolvers registered in-process via <see cref="Register"/>.
        /// </summary>
        private static readonly List<SdkResolver> s_registeredResolvers = new();

        /// <summary>
        /// Registers an <see cref="SdkResolver"/> to be consulted during SDK resolution by a host that runs
        /// the MSBuild engine in-process (for example the .NET SDK CLI), without MSBuild discovering and
        /// loading it from disk by reflection.
        /// </summary>
        /// <param name="resolver">The resolver instance to register.</param>
        /// <remarks>
        /// <para>
        /// This is the supported way to provide SDK resolvers in a trimmed or Native AOT host, where the
        /// on-disk <c>SdkResolvers</c> probing and reflection-based loading used for plugin resolvers are
        /// unavailable. The registered resolver is consulted on the same reflection-free code path as
        /// MSBuild's built-in resolver, so it never triggers the dynamic-loading failure (MSB4282).
        /// </para>
        /// <para>
        /// The registered resolver participates in resolution in <see cref="Priority"/> order alongside
        /// MSBuild's built-in resolver, with no assembly loading or reflection. It is consulted for every
        /// SDK reference in the process.
        /// </para>
        /// <para>
        /// Intended to be called once per resolver during host initialization, before the first project is
        /// evaluated. The set of registered resolvers is captured the first time an SDK is resolved in the
        /// process; registrations performed after that point are not guaranteed to take effect.
        /// </para>
        /// <para>
        /// This method is thread-safe. Registering the same instance more than once has no additional effect.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
        public static void Register(SdkResolver resolver)
        {
            if (resolver is null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            lock (s_registeredResolvers)
            {
                bool alreadyRegistered = false;
                foreach (SdkResolver registeredResolver in s_registeredResolvers)
                {
                    if (ReferenceEquals(registeredResolver, resolver))
                    {
                        alreadyRegistered = true;
                        break;
                    }
                }

                if (!alreadyRegistered)
                {
                    s_registeredResolvers.Add(resolver);
                }
            }
        }

        /// <summary>
        /// Gets a snapshot of the resolvers registered via <see cref="Register"/>, for the engine to fold
        /// into its reflection-free default-resolver pass.
        /// </summary>
        internal static IReadOnlyList<SdkResolver> RegisteredResolvers
        {
            get
            {
                lock (s_registeredResolvers)
                {
                    return s_registeredResolvers.Count == 0
                        ? Array.Empty<SdkResolver>()
                        : s_registeredResolvers.ToArray();
                }
            }
        }

        /// <summary>
        /// Resolvers compiled into the host, keyed by the simple name of the resolver assembly they came from.
        /// </summary>
        private static readonly Dictionary<string, (Guid ModuleVersionId, List<SdkResolver> Resolvers)> s_precompiledResolvers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers an <see cref="SdkResolver"/> that the host compiled into itself, to stand in for the resolver that
        /// MSBuild would otherwise load by reflection from an <c>SdkResolvers</c> manifest naming the same build of the
        /// resolver's assembly.
        /// </summary>
        /// <param name="resolver">The resolver instance.</param>
        /// <param name="assemblyName">
        /// The simple name of the resolver assembly (the file name, without extension, of the manifest's <c>Path</c>) that
        /// <paramref name="resolver"/> stands in for. Usually the resolver's own assembly; a host may also supply a
        /// replacement that adapts the original to its environment.
        /// </param>
        /// <param name="assemblyModuleVersionId">
        /// The module version id (MVID) of the resolver assembly build that <paramref name="resolver"/> was compiled from.
        /// The resolver is used only for a manifest whose assembly file has this MVID; a manifest naming any other file is
        /// loaded as before.
        /// </param>
        /// <remarks>
        /// <para>
        /// Unlike <see cref="Register"/>, which adds a resolver to MSBuild's built-in resolver pass, a precompiled resolver
        /// takes part in resolution exactly where its manifest does: with the manifest's <c>ResolvableSdkPattern</c>, in
        /// the same order relative to the built-in resolver and other manifests. A Native AOT MSBuild uses this to keep SDK
        /// resolution identical to a JIT MSBuild of the same toolset.
        /// </para>
        /// <para>
        /// Register every resolver type of an assembly (MSBuild instantiates all of them from a manifest), during host
        /// initialization before the first project is evaluated. This method is thread-safe.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
        public static void RegisterPrecompiled(SdkResolver resolver, string assemblyName, Guid assemblyModuleVersionId)
        {
            if (resolver is null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            if (string.IsNullOrEmpty(assemblyName))
            {
                throw new ArgumentException("The resolver assembly name must not be empty.", nameof(assemblyName));
            }

            lock (s_precompiledResolvers)
            {
                if (!s_precompiledResolvers.TryGetValue(assemblyName, out var entry))
                {
                    entry = (assemblyModuleVersionId, new List<SdkResolver>());
                    s_precompiledResolvers.Add(assemblyName, entry);
                }
                else if (entry.ModuleVersionId != assemblyModuleVersionId)
                {
                    throw new ArgumentException($"SDK resolver assembly '{assemblyName}' was already registered with module version id {entry.ModuleVersionId}.", nameof(assemblyModuleVersionId));
                }

                if (!entry.Resolvers.Contains(resolver))
                {
                    entry.Resolvers.Add(resolver);
                }
            }
        }

        /// <summary>
        /// Gets the precompiled resolvers registered for the resolver assembly with the given simple name.
        /// </summary>
        internal static bool TryGetPrecompiledResolvers(string assemblyName, out Guid moduleVersionId, out IReadOnlyList<SdkResolver> resolvers)
        {
            lock (s_precompiledResolvers)
            {
                if (s_precompiledResolvers.TryGetValue(assemblyName, out var entry))
                {
                    moduleVersionId = entry.ModuleVersionId;
                    resolvers = entry.Resolvers.ToArray();
                    return true;
                }
            }

            moduleVersionId = Guid.Empty;
            resolvers = Array.Empty<SdkResolver>();
            return false;
        }

        /// <summary>
        /// Clears all registered resolvers. For test use only, to reset the process-global registration state.
        /// </summary>
        internal static void ClearRegisteredResolversForTests()
        {
            lock (s_registeredResolvers)
            {
                s_registeredResolvers.Clear();
            }

            lock (s_precompiledResolvers)
            {
                s_precompiledResolvers.Clear();
            }
        }
    }
}
