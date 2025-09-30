# IMultiThreadableTask Analyzer - Design Proposal

**Status**: Proposal for Review  
**Target MSBuild Version**: 17.15+  
**Authors**: MSBuild Team  
**Last Updated**: September 30, 2025

---

## Executive Summary

### Problem Statement

MSBuild's multithreaded execution allows tasks implementing `IMultiThreadableTask` to run concurrently within a single process. However, many .NET APIs commonly used in tasks depend on process-global state (e.g., current working directory, environment variables), creating race conditions when tasks execute simultaneously.

**Example Race Condition**:
```csharp
// Task A: sets working directory to C:\ProjectA
// Task B: sets working directory to C:\ProjectB (races with A)
// Task A: calls File.Exists("bin\output.dll") (resolves to C:\ProjectB\bin - WRONG!)
```

### Proposed Solution

A Roslyn analyzer that detects unsafe API usage in `IMultiThreadableTask` implementations at compile time, with an automated code fixer to wrap file paths with `TaskEnvironment.GetAbsolutePath()`.

### Key Benefits

- Catches threading issues at compile time instead of runtime
- Automated fixes reduce manual effort for task authors
- Educates developers about thread-safety requirements
- Prevents race conditions in multithreaded builds

---

## 1. Design Overview

### 1.1 Scope

The analyzer detects two categories of problematic APIs:

1. **Always-Banned APIs** - Never safe in multithreaded context (e.g., `Environment.Exit`, `Process.Kill`, `Path.GetFullPath`)
2. **Conditionally-Banned APIs** - File system APIs safe only with absolute paths (e.g., `File.Exists`, `Directory.CreateDirectory`)

Detailed API list available in [thread-safe-tasks-api-analysis.md](thread-safe-tasks-api-analysis.md).

### 1.2 Diagnostic Information

- **ID**: `MSB9999`
- **Severity**: Warning (proposal)
- **Category**: Microsoft.Build.Tasks
- **Activation**: Only within types implementing `IMultiThreadableTask`

### 1.3 Code Fixer Capability

Offers automated fix: wraps path arguments with `TaskEnvironment.GetAbsolutePath()`.

**Example Transformation**:
```csharp
// Before:
if (File.Exists(relativePath)) { ... }

// After (automated fix):
if (File.Exists(TaskEnvironment.GetAbsolutePath(relativePath))) { ... }
```

---

## 2. Detection Strategy

### 2.1 Always-Banned APIs

Detected via exact symbol matching using Roslyn documentation IDs. Categories:

- **Path APIs**: `Path.GetFullPath` (uses current directory implicitly)
- **Environment APIs**: `Environment.CurrentDirectory`, `Environment.SetEnvironmentVariable`, `Environment.Exit`
- **Process APIs**: `Process.Start`, `Process.Kill`, `ProcessStartInfo` constructors
- **Threading APIs**: `ThreadPool.SetMinThreads`, `ThreadPool.SetMaxThreads`
- **Culture APIs**: `CultureInfo.DefaultThreadCurrentCulture` setter
- **Assembly Loading**: `Assembly.Load*`, `Activator.CreateInstance*`
- **Console APIs**: `Console.Write`, `Console.ReadLine` (interferes with build output)

**Design Decision**: Explicit list ensures precision and clear error messages.

### 2.2 Conditionally-Banned APIs (Smart Path Detection)

File system types analyzed:
- `System.IO.File`
- `System.IO.Directory`
- `System.IO.FileInfo`
- `System.IO.DirectoryInfo`
- `System.IO.FileStream`
- `System.IO.StreamReader`
- `System.IO.StreamWriter`

**Detection Logic**:
1. Check if invoked member belongs to one of these types
2. Find first `string` parameter (assumed to be file path)
3. Check if argument is wrapped with `TaskEnvironment.GetAbsolutePath()` or `.AbsolutePath` property
4. Warn only if NOT wrapped

**Recognized Safe Patterns**:
```csharp
// ✅ Safe - wrapped
File.Exists(TaskEnvironment.GetAbsolutePath(path))

// ✅ Safe - absolute path property
File.Exists(fileInfo.FullName)
File.Exists(directoryInfo.FullName)

// ❌ Warning - not wrapped
File.Exists(path)
```

**Design Decision**: Pattern-based detection (vs. listing every method) keeps analyzer maintainable as APIs evolve.

### 2.3 Current Limitations

- **No data-flow analysis**: Doesn't track if `path` variable already contains absolute path
- **First parameter assumption**: Only checks first string parameter for paths
- **No constant analysis**: Warns even for string literals like `"C:\\Windows\\System32"`

**Rationale**: These limitations prevent false negatives at cost of some false positives. Developers can suppress warnings where appropriate.

---

## 3. Code Fixer Design

### 3.1 Transformation Rules

**Offers fixes for conditionally-banned APIs** (path methods):
- Wraps first string argument with `TaskEnvironment.GetAbsolutePath(...)`
- Preserves all other arguments unchanged
- Adds `using` directive if needed (implementation detail)

**Does NOT offer fixes for**:
- Always-banned APIs (no safe alternative via wrapping)
- Already-wrapped paths (no warning issued)
- Non-string parameters

