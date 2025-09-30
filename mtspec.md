# Thread-Safe Tasks: API Analysis Reference

This document provides a list of .NET APIs that should not be used or should be used with caution in thread-safe tasks. These APIs are problematic because they either rely on or modify process-level state, which can cause race conditions in multithreaded execution.

The APIs listed in this document will be detected by Roslyn analyzers and/or MSBuild BuildCheck to help identify potential threading issues in tasks that implement `IMultiThreadableTask`.

**Note**: The analyzers rely on **static code analysis** and may not catch all dynamic scenarios (such as reflection-based API calls).

## API Issues Categories

Categories of threading issues with .NET API usage in thread-safe tasks to be aware of:

1. **Working Directory Modifications and Usage**, such as file system operations with relative paths.
1. **Environment Variables Modification and Usage**
1. **Process Culture Modification and Usage**, which can affect data formatting.
1. **Assembly Loading**
1. **Static Fields**

### Best Practices

Instead of the problematic APIs listed below, thread-safe tasks should:

1. **Use `TaskEnvironment`** for all file system operations, environment variable changes, and working directory changes.
1. **Always use absolute paths** when still using some standard .NET file system APIs.
1. **Explicitly configure external processes** using `TaskEnvironment`.
1. **Never modify process culture**: Avoid modifying culture defaults.

## Detailed API Reference

The following tables list specific .NET APIs and their threading safety classification:

### System.IO.Path Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| `Path.GetFullPath(string path)` | ERROR | Uses current working directory | Use MSBuild API |

### System.IO.File Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| All methods | ERROR | Uses current working directory | Use absolute paths |

### System.IO.Directory Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| All methods | ERROR | Uses current working directory | Use absolute paths |

### System.Environment Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
|  All properties setters | ERROR | Modifies process-level state | Use MSBuild API |
| `Environment.CurrentDirectory` (getter, setter) | ERROR | Accesses process-level state | Use MSBuild API |
| `Environment.Exit(int exitCode)` | ERROR | Terminates entire process | Return false from task or throw exception |
| `Environment.FailFast` all overloads | ERROR | Terminates entire process | Return false from task or throw exception |
| All other methods | ERROR | Modifies process-level state | Use MSBuild API |

### System.IO.FileInfo Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| Constructor `FileInfo(string fileName)` | WARNING | Uses current working directory | Use absolute paths |
| `CopyTo` all overloads | WARNING | Destination path relative to current directory | Use absolute paths |
| `MoveTo` all overloads | WARNING | Destination path relative to current directory | Use absolute paths |
| `Replace` all overloads | WARNING | Paths relative to current directory | Use absolute paths |

### System.IO.DirectoryInfo Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| Constructor `DirectoryInfo(string path)` | WARNING | Uses current working directory | Use absolute paths |
| `MoveTo(string destDirName)` | WARNING | Destination path relative to current directory | Use absolute paths |

### System.IO.FileStream Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| Constructor `FileStream` all overloads | WARNING | Uses current working directory | Use absolute paths |

### System.IO Stream Classes

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| Constructor `StreamReader` all overloads | WARNING | Uses current working directory | Use absolute paths |Collapse commentComment on line R86Copilot commented on Sep 25, 2025 CopilotAIon Sep 25, 2025ContributorMore actionsThe table header is missing for this section. Should include a header row like the other API tables in the document.Copilot uses AI. Check for mistakes.Positive FeedbackNegative FeedbackWrite a replyResolve commentCode has comments. Press enter to view.

### System.Diagnostics.Process Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| All properties setters | ERROR | Modifies process-level state | Avoid |
| `Process.GetCurrentProcess().Kill()` | ERROR | Terminates entire process | Avoid |
| `Process.GetCurrentProcess().Kill(bool entireProcessTree)` | ERROR | Terminates entire process | Avoid |
| `Process.Start` all overloads | ERROR | May inherit process state | Use MSBuild API |

### System.Diagnostics.ProcessStartInfo Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| Constructor `ProcessStartInfo()` all overloads | ERROR | May inherit process state | Use MSBuild API |

### System.Threading.ThreadPool Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| `ThreadPool.SetMinThreads(int workerThreads, int completionPortThreads)` | ERROR | Modifies process-wide settings | Avoid |
| `ThreadPool.SetMaxThreads(int workerThreads, int completionPortThreads)` | ERROR | Modifies process-wide settings | Avoid |

