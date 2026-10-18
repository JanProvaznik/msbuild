# Precompiled task assemblies and SDK resolvers

**Status:** Implemented. Used by the Native AOT MSBuild in [`src/MSBuild.Aot`](../../src/MSBuild.Aot/README.md).

## Background and motivation

[`Task.RegisterTask<T>()`](task-class-registration-api.md) lets an in-process host hand the engine task classes by
name, bypassing `<UsingTask>`. That suits a host that runs a known, closed set of projects, but not a general-purpose
MSBuild: projects can override, condition, or re-point `<UsingTask>` elements, pick a `TaskFactory`, or ask for a
`Runtime`/`Architecture`, and two assemblies can define tasks with the same name.

A Native AOT MSBuild needs the opposite trade-off: keep task selection exactly as it is, and only avoid *loading* the
assemblies it has compiled in. The same applies to SDK resolvers: `SdkResolver.Register` adds a resolver to the built-in
pass, which changes resolution order relative to the `SdkResolvers` manifests a JIT MSBuild would load.

## API

```diff
 namespace Microsoft.Build.Utilities;

 public abstract class Task
 {
+    public static void RegisterPrecompiledTask<
+        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(
+        Guid assemblyModuleVersionId)
+        where T : ITask;
 }

 namespace Microsoft.Build.Framework;

 public abstract class SdkResolver
 {
+    public static void RegisterPrecompiled(SdkResolver resolver, string assemblyName, Guid assemblyModuleVersionId);
 }
```

## Semantics

- **Tasks.** The engine resolves `<UsingTask>` as usual. When it reaches the step that would load the named assembly
  and find the type (`AssemblyTaskFactory.InitializeFactory`), it first looks for a registration whose assembly simple
  name matches the `<UsingTask>`:
  - `AssemblyFile`: the file's module version id must equal the registered one (`Guid.Empty` matches any file; used for
    `Microsoft.Build.Tasks.Core`, which the engine always binds by name). Ids are read from the file's metadata with
    `System.Reflection.Metadata` and cached by path, size and timestamp.
  - `AssemblyName`: simple name, and version when the name carries one.
  - The type name is matched like `TypeLoader` does (full name, then partial name).
  - A `<UsingTask>` that explicitly requests a task host (`TaskHostFactory`, or a `Runtime`/`Architecture` other than
    the current process's) does not use the registration, so the task keeps running out of proc.
  The resolved type then flows through the normal path (parameter binding, multithreaded-mode routing, telemetry).
- **SDK resolvers.** When the engine processes an `SdkResolvers` manifest whose assembly file name is `assemblyName`
  and whose module version id matches, it uses the registered resolvers in the manifest's place - with the manifest's
  `ResolvableSdkPattern`, in the manifest's position. Otherwise the manifest is loaded as before (or, with
  `Microsoft.Build.EnableSdkResolverDynamicLoading` off, fails with MSB4282).
- Registration is process-global and thread-safe, and is meant to happen at host startup.

## Related engine behavior in a process that cannot load assemblies

`TaskFactoryUtilities.CanLoadTaskAssemblies` is `false` under Native AOT. Then:

- an `AssemblyTaskFactory` task that is not precompiled is described through a `MetadataLoadContext` and runs in a
  .NET task host (its runtime defaults to the current one, as an in-proc load would);
- MSBuild's out-of-proc task factories (`RoslynCodeTaskFactory`) compile for out-of-proc execution and the engine runs
  the compiled task in a task host without loading it (`ITaskAssemblyMetadataLoader`);
- a task factory from any other assembly fails with MSB4283;
- MSBuild's own forwarding loggers are created without reflection (`LoggerDescription.TryCreateWellKnownForwardingLogger`).