### 3.2 User Experience

- Fix appears in Visual Studio Quick Actions (Ctrl+.)
- Action title: "Wrap with TaskEnvironment.GetAbsolutePath()"
- Available immediately when warning appears

---

## 4. Open Questions for Review

### 4.1 Distribution Model (Proposal)

**Recommended**: Ship analyzer with `Microsoft.Build.Utilities.Core` NuGet package

**Pros**:
- Task authors already reference this package
- Zero configuration required
- Analyzer version stays synchronized with APIs
- Automatic updates

**Cons**:
- Increases package size
- Cannot be disabled without disabling entire analyzer infrastructure

**Alternative Considered**: Standalone `Microsoft.Build.Analyzers` package
- **Rejected**: Discovery problem, extra manual step reduces adoption

**Question**: Do we have consensus on shipping with Utilities.Core?

### 4.2 Default Severity

**Proposal**: `Warning`

**Alternatives**:
- `Error`: Blocks compilation - may be too aggressive for initial release
- `Info`: Easy to miss - defeats purpose

**Question**: Should we plan to increase to Error in a future version?

### 4.3 Opt-Out Mechanism

**Proposal**: MSBuild property `EnableMSBuildThreadSafetyAnalyzer`

```xml
<PropertyGroup>
  <EnableMSBuildThreadSafetyAnalyzer>false</EnableMSBuildThreadSafetyAnalyzer>
</PropertyGroup>
```

**Question**: Is property name acceptable? Should we support per-diagnostic severity via `.editorconfig` only?

### 4.4 Scope of Analysis

**Proposal**: Analyzer only activates within types implementing `IMultiThreadableTask`

**Alternative**: Analyze all code
- **Rejected**: Creates noise for non-multithreadable tasks

**Question**: Should we offer opt-in to analyze all Task types (not just IMultiThreadableTask)?

---

## 5. Testing Approach

### 5.1 Validation

A demo project demonstrates analyzer behavior:
- **ProblematicTask**: Task with 9 unsafe API usages → expects 9 MSB9999 warnings
- **CorrectTask**: Same logic using wrapped paths → expects 0 warnings

**Location**: `src/ThreadSafeTaskAnalyzer/VisualStudioDemo/`

### 5.2 Manual Testing

1. Open demo in Visual Studio
2. Observe green squiggles on unwrapped File/Directory calls
3. Verify Quick Action offers "Wrap with..." fix
4. Apply fix and confirm warning disappears

---

## 6. Design Rationale

### 6.1 Why Two Detection Modes?

**Always-banned APIs** require explicit symbol matching because:
- No safe usage pattern exists (e.g., `Environment.Exit` always wrong)
- Need precise, actionable error messages

**Conditionally-banned APIs** use pattern detection because:
- Safe when used correctly (with absolute paths)
- Hundreds of methods across file system types - listing all is unmaintainable

### 6.2 Why Check Only First String Parameter?

.NET file system APIs follow consistent pattern:
```csharp
File.ReadAllText(string path)            // path is first param
Directory.CreateDirectory(string path)   // path is first param
new FileStream(string path, FileMode mode) // path is first param
```

**Tradeoff**: Simplicity and performance vs. handling rare edge cases. False negatives possible for unusual APIs, but unlikely in practice.

### 6.3 Why Not Full Data-Flow Analysis?

**Rejected**: Tracking whether variables contain absolute paths via data-flow analysis

**Reasons**:
- Extremely complex for limited benefit
- Performance impact on live editing
- Many code paths are unknowable at compile time (user input, config files, etc.)

**Chosen approach**: Conservative warnings + developer suppressions for known-safe cases

---

## 7. Future Enhancements (Out of Scope for v1)

1. **Constant analysis**: Suppress warnings for string literal absolute paths (`"C:\\Windows"`)
2. **Data-flow tracking**: Recognize when variable provably contains absolute path
3. **Strict mode**: Warn on all path operations, even when wrapped
4. **VB.NET support**: Currently C# only
5. **Additional banned APIs**: Console.SetOut, AppDomain.SetData, etc.
6. **Build-time enforcement**: MSBuild task that fails build on violations

---

## 8. Known Limitations

1. **String literals**: Warns on `File.Exists("C:\\absolute\\path")` even though safe
2. **Method chaining**: May not recognize `GetAbsolutePath()` in complex expressions
3. **Reflection**: Cannot detect dynamic API calls via reflection
4. **Code generation**: Does not analyze dynamically generated code
5. **Performance**: No specific performance optimizations yet implemented

These are acceptable for initial version. Can be addressed based on user feedback.

---

## 9. References

- [thread-safe-tasks-api-analysis.md](thread-safe-tasks-api-analysis.md) - Complete list of banned APIs
- [thread-safe-tasks.md](thread-safe-tasks.md) - Thread-safe tasks overview
- [multithreaded-msbuild.md](multithreaded-msbuild.md) - Multithreaded MSBuild specification
- Demo implementation: `src/ThreadSafeTaskAnalyzer/`
- Sample usage: `src/ThreadSafeTaskAnalyzer/VisualStudioDemo/`

---

**Document Version**: 1.0  
**Status**: Awaiting review and decision on open questions
