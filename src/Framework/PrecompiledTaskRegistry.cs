// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Microsoft.Build.Framework;

/// <summary>
/// Task classes that a host compiled into itself, grouped by the task assembly they came from.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="TaskClassRegistry"/>, which maps a bare task name to a class and bypasses
/// <c>&lt;UsingTask&gt;</c> entirely, this registry keeps normal task resolution intact: the engine still
/// evaluates the project's <c>&lt;UsingTask&gt;</c> elements, picks the matching registration (including its
/// <c>TaskFactory</c>, <c>Runtime</c> and <c>Architecture</c>), and resolves its <c>AssemblyFile</c> or
/// <c>AssemblyName</c>. Only the final "load this assembly and find this type" step is replaced: if the
/// assembly the <c>&lt;UsingTask&gt;</c> names is the same build of an assembly the host compiled in (same
/// module version id), the compiled-in type is used instead of loading the file.
/// </para>
/// <para>
/// This is what lets a Native AOT MSBuild run the tasks of a specific SDK layout in-process while any other
/// task assembly - a NuGet package, a project-local task, or a different build of an SDK task assembly - is
/// still honored exactly as written (the AOT host runs those out of process).
/// </para>
/// </remarks>
internal static class PrecompiledTaskRegistry
{
    private static readonly ConcurrentDictionary<string, PrecompiledTaskAssembly> s_assembliesByName = new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool s_hasRegistrations;

    /// <summary>
    /// Gets a value indicating whether any precompiled task has been registered.
    /// </summary>
    internal static bool HasRegistrations => s_hasRegistrations;

    /// <summary>
    /// Registers a compiled-in task class.
    /// </summary>
    /// <typeparam name="T">The task class.</typeparam>
    /// <param name="moduleVersionId">
    /// The module version id of the task assembly build that <typeparamref name="T"/> was compiled from. A
    /// <c>&lt;UsingTask AssemblyFile="..."&gt;</c> uses the compiled-in class only when the file has this module
    /// version id. <see cref="Guid.Empty"/> matches any file with the assembly's simple name; it is meant for
    /// assemblies that the engine always binds by name, such as Microsoft.Build.Tasks.Core.
    /// </param>
    internal static void Register<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] T>(Guid moduleVersionId)
        where T : ITask
    {
        Type taskType = typeof(T);
        AssemblyName assemblyName = taskType.Assembly.GetName();
        string simpleName = assemblyName.Name ?? throw new ArgumentException(taskType.FullName, nameof(T));

        PrecompiledTaskAssembly assembly = s_assembliesByName.GetOrAdd(simpleName, name => new PrecompiledTaskAssembly(name, assemblyName.Version, moduleVersionId));
        if (assembly.ModuleVersionId != moduleVersionId)
        {
            throw new ArgumentException($"Task assembly '{simpleName}' was already registered with module version id {assembly.ModuleVersionId}.", nameof(moduleVersionId));
        }

        assembly.Add(new PrecompiledTask(taskType));
        s_hasRegistrations = true;
    }

    /// <summary>
    /// Looks up the compiled-in task classes of the assembly with the given simple name.
    /// </summary>
    internal static bool TryGetAssembly(string simpleName, [NotNullWhen(true)] out PrecompiledTaskAssembly? assembly)
    {
        if (!s_hasRegistrations)
        {
            assembly = null;
            return false;
        }

        return s_assembliesByName.TryGetValue(simpleName, out assembly);
    }
}

/// <summary>
/// The compiled-in task classes of one task assembly.
/// </summary>
internal sealed class PrecompiledTaskAssembly
{
    private readonly object _lock = new();
    private readonly Dictionary<string, PrecompiledTask> _tasksByFullName = new(StringComparer.OrdinalIgnoreCase);
    private PrecompiledTask[] _tasks = [];

    internal PrecompiledTaskAssembly(string name, Version? version, Guid moduleVersionId)
    {
        Name = name;
        Version = version;
        ModuleVersionId = moduleVersionId;
    }

    /// <summary>
    /// The simple name of the assembly.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// The version of the compiled-in assembly.
    /// </summary>
    internal Version? Version { get; }

    /// <summary>
    /// The module version id of the assembly build that was compiled in, or <see cref="Guid.Empty"/> to match by name.
    /// </summary>
    internal Guid ModuleVersionId { get; }

    /// <summary>
    /// The registered task classes, in registration order (the order partial type-name matching walks them).
    /// </summary>
    internal PrecompiledTask[] Tasks => _tasks;

    internal void Add(PrecompiledTask task)
    {
        lock (_lock)
        {
            if (!_tasksByFullName.ContainsKey(task.FullName))
            {
                _tasksByFullName.Add(task.FullName, task);
                _tasks = [.. _tasks, task];
            }
        }
    }

    internal bool TryGetByFullName(string fullName, [NotNullWhen(true)] out PrecompiledTask? task)
    {
        lock (_lock)
        {
            return _tasksByFullName.TryGetValue(fullName, out task);
        }
    }
}

/// <summary>
/// A compiled-in task class.
/// </summary>
internal sealed class PrecompiledTask
{
    internal PrecompiledTask([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] Type taskType)
    {
        TaskType = taskType;
        FullName = taskType.FullName ?? taskType.Name;
    }

    /// <summary>
    /// The task class. Its public constructors and properties are rooted for trimming by the registration.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
    internal Type TaskType { get; }

    /// <summary>
    /// The full name of the task class.
    /// </summary>
    internal string FullName { get; }
}
