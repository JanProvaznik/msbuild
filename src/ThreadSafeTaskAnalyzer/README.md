# ThreadSafeTaskAnalyzer

This analyzer detects problematic API usage patterns in multithreadable MSBuild tasks that implement `IMultiThreadableTask`.

## Overview

The analyzer flags APIs that should not be used in `IMultiThreadableTask` implementations due to threading concerns. Multithreadable tasks run in parallel and must not use APIs that depend on process-global state such as current working directory, environment variables, or process-wide culture settings.

## Banned APIs

The analyzer has **80+ banned APIs** hardcoded, organized into these categories:

### 1. File System Operations
- `System.IO.File` - All methods use current working directory for relative paths
- `System.IO.Directory` - All methods use current working directory for relative paths
- `System.IO.Path.GetFullPath()` - Uses current working directory
- `System.IO.FileInfo`, `System.IO.DirectoryInfo` constructors - Use current working directory
- `System.IO.FileStream`, `System.IO.StreamReader`, `System.IO.StreamWriter` constructors with file paths

**Alternative**: Use absolute paths or `TaskEnvironment.GetAbsolutePath()`

### 2. Environment & Process State
- `System.Environment.CurrentDirectory` - Accesses process-level state
- `System.Environment.SetEnvironmentVariable()` - Modifies process-level state  
- `System.Environment.Exit()`, `FailFast()` - Terminates entire process
- `System.Diagnostics.Process.Start()` - May inherit environment and working directory
- `System.Diagnostics.ProcessStartInfo` constructors - May inherit process state

**Alternative**: Use `TaskEnvironment.GetEnvironmentVariable()`, `TaskEnvironment.SetEnvironmentVariable()`, `TaskEnvironment.GetProcessStartInfo()`

### 3. Console I/O
- `System.Console.Write()`, `WriteLine()` - May interfere with build output
- `System.Console.ReadLine()`, `ReadKey()` - May cause deadlocks in automated builds
- `System.Console.Out`, `Error`, `In` properties

**Alternative**: Use MSBuild's `Log.LogMessage()`, `Log.LogError()`, etc.

### 4. Threading & Culture
- `System.Threading.ThreadPool.SetMinThreads()`, `SetMaxThreads()` - Modifies process-wide settings
- `System.Globalization.CultureInfo.DefaultThreadCurrentCulture/UICulture` - Affects new threads

**Alternative**: Modify thread-specific culture settings

### 5. Assembly Loading
- `System.Reflection.Assembly.Load*()` methods - May cause version conflicts
- `System.Activator.CreateInstance*()` methods - May cause version conflicts
- `System.AppDomain.Load*()` and `CreateInstance*()` methods

**Warning**: These emit warnings rather than errors due to legitimate use cases

## Diagnostic ID

**MSB4260**: Symbol is banned in IMultiThreadableTask implementations

## Code Fixer

The analyzer includes a code fixer that can automatically wrap path arguments with `TaskEnvironment.GetAbsolutePath()` for common file system operations.

### Supported Fixes

The code fixer can automatically fix violations for:
- `File.Exists(path)` → `File.Exists(TaskEnvironment.GetAbsolutePath(path))`
- `Directory.Exists(path)` → `Directory.Exists(TaskEnvironment.GetAbsolutePath(path))`
- `new FileInfo(path)` → `new FileInfo(TaskEnvironment.GetAbsolutePath(path))`
- And other path-related API calls

### Using the Code Fixer

In Visual Studio or VS Code with C# extension:
1. Click on the warning squiggle or place cursor on the warning
2. Press `Ctrl+.` (Quick Actions)
3. Select "Wrap with TaskEnvironment.GetAbsolutePath()"

**Note**: The code fixer provides a quick fix for path-related issues, but you should review the changes to ensure they're appropriate for your scenario. Some APIs (like Console.WriteLine or Environment.SetEnvironmentVariable) require manual migration to the appropriate TaskEnvironment or MSBuild logging methods.

## Usage

The analyzer automatically activates when analyzing projects that reference it. No additional configuration is needed - all banned APIs are hardcoded in the analyzer.

### Example Integration

```xml
<ItemGroup>
  <Analyzer Include="Microsoft.Build.Utilities.MultiThreadableTaskAnalyzer.dll" />
</ItemGroup>
```

## Demo

See the `demo/` directory for examples of:
- Tasks that trigger warnings (`SimpleTest.cs`)
- Correct thread-safe implementations (`GoodTask`)
- Common migration scenarios

Run the demo:
```powershell
cd demo
dotnet build RealDemo.csproj
```