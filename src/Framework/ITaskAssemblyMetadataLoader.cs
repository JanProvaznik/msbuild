// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Microsoft.Build.Framework;

/// <summary>
/// Implemented by the engine context handed to MSBuild's own task factories. A process that cannot load task
/// assemblies (a Native AOT MSBuild) uses it to inspect an assembly a factory produced - for example an inline task
/// compiled by RoslynCodeTaskFactory - without loading its code; the task then runs in a .NET task host.
/// </summary>
internal interface ITaskAssemblyMetadataLoader
{
    /// <summary>
    /// Loads <paramref name="assemblyPath"/> for metadata inspection only. The returned assembly and its types can be
    /// reflected over but not executed, and stay valid for the lifetime of the process.
    /// </summary>
    Assembly LoadTaskAssemblyMetadata(string assemblyPath);
}
