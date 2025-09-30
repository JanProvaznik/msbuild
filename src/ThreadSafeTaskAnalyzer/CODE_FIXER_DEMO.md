# Code Fixer Demo

This document demonstrates how the code fixer works for the IMultiThreadableTask analyzer.

## Before Fix

```csharp
using System.IO;
using Microsoft.Build.Framework;

public class MyTask : IMultiThreadableTask
{
    public IBuildEngine BuildEngine { get; set; }
    public ITaskHost HostObject { get; set; }
    public TaskEnvironment TaskEnvironment { get; set; }
    
    public bool Execute()
    {
        // ⚠️ MSB4260: Symbol 'File.Exists(string)' is banned in IMultiThreadableTask implementations
        if (File.Exists("input.txt"))
        {
            // ⚠️ MSB4260: Symbol 'Directory.Exists(string)' is banned
            if (Directory.Exists("output"))
            {
                // ⚠️ MSB4260: Symbol 'FileInfo' constructor is banned
                var fileInfo = new FileInfo("data.xml");
                
                return true;
            }
        }
        return false;
    }
}
```

## After Applying Code Fixer

The code fixer wraps path arguments with `TaskEnvironment.GetAbsolutePath()`:

```csharp
using System.IO;
using Microsoft.Build.Framework;

public class MyTask : IMultiThreadableTask
{
    public IBuildEngine BuildEngine { get; set; }
    public ITaskHost HostObject { get; set; }
    public TaskEnvironment TaskEnvironment { get; set; }
    
    public bool Execute()
    {
        // ✅ Fixed: Wrapped with TaskEnvironment.GetAbsolutePath()
        if (File.Exists(TaskEnvironment.GetAbsolutePath("input.txt")))
        {
            // ✅ Fixed: Wrapped with TaskEnvironment.GetAbsolutePath()
            if (Directory.Exists(TaskEnvironment.GetAbsolutePath("output")))
            {
                // ✅ Fixed: Wrapped with TaskEnvironment.GetAbsolutePath()
                var fileInfo = new FileInfo(TaskEnvironment.GetAbsolutePath("data.xml"));
                
                return true;
            }
        }
        return false;
    }
}
```

## How to Use

1. **In Visual Studio**: 
   - Place cursor on the warning squiggle
   - Press `Ctrl+.` or click the light bulb 💡
   - Select "Wrap with TaskEnvironment.GetAbsolutePath()"

2. **In VS Code with C# extension**:
   - Place cursor on the diagnostic
   - Press `Ctrl+.` (Windows/Linux) or `Cmd+.` (Mac)
   - Select "Wrap with TaskEnvironment.GetAbsolutePath()"

3. **Command Line** (using dotnet format):
   ```bash
   dotnet format --fix-analyzers --diagnostics=MSB4260
   ```

## What the Fixer Does

- **Identifies**: Path string arguments in file system API calls
- **Wraps**: The first argument (typically the path) with `TaskEnvironment.GetAbsolutePath()`
- **Preserves**: All other arguments and expression structure

## Limitations

The code fixer currently handles:
- ✅ File system operations (File, Directory, FileInfo, DirectoryInfo, FileStream, etc.)
- ❌ Console operations (requires manual migration to Log methods)
- ❌ Environment variables (requires manual migration to TaskEnvironment methods)
- ❌ Process.Start (requires manual configuration with TaskEnvironment.GetProcessStartInfo)

For non-path APIs, you'll need to manually migrate to the appropriate TaskEnvironment or MSBuild logging methods.

## Example: Complex Expressions

The fixer also works with complex path expressions:

### Before
```csharp
string basePath = "data";
string fileName = "file.txt";
if (File.Exists(Path.Combine(basePath, fileName)))
{
    // ...
}
```

### After
```csharp
string basePath = "data";
string fileName = "file.txt";
if (File.Exists(TaskEnvironment.GetAbsolutePath(Path.Combine(basePath, fileName))))
{
    // ...
}
```

## Best Practices

1. **Review the fix**: Always review auto-generated fixes to ensure correctness
2. **Test thoroughly**: Ensure paths resolve correctly in your build scenarios
3. **Consider performance**: If calling `GetAbsolutePath()` repeatedly on the same path, cache the result
4. **Use variables**: For better readability, consider extracting to a variable:

```csharp
// Instead of:
if (File.Exists(TaskEnvironment.GetAbsolutePath("input.txt")))

// Consider:
string inputPath = TaskEnvironment.GetAbsolutePath("input.txt");
if (File.Exists(inputPath))
```