### System.Globalization.CultureInfo Class

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| `CultureInfo.DefaultThreadCurrentCulture` (setter) | ERROR | Affects new threads | Modify the thread culture instead |
| `CultureInfo.DefaultThreadCurrentUICulture` (setter) | ERROR | Affects new threads | Modify the thread culture instead |

### Static

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| Static fields | WARNING | Shared across threads, can cause race conditions | Avoid |

### Assembly Loading (System.Reflection.Assembly class, System.Activator class)
Tasks that load assemblies dynamically in the task host may cause version conflicts. Version conflicts in task assemblies will cause build failures (previously these might have been sporadic). Both dynamically loaded dependencies and static dependencies can cause issues.

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| `Assembly.LoadFrom(string assemblyFile)` | WARNING | May cause version conflicts | Be aware of potential conflicts, use absolute paths |
| `Assembly.LoadFile(string path)` | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `Assembly.Load` all overloads | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `Assembly.LoadWithPartialName(string partialName)` | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `Activator.CreateInstanceFrom(string assemblyFile, string typeName)` | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `Activator.CreateInstance(string assemblyName, string typeName)` | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `AppDomain.Load` all overloads | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `AppDomain.CreateInstanceFrom(string assemblyFile, string typeName)` | WARNING | May cause version conflicts | Be aware of potential conflicts |
| `AppDomain.CreateInstance(string assemblyName, string typeName)` | WARNING | May cause version conflicts | Be aware of potential conflicts |

### P/Invoke

**Concerns**:
- P/Invoke calls may use process-level state like current working directory
- Native code may not be thread-safe
- Native APIs may modify global process state

| API | Level | Short Reason | Recommendation |
|-----|-------|--------------|-------|
| `[DllImport]` attribute | WARNING | Not covered by analyzers | Review for thread safety, use absolute paths |



Thread-Safe Tasks
Overview
MSBuild's current execution model assumes that tasks have exclusive control over the entire process during execution. This allows tasks to freely modify global process state such as environment variables, the current working directory, and other process-level resources. This design works well for MSBuild's approach of executing builds in separate processes for parallelization. With the introduction of multithreaded execution within a single MSBuild process, multiple tasks can now run concurrently. This requires a new task design to ensure that multiple tasks do not access/modify shared process state, and the relative paths are resolved correctly.

To enable this multithreaded execution model, tasks will declare their capability to run in multiple threads within one process. These capabilities are referred to as thread-safety capabilities and the corresponding tasks are called thread-safe tasks. Thread-safe tasks must avoid using APIs that modify or depend on global process state, as this could cause conflicts when multiple tasks execute concurrently. See Thread-Safe Tasks API Analysis Reference for detailed guidelines. Task authors will also get access to a TaskEnvironment that provides safe alternatives to global process state APIs. For example, task authors should use TaskEnvironment.GetAbsolutePath() instead of Path.GetFullPath() to ensure correct path resolution in multithreaded scenarios.

Tasks that are not thread-safe can still participate in multithreaded builds. MSBuild will execute these tasks in separate TaskHost processes to provide process-level isolation.

Thread-Safe Capability Indicators
Task authors can declare thread-safe capabilities in two different ways:

Interface-Based Thread-Safe Capability Declaration - Provides access to thread-safe APIs through TaskEnvironment to be used in the task code.
Attribute-Based Thread-Safe Capability Declaration - Allows existing tasks to declare its ability run in multithreaded mode without code changes. It is a compatibility bridge option.
Tasks that use TaskEnvironment cannot load in older MSBuild versions that do not support multithreading features, requiring authors to drop support for older MSBuild versions. To address this challenge, MSBuild provides a compatibility bridge that allows certain tasks targeting older MSBuild versions to participate in multithreaded builds. While correct absolute path resolution can be and should be achieved without accessing TaskEnvironment in tasks that use compatibility bridge options, tasks must avoid relying on environment variables or modifying global process state.

So, task authors who need to support older MSBuild versions will have three choices:

Maintain separate implementations - Create and support both thread-safe and legacy versions of the same task.
Use compatibility bridge approaches - Rely on MSBuild's ability to run legacy tasks in multithreaded mode without access to TaskEnvironment.
Accept reduced performance - Tasks will execute more slowly than their thread-safe versions because they must run in a separate TaskHost process
Interface-Based Thread-Safe Capability Declaration
Tasks indicate thread-safety capabilities by implementing the IMultiThreadableTask interface.

namespace Microsoft.Build.Framework;
public interface IMultiThreadableTask : ITask
{
    TaskEnvironment TaskEnvironment { get; set; }
}
Similar to how MSBuild provides the abstract Task class with default implementations for the ITask interface, MSBuild will offer a MultiThreadableTask abstract class with default implementations for the IMultiThreadableTask interface. Task authors will only need to implement the Execute method for the ITask interface and use TaskEnvironment within it to create their thread-safe tasks.

namespace Microsoft.Build.Utilities;
public abstract class MultiThreadableTask : Task, IMultiThreadableTask
{
    public TaskEnvironment TaskEnvironment{ get; set; }
}
Task authors who want to support older MSBuild versions need to:

Maintain both thread-safe and legacy implementations.
Use conditional task declarations based on MSBuild version to select which assembly to load the task from.
Note: Consider backporting IMultiThreadableTask to MSBuild 17.14 for graceful failure when the interface is used.

Attribute-Based Thread-Safe Capability Declaration
Task authors can indicate thread-safety capabilities by marking their task classes with a specific attribute. Tasks marked with this attribute can run in multithreaded builds but do not have access to TaskEnvironment APIs.

namespace Microsoft.Build.Framework;
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
internal class MSBuildMultiThreadableTaskAttribute : Attribute
{
    public MSBuildMultiThreadableTaskAttribute() { }
}
MSBuild detects MSBuildMultiThreadableTaskAttribute by its namespace and name only, ignoring the defining assembly, which allows customers to define the attribute in their own assemblies alongside their tasks. Since MSBuild does not ship the attribute, customers using newer MSBuild versions should prefer the Interface-Based Thread-Safe Capability Declaration.

For tasks to be eligible for multithreaded execution using this approach, they must satisfy the following conditions:

The task must not modify global process state (environment variables, working directory)
The task must not depend on global process state, including relative path resolution
API Usage Example
[MSBuildMultiThreadableTask]
public class MyTask : Task {...}
TaskEnvironment API
The TaskEnvironment provides thread-safe alternatives to APIs that use global process state, enabling tasks to execute safely in a multithreaded environment.

namespace Microsoft.Build.Framework;
public interface IMultiThreadableTask : ITask
{
    TaskEnvironment TaskEnvironment { get; set; }
}

public class TaskEnvironment
{ 
    public virtual AbsolutePath ProjectCurrentDirectory { get; set; }

    // This function resolves paths relative to ProjectCurrentDirectory.
    public virtual AbsolutePath GetAbsolutePath(string path);
    
    public virtual string? GetEnvironmentVariable(string name);
    public virtual IReadOnlyDictionary<string, string> GetEnvironmentVariables();
    public virtual void SetEnvironmentVariable(string name, string? value);

    public virtual ProcessStartInfo GetProcessStartInfo();
}
The TaskEnvironment class that MSBuild provides is not thread-safe. Task authors who spawn multiple threads within their task implementation must provide their own synchronization when accessing the task environment from multiple threads. However, each task receives its own isolated environment object, so synchronization with other concurrent tasks is not required.

Path Handling
To prevent common thread-safety issues related to path handling, we introduce path type that is implicitly convertible to string:

namespace Microsoft.Build.Framework;
public readonly struct AbsolutePath
{
    // Default value returns string.Empty for Path property
    public string Path { get; }
    internal AbsolutePath(string path, bool ignoreRootedCheck) { }
    public AbsolutePath(string path); // Checks Path.IsPathRooted
    public AbsolutePath(string path, AbsolutePath basePath) { }
    public static implicit operator string(AbsolutePath path) { }
    public override string ToString() => Path;
}
AbsolutePath converts implicitly to string for seamless integration with existing File/Directory APIs.

API Usage Example
public bool Execute(...)
{
    // Use APIs provided by TaskEnvironment
    string envVar = TaskEnvironment.GetEnvironmentVariable("EnvVar");
       
    // Convert string properties to strongly-typed paths and use them in standard File/Directory APIs
    AbsolutePath path = TaskEnvironment.GetAbsolutePath("SomePath");
    string content = File.ReadAllText(path);
    string content2 = File.ReadAllText(path.ToString());
    string content3 = File.ReadAllText(path.Path);
    ...
}