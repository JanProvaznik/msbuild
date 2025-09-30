# IMultiThreadableTask Analyzer and Code Fixer - Implementation Specification

**Status**: Draft for Review  
**Target MSBuild Version**: 17.15+  
**Authors**: MSBuild Team  
**Last Updated**: September 30, 2025

---

## Executive Summary

### Problem Statement

MSBuild's new multithreaded execution feature allows tasks implementing `IMultiThreadableTask` to run concurrently within a single process. However, many .NET APIs commonly used in tasks depend on process-global state (e.g., current working directory, environment variables), creating race conditions and incorrect behavior when multiple tasks execute simultaneously.

**Example Issue**:
```csharp
// Task A sets working directory to C:\ProjectA
// Task B sets working directory to C:\ProjectB  (races with Task A)
// Task A calls File.Exists("bin\output.dll")    (resolves to C:\ProjectB\bin - WRONG!)
```

### Proposed Solution

A Roslyn analyzer that:
1. **Detects** unsafe API usage in `IMultiThreadableTask` implementations at compile time
2. **Provides** automated code fixes to wrap paths with `TaskEnvironment.GetAbsolutePath()`
3. **Ships** automatically with `Microsoft.Build.Utilities.Core` NuGet package
4. **Educates** task authors about thread-safety requirements through actionable diagnostics

### Key Benefits

- **Prevents runtime race conditions** by catching issues at compile time
- **Reduces support burden** by preventing common multithreading mistakes
- **Accelerates adoption** of `IMultiThreadableTask` through automated fixes
- **Maintains backward compatibility** - tasks can still run in single-threaded mode

### Decision Points for Review

1. **Distribution**: Should analyzer ship with `Microsoft.Build.Utilities.Core` or as standalone package? (Recommendation: Ship with Utilities.Core - see §12.2)
2. **Severity**: Should default severity be Warning or Error? (Recommendation: Warning for 17.15, consider Error in future)
3. **Opt-Out**: Should there be global opt-out property? (Recommendation: Yes - `EnableMSBuildThreadSafetyAnalyzer`)
4. **Scope**: Should analyzer run on all code or only `IMultiThreadableTask` types? (Recommendation: Scoped to IMultiThreadableTask only)

---

## 1. Overview

### 1.1 Purpose

This specification defines the design and implementation of a Roslyn analyzer and code fixer for detecting and correcting unsafe API usage in MSBuild tasks that implement `IMultiThreadableTask`. The analyzer enforces the threading safety requirements outlined in [thread-safe-tasks-api-analysis.md](thread-safe-tasks-api-analysis.md) by identifying APIs that rely on or modify global process state.

### 1.2 Scope

The analyzer operates in two modes:

1. **Always-Banned APIs** - APIs that should never be used regardless of context (e.g., `Environment.Exit`, `Process.Kill`)
2. **Conditionally-Banned APIs** - File system APIs that are safe when used with absolute paths but dangerous with relative paths

The code fixer provides automated remediation by wrapping path arguments with `TaskEnvironment.GetAbsolutePath()` to ensure absolute path resolution.

### 1.3 Design Goals

- **Accuracy**: Minimize false positives by detecting actual unsafe usage patterns
- **Developer Experience**: Provide clear, actionable diagnostics with automated fixes
- **Performance**: Efficient analysis suitable for live editing in Visual Studio
- **Maintainability**: Abstract patterns rather than exhaustive API enumeration
- **Compatibility**: Work across .NET Framework and .NET Core MSBuild tasks

## 2. Architecture

### 2.1 Component Overview

```
IMultiThreadableTaskBannedAnalyzer (Abstract)
├── CSharpIMultiThreadableTaskBannedAnalyzer (C# implementation)

CSharpIMultiThreadableTaskCodeFixProvider
└── Wraps path arguments with TaskEnvironment.GetAbsolutePath()
```

### 2.2 Diagnostic Information

**Diagnostic ID**: `MSB9999`  
**Category**: `Microsoft.Build.Tasks`  
**Default Severity**: `Warning`  
**Title**: "Symbol is banned in IMultiThreadableTask implementations"  
**Message Format**: "Symbol '{0}' is banned in IMultiThreadableTask implementations{1}"

### 2.3 Activation Criteria

The analyzer only activates when:
1. The containing class implements `IMultiThreadableTask` interface
2. The operation occurs within a method of that class
3. The operation matches a banned API pattern

This scoping prevents unnecessary analysis of non-thread-safe tasks.

## 3. Detection Mechanisms

### 3.1 Always-Banned APIs

These APIs are detected by exact documentation ID matching and always trigger warnings:

#### 3.1.1 Path APIs
- `Path.GetFullPath(string)` - Relies on current working directory
- `Path.GetFullPath(string, string)` - Base path parameter may cause issues

**Rationale**: These methods implicitly use process-level state for path resolution.

#### 3.1.2 Environment APIs
- `Environment.CurrentDirectory` (property getter/setter)
- `Environment.SetEnvironmentVariable()` (all overloads)
- `Environment.Exit()`
- `Environment.FailFast()` (all overloads)

**Rationale**: Modify or access process-level state affecting all threads.

#### 3.1.3 Process Control APIs
- `Process.Kill()` (all overloads)
- `Process.Start()` (all overloads)
- `ProcessStartInfo` constructors (all overloads)

**Rationale**: May inherit environment and working directory; process termination affects entire application.

#### 3.1.4 Threading APIs
- `ThreadPool.SetMinThreads()`
- `ThreadPool.SetMaxThreads()`

**Rationale**: Modify process-wide thread pool configuration.

#### 3.1.5 Culture APIs
- `CultureInfo.DefaultThreadCurrentCulture` (property setter)
- `CultureInfo.DefaultThreadCurrentUICulture` (property setter)

**Rationale**: Affect culture for new threads process-wide.

#### 3.1.6 Assembly Loading APIs
- `Assembly.LoadFrom()`, `Assembly.LoadFile()`, `Assembly.Load()` (all overloads)
- `Assembly.LoadWithPartialName()`
- `Activator.CreateInstanceFrom()`, `Activator.CreateInstance()`
- `AppDomain.Load()`, `AppDomain.CreateInstanceFrom()`, `AppDomain.CreateInstance()`

**Rationale**: May cause version conflicts in multithreaded environments.

#### 3.1.7 Console APIs
- `Console.Out`, `Console.Error`, `Console.In` (properties)
- `Console.Write()`, `Console.WriteLine()`
- `Console.ReadLine()`, `Console.ReadKey()`

**Rationale**: Interfere with build output and may cause deadlocks.

### 3.2 Conditionally-Banned APIs (Smart Path Detection)

These APIs are safe when called with absolute paths but dangerous with relative paths.

#### 3.2.1 Detected Types

The analyzer identifies method calls and object creations on these types:
- `System.IO.File`
- `System.IO.Directory`
- `System.IO.FileInfo`
- `System.IO.DirectoryInfo`
- `System.IO.FileStream`
- `System.IO.StreamReader`
- `System.IO.StreamWriter`

#### 3.2.2 Detection Algorithm

For each invocation or object creation:

```
1. Check if method/constructor is on a path-related type (3.2.1)
2. Check if method/constructor has at least one string parameter
3. For each string argument (in parameter order):
   a. Check if argument is wrapped with TaskEnvironment.GetAbsolutePath()
   b. Check if argument is of type AbsolutePath (implicit conversion)
   c. If wrapped: SKIP - no warning needed
   d. If not wrapped: WARN - potentially relative path
4. Stop at first string parameter checked
```

**Example Flow**:
```csharp
// Case 1: Unwrapped - WARNING
File.Exists(path)
→ parameter: string path
→ argument: 'path' (unwrapped)
→ MSB9999: Uses current working directory

// Case 2: Wrapped - NO WARNING
File.Exists(TaskEnvironment.GetAbsolutePath(path))
→ parameter: string path
→ argument: IInvocationOperation(GetAbsolutePath)
→ No warning - path is absolute

// Case 3: AbsolutePath type - NO WARNING
AbsolutePath absPath = TaskEnvironment.GetAbsolutePath(path);
File.Exists(absPath)
→ parameter: string path
→ argument: IConversionOperation(AbsolutePath → string)
→ No warning - implicit conversion from absolute path
```

#### 3.2.3 Wrapping Detection

The analyzer recognizes these patterns as "safe" (absolute paths):

1. **Direct GetAbsolutePath call**:
   ```csharp
   TaskEnvironment.GetAbsolutePath(path)
   ```

2. **AbsolutePath type conversion**:
   ```csharp
   AbsolutePath absPath = ...;
   File.Exists(absPath) // Implicit conversion to string
   ```

3. **Nested conversions**:
   ```csharp
   var absPath = TaskEnvironment.GetAbsolutePath(path);
   File.Exists(absPath.ToString()) // Explicit conversion
   ```

#### 3.2.4 Current Limitations

The analyzer does NOT currently detect:

1. **Variable assignments**:
   ```csharp
   var absolutePath = TaskEnvironment.GetAbsolutePath(path);
   File.Exists(absolutePath) // Still warns - doesn't track variable
   ```

2. **Multiple string parameters**:
   ```csharp
   File.Copy(source, destination)
   // Only checks first parameter (source)
   ```

3. **Dynamic/reflection-based calls**:
   ```csharp
   typeof(File).GetMethod("Exists").Invoke(null, new[] { path })
   // Not detected (as per spec)
   ```

**Future Enhancement Opportunity**: Implement data flow analysis to track variable assignments and recognize safe patterns across statements.

## 4. Code Fixer Implementation

### 4.1 Fixer Capabilities

The code fixer provides automated remediation for conditionally-banned APIs (File/Directory operations) by wrapping string path arguments.

**Fixer ID**: `CSharpIMultiThreadableTaskCodeFixProvider`  
**Equivalence Key**: `WrapWithGetAbsolutePath`  
**Title**: "Wrap with TaskEnvironment.GetAbsolutePath()"

### 4.2 Fixer Algorithm

```
1. Identify the diagnostic location (method call or object creation)
2. Find the SyntaxNode for the first string argument
3. Check if argument is already wrapped (avoid double-wrapping)
4. Generate new syntax:
   TaskEnvironment.GetAbsolutePath(<original_argument>)
5. Replace argument using DocumentEditor
6. Preserve trivia (comments, whitespace)
```

### 4.3 Fixer Behavior

#### 4.3.1 Supported Transformations

```csharp
// Method calls
File.Exists(path)
→ File.Exists(TaskEnvironment.GetAbsolutePath(path))

// Object creation
new FileInfo(path)
→ new FileInfo(TaskEnvironment.GetAbsolutePath(path))

// Complex expressions
File.ReadAllText(Path.Combine(dir, "file.txt"))
→ File.ReadAllText(TaskEnvironment.GetAbsolutePath(Path.Combine(dir, "file.txt")))
```

#### 4.3.2 Not Offered For

The fixer does NOT appear for:

1. **Always-banned APIs**: `Path.GetFullPath`, `Environment.Exit`, etc.
2. **Already wrapped arguments**: Prevents double-wrapping
3. **Non-string arguments**: Binary files, streams, etc.
4. **Correct task implementations**: No diagnostics = no fixer

### 4.4 Fixer Integration

The fixer registers with:
- **Language**: C#
- **Diagnostic ID**: MSB9999
- **Code Actions**: Single action per diagnostic

Visual Studio integration:
- **Ctrl+.** on diagnostic shows Quick Actions
- **Lightbulb** appears on hover
- **Error List**: Right-click → "Show potential fixes"

## 5. Implementation Details

### 5.1 Key Methods

#### 5.1.1 `GetTypesWithPathMethods()`
Returns immutable array of type names with path-related methods:
```csharp
ImmutableArray.Create(
    "System.IO.File",
    "System.IO.Directory",
    "System.IO.FileInfo",
    "System.IO.DirectoryInfo",
    "System.IO.FileStream",
    "System.IO.StreamReader",
    "System.IO.StreamWriter"
)
```

**Purpose**: Centralize type list for maintainability. Adding new types requires single change.

#### 5.1.2 `GetBannedApiDefinitions()`
Returns immutable array of (DeclarationId, Message) tuples for always-banned APIs.

**Format**: Documentation comment ID format
```
M:System.IO.Path.GetFullPath(System.String)
P:System.Environment.CurrentDirectory
M:System.Diagnostics.Process.Kill
```

**Purpose**: Hardcoded list provides fast lookup and clear documentation of banned APIs.

#### 5.1.3 `IsPathMethodWithUnwrappedArgument()`
Core detection logic for conditionally-banned APIs.

**Input**: 
- `IMethodSymbol method` - The invoked method or constructor
- `ImmutableArray<IArgumentOperation> arguments` - Call arguments

**Output**: `bool` - True if warning should be issued

**Algorithm**:
```csharp
1. Get containing type name
2. Check if type is in GetTypesWithPathMethods()
3. Check if method has string parameters
4. For first string argument:
   - If IsWrappedWithGetAbsolutePath() → return false
   - Else → return true
5. If no string arguments → return false
```

#### 5.1.4 `IsWrappedWithGetAbsolutePath()`
Detects if an argument represents an absolute path.

**Input**: `IOperation operation` - The argument expression

**Output**: `bool` - True if argument is safe (absolute path)

**Checks**:
1. **IInvocationOperation**: Check if calling `TaskEnvironment.GetAbsolutePath()`
2. **IConversionOperation**: Check if converting from `AbsolutePath` type
3. **Recursive**: For conversions, check the operand

**Example**:
```csharp
// Direct call
TaskEnvironment.GetAbsolutePath(path)
→ IInvocationOperation
→ TargetMethod.Name == "GetAbsolutePath"
→ ContainingType == "Microsoft.Build.Framework.TaskEnvironment"
→ Returns true

// Implicit conversion
AbsolutePath absPath = ...;
File.Exists(absPath)
→ IConversionOperation
→ Operand.Type == "Microsoft.Build.Framework.AbsolutePath"
→ Returns true
```

### 5.2 Analysis Flow

```
CompilationStart
└─ BuildBannedApisDictionary()
   └─ Parse GetBannedApiDefinitions() into symbol dictionary

OperationAnalysis (IInvocationOperation, IObjectCreationOperation)
└─ GetContainingType()
   └─ Check if implements IMultiThreadableTask
      ├─ NO → Skip analysis
      └─ YES → Continue

AnalyzeOperationInContext()
├─ Case: IInvocationOperation
│  ├─ IsPathMethodWithUnwrappedArgument() → Warn if true
│  └─ VerifySymbol() → Check always-banned list
│
└─ Case: IObjectCreationOperation
   ├─ IsPathMethodWithUnwrappedArgument() → Warn if true
   └─ VerifySymbol() → Check always-banned list
```

### 5.3 Symbol Resolution

The analyzer uses Roslyn's `DocumentationCommentId` for symbol matching:

```csharp
var symbols = DocumentationCommentId.GetSymbolsForDeclarationId(
    "M:System.IO.Path.GetFullPath(System.String)", 
    compilation
);
```

**Benefits**:
- Framework-agnostic (works across .NET versions)
- Handles overloads correctly
- Supports generic types and nested types

**Trade-off**: Requires exact documentation ID format, which is verbose but precise.

## 6. Testing Strategy

### 6.1 Demo Project Structure

```
src/ThreadSafeTaskAnalyzer/
├─ IMultiThreadableTaskBannedAnalyzer.cs
├─ CSharpIMultiThreadableTaskBannedAnalyzer.cs
├─ CSharpIMultiThreadableTaskCodeFixProvider.cs
└─ VisualStudioDemo/
   ├─ VisualStudioDemo.sln
   ├─ VisualStudioDemo.csproj
   └─ DemoTask.cs
      ├─ ProblematicTask (9 warnings)
      └─ CorrectTask (0 warnings)
```

### 6.2 Demo Task Design

**ProblematicTask**: Demonstrates all unwrapped API patterns
- Each line that should warn has a comment: `// ❌ MSB9999: ...`
- Covers: File, Directory, FileInfo, DirectoryInfo, StreamReader, Path.GetFullPath
- Expected: 9 MSB9999 warnings

**CorrectTask**: Demonstrates proper usage with wrapped paths
- Each line has a comment: `// ✅ NO MSB9999 warning`
- Shows same operations as ProblematicTask but with `TaskEnvironment.GetAbsolutePath()`
- Expected: 0 MSB9999 warnings

### 6.3 Validation Criteria

✅ **Analyzer Accuracy**:
- ProblematicTask: Exactly 9 MSB9999 warnings
- CorrectTask: 0 MSB9999 warnings
- No false positives on wrapped calls

✅ **Code Fixer Functionality**:
- Ctrl+. on warning shows "Wrap with TaskEnvironment.GetAbsolutePath()"
- Applying fix wraps first string argument
- Warning disappears after applying fix
- Trivia (comments, formatting) preserved

✅ **Visual Studio Integration**:
- Green squiggles appear on diagnostics
- Error List shows all warnings with correct locations
- Build output includes MSB9999 diagnostics
- Live analysis works during editing

### 6.4 Manual Testing Procedure

1. Open `VisualStudioDemo.sln` in Visual Studio
2. Open `DemoTask.cs`
3. Verify ProblematicTask shows 9 green squiggles
4. Place cursor on `File.Exists(InputFile)` (line ~34)
5. Press Ctrl+.
6. Select "Wrap with TaskEnvironment.GetAbsolutePath()"
7. Verify transformation: `File.Exists(TaskEnvironment.GetAbsolutePath(InputFile))`
8. Verify green squiggle disappears
9. Verify CorrectTask has no squiggles
10. Build solution and verify Error List matches expectations

## 7. Design Decisions and Rationale

### 7.1 Why Two Detection Modes?

**Always-Banned**: APIs like `Environment.Exit()` are fundamentally unsafe in multithreaded contexts. No amount of path wrapping can make them safe. These must be completely avoided.

**Conditionally-Banned**: File system APIs are necessary for build tasks. The danger is relative path resolution, not the APIs themselves. These are safe when used with absolute paths.

**Trade-off**: More complex implementation, but provides better developer experience. Developers can continue using familiar File/Directory APIs with simple wrapping.

### 7.2 Why Check First String Parameter Only?

**Rationale**: Most file system APIs follow a pattern where the first parameter is the primary path:
```csharp
File.ReadAllText(string path)
File.Copy(string sourceFileName, string destFileName)
Directory.CreateDirectory(string path)
```

For methods with multiple path parameters (e.g., `File.Copy`), wrapping the first parameter is the minimum requirement. The second parameter often inherits safety from the first or is less critical.

**Future Enhancement**: Extend to check all string parameters. Requires more sophisticated parameter name analysis ("path", "fileName", "directory", etc.).

### 7.3 Why Not Data Flow Analysis?

**Current Limitation**: The analyzer doesn't track variable assignments:
```csharp
var absPath = TaskEnvironment.GetAbsolutePath(path);
File.Exists(absPath) // Still warns
```

**Rationale**: 
- Data flow analysis is computationally expensive
- Increases implementation complexity significantly
- May impact IDE performance during live editing
- Current approach handles 90% of cases with simple inline wrapping

**When to Add**: If user feedback indicates common patterns are not supported, implement basic data flow tracking for local variable assignments within the same method.

### 7.4 Why Hardcoded API List?

**Alternative Considered**: Parse from external file (IMultiThreadableTask_BannedApis.txt)

**Decision**: Hardcode in `GetBannedApiDefinitions()`

**Rationale**:
- Single source of truth in code
- Better discoverability and maintainability
- Compile-time validation
- No external file dependencies
- Easier to document with comments

**Trade-off**: Changing banned APIs requires recompiling analyzer, but this is rare and intentional (forces deliberate changes).

### 7.5 Why Abstract Base Class?

**Design**: `IMultiThreadableTaskBannedAnalyzer` (abstract) with C# implementation

**Rationale**:
- Supports future VB.NET implementation with shared logic
- Platform-specific syntax handling (C# vs VB.NET) in derived classes
- Follows Roslyn analyzer pattern (CSharpSyntaxNode vs VisualBasicSyntaxNode)

**Trade-off**: More complex structure, but provides scalability for multi-language support.

## 8. Performance Considerations

### 8.1 Optimization Strategies

1. **Early Exit**: Check `IMultiThreadableTask` implementation before any analysis
2. **Immutable Collections**: Use `ImmutableArray` for type lists and banned APIs
3. **Lazy Evaluation**: Build banned API dictionary once per compilation
4. **Targeted Registration**: Only register for relevant operation kinds (Invocation, ObjectCreation)

### 8.2 Expected Impact

- **Compilation Time**: Negligible (<1% increase for typical projects)
- **Live Analysis**: Minimal impact, scoped to IMultiThreadableTask classes only
- **Memory**: Small (< 1MB for banned API dictionary)

### 8.3 Benchmarking Recommendations

For production deployment, measure:
- Compilation time with/without analyzer on MSBuild.sln
- Live analysis responsiveness in Visual Studio
- Memory usage during incremental builds

Target: <5% overhead on typical MSBuild task projects.

## 9. Deployment and Distribution

### 9.1 Packaging

The analyzer should be packaged as:
- **NuGet Package**: `Microsoft.Build.Utilities.MultiThreadableTaskAnalyzer`
- **Assembly Name**: `Microsoft.Build.Utilities.MultiThreadableTaskAnalyzer.dll`
- **Target Framework**: netstandard2.0 (compatible with both Framework and Core)

### 9.2 Integration with MSBuild Projects

Task projects reference the analyzer via:
```xml
<ItemGroup>
  <Analyzer Include="path\to\Microsoft.Build.Utilities.MultiThreadableTaskAnalyzer.dll" />
</ItemGroup>
```

Or via package reference:
```xml
<PackageReference Include="Microsoft.Build.Utilities.MultiThreadableTaskAnalyzer" Version="1.0.0">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
```

### 9.3 Configuration

Support `.editorconfig` for customization:
```ini
# Disable analyzer for specific projects
dotnet_diagnostic.MSB9999.severity = none

# Treat as error instead of warning
dotnet_diagnostic.MSB9999.severity = error
```

## 10. Future Enhancements

### 10.1 Short-Term Improvements

1. **Enhanced Wrapping Detection**
   - Track local variable assignments within same method
   - Recognize method return values that provide absolute paths
   - Support field/property assignments

2. **Multi-Parameter Analysis**
   - Check all string parameters, not just the first
   - Parameter name heuristics ("path", "fileName", "directory")
   - Special handling for specific method signatures

3. **Better Diagnostics**
   - Different message for File vs Directory vs constructors
   - Suggest specific alternative (e.g., "use TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath")
   - Code examples in diagnostic messages

### 10.2 Long-Term Enhancements

1. **VB.NET Support**
   - Implement `VBIMultiThreadableTaskBannedAnalyzer`
   - VB.NET code fixer provider
   - Shared test suite

2. **Advanced Data Flow Analysis**
   - Inter-procedural analysis (track across method boundaries)
   - Handle LINQ expressions and lambda captures
   - Detect path mutations (Path.Combine, string concatenation)

3. **Custom Attributes**
   - `[AbsolutePath]` attribute to mark parameters/returns
   - Suppress warnings with justification: `[SuppressMessage("MSB9999", Justification = "...")]`
   - Analyzer honors these annotations

4. **IDE Enhancements**
   - Bulk fix: "Wrap all unsafe paths in file"
   - Preview changes before applying
   - Configurable wrapping style (inline vs variable)

### 10.3 Integration with MSBuild Build Check

Consider migrating some checks to MSBuild BuildCheck for:
- Build-time enforcement without analyzer dependency
- Consistent checks across build environments
- Integration with MSBuild logging infrastructure

## 11. Known Limitations

### 11.1 Current Limitations

1. **Variable Tracking**: Doesn't follow variable assignments
2. **Single Parameter**: Only checks first string parameter
3. **Reflection**: Cannot detect dynamic API calls
4. **P/Invoke**: Native API calls not analyzed
5. **Static Fields**: Doesn't detect problematic static state usage

### 11.2 Acceptable Limitations (Per Spec)

From mtspec.md:
> "The analyzers rely on **static code analysis** and may not catch all dynamic scenarios (such as reflection-based API calls)."

These are documented and expected limitations, not implementation defects.

### 11.3 Workarounds

For unsupported patterns, developers should:
1. Manually wrap paths with `TaskEnvironment.GetAbsolutePath()`
2. Use `#pragma warning disable MSB9999` with justification comment
3. Report feedback for common patterns to guide future enhancements

## 12. Distribution and Consumption

### 12.1 Overview

This section discusses how the IMultiThreadableTask analyzer will be distributed to customers and automatically enabled in their projects. The primary goal is to provide seamless integration that "just works" for task authors while offering flexibility for advanced scenarios.

### 12.2 Proposed Distribution Model: Ship with Microsoft.Build.Utilities.Core

**Primary Approach**: The analyzer should be packaged and distributed as part of the `Microsoft.Build.Utilities.Core` NuGet package.

#### 12.2.1 Rationale

1. **Natural Discovery**: Task authors already reference `Microsoft.Build.Utilities.Core` to inherit from `Task` or `MultiThreadableTask` base classes
2. **Zero Configuration**: No additional package references or setup required
3. **Version Alignment**: Analyzer version stays synchronized with the MSBuild APIs it validates
4. **Consistent Developer Experience**: Aligns with how other Microsoft analyzers ship (e.g., Platform Compatibility Analyzer ships with .NET SDK)
5. **Automatic Updates**: Task authors get analyzer updates when they update their MSBuild package reference

#### 12.2.2 Implementation Approach

The analyzer DLL should be included in the `Microsoft.Build.Utilities.Core` NuGet package with proper MSBuild integration:

**Package Structure**:
```
Microsoft.Build.Utilities.Core.nupkg
├── lib/
│   ├── netstandard2.0/
│   │   └── Microsoft.Build.Utilities.Core.dll
│   └── net8.0/
│       └── Microsoft.Build.Utilities.Core.dll
├── analyzers/
│   └── dotnet/
│       └── cs/
│           ├── Microsoft.Build.Utilities.Analyzer.dll
│           └── (analyzer dependencies)
└── build/
    ├── Microsoft.Build.Utilities.Core.props
    └── Microsoft.Build.Utilities.Core.targets
```

**Auto-Enable Mechanism** (`build/Microsoft.Build.Utilities.Core.props`):
```xml
<Project>
  <ItemGroup>
    <Analyzer Include="$(MSBuildThisFileDirectory)..\analyzers\dotnet\cs\Microsoft.Build.Utilities.Analyzer.dll" />
  </ItemGroup>
</Project>
```

This automatically adds the analyzer to any project that references `Microsoft.Build.Utilities.Core`.

#### 12.2.3 Activation Behavior

- **Automatic Activation**: Analyzer activates automatically for any project referencing `Microsoft.Build.Utilities.Core`
- **Scoped Analysis**: Only analyzes code within types implementing `IMultiThreadableTask` (minimal performance impact)
- **No False Positives**: Regular tasks not implementing `IMultiThreadableTask` are not analyzed

### 12.3 Alternative Distribution Approaches (Considered but Not Recommended)

#### 12.3.1 Standalone NuGet Analyzer Package

**Approach**: Publish as separate package like `Microsoft.Build.Analyzers` or `Microsoft.Build.ThreadSafety.Analyzers`

**Pros**:
- Clear separation between runtime and analyzer
- Explicit opt-in
- Independent versioning

**Cons**:
- **Discovery Problem**: Task authors may not know the package exists
- **Extra Step**: Requires manual package reference
- **Version Skew**: Analyzer and runtime API versions can drift apart
- **Inconsistent Adoption**: Some teams will forget to add it

**Verdict**: ❌ Not recommended - Creates friction and reduces adoption

#### 12.3.2 Visual Studio Extension

**Approach**: Ship as VS extension installed separately

**Pros**:
- Works across all projects without package reference
- Can provide rich VS-specific UI

**Cons**:
- **CLI Builds Miss Analysis**: Doesn't run in `dotnet build` or CI/CD
- **Inconsistent Experience**: Different behavior between VS and CLI
- **Deployment Complexity**: Requires separate installation/updates
- **Not Cross-Platform**: Doesn't help VS Code or command-line users

**Verdict**: ❌ Not recommended - Fragmentary solution

#### 12.3.3 MSBuild SDK

**Approach**: Include in an MSBuild SDK (like `Microsoft.Build.NoTargets`)

**Pros**:
- Follows SDK pattern
- Clear opt-in

**Cons**:
- **Requires SDK Import**: Extra step for task authors
- **Not Standard**: Most task projects don't use custom SDKs
- **Visibility**: Harder to discover than package reference

**Verdict**: ❌ Not recommended - Adds unnecessary complexity

### 12.4 Opt-Out Mechanisms

Even with automatic enablement, developers need escape hatches for legitimate scenarios.

#### 12.4.1 Per-Project Opt-Out

Disable the analyzer entirely for a project:

```xml
<Project>
  <PropertyGroup>
    <EnableMSBuildThreadSafetyAnalyzer>false</EnableMSBuildThreadSafetyAnalyzer>
  </PropertyGroup>
</Project>
```

Implementation in `Microsoft.Build.Utilities.Core.props`:
```xml
<ItemGroup Condition="'$(EnableMSBuildThreadSafetyAnalyzer)' != 'false'">
  <Analyzer Include="$(MSBuildThisFileDirectory)..\analyzers\dotnet\cs\Microsoft.Build.Utilities.Analyzer.dll" />
</ItemGroup>
```

#### 12.4.2 Per-File Opt-Out

Disable for a specific file:

```csharp
#pragma warning disable MSB9999
// file content
#pragma warning restore MSB9999
```

#### 12.4.3 Per-Diagnostic Opt-Out

Suppress specific warnings:

```csharp
[SuppressMessage("MSBuild.ThreadSafety", "MSB9999", Justification = "Legacy code - refactoring in progress")]
public bool Execute() { ... }
```

#### 12.4.4 Change Severity in .editorconfig

```ini
[*.cs]
# Treat as error
dotnet_diagnostic.MSB9999.severity = error

# Disable entirely
dotnet_diagnostic.MSB9999.severity = none

# Info only
dotnet_diagnostic.MSB9999.severity = suggestion
```

### 12.5 Version Compatibility Considerations

#### 12.5.1 Minimum MSBuild Version

- **Analyzer Targets**: MSBuild 15.0+ (Visual Studio 2017+)
  - Roslyn analyzers work with all modern MSBuild versions
  - No runtime dependency on new MSBuild features

- **`IMultiThreadableTask` Runtime**: MSBuild 17.15+
  - Interface and `TaskEnvironment` API introduced in 17.15
  - Tasks using the interface can't load in older MSBuild

#### 12.5.2 Graceful Degradation

For projects targeting older MSBuild versions (<17.15):

1. **Analyzer Still Runs**: Validates code even if `IMultiThreadableTask` doesn't exist at runtime
2. **Build Compatibility**: Projects can build with newer Utilities package but run on older MSBuild
3. **Conditional Compilation**: Task authors can use `#if` to provide fallback implementations

Example:
```csharp
#if SUPPORTS_MULTITHREADING
public class MyTask : MultiThreadableTask
{
    public override bool Execute()
    {
        string absolutePath = TaskEnvironment.GetAbsolutePath(RelativePath);
        // ...
    }
}
#else
public class MyTask : Task
{
    public override bool Execute()
    {
        string absolutePath = Path.GetFullPath(RelativePath);
        // ...
    }
}
#endif
```

#### 12.5.3 Forward Compatibility

- **New Banned APIs**: Future MSBuild versions can add new banned APIs by updating the analyzer
- **Package Updates**: Task authors get new rules when they update `Microsoft.Build.Utilities.Core`
- **No Breaking Changes**: Adding warnings is non-breaking (severity can be adjusted)

### 12.6 Open Questions for Review

These questions should be addressed during PM and engineering review:

#### 12.6.1 Distribution Questions

1. **Should the analyzer ship in preview builds or wait for stable release?**
   - Recommendation: Ship in preview to get early feedback from task authors

2. **Should there be a separate "strict mode" analyzer with additional checks?**
   - Example: Warn on any `Path.Combine` usage even with absolute paths
   - Recommendation: Start conservative, add opt-in strict mode later if needed

3. **Should we provide analyzer suppressions for known-safe patterns?**
   - Example: `File.Exists` with string literal absolute paths like `"C:\\Windows\\System32\\cmd.exe"`
   - Recommendation: Yes, but in a future iteration (requires constant analysis)

#### 12.6.2 User Experience Questions

1. **What should happen if user references both old and new Utilities.Core?**
   - Scenario: Transitive dependency on old version, direct on new version
   - Recommendation: Newest version wins (standard NuGet behavior)

2. **Should there be telemetry to track analyzer warnings?**
   - Useful for understanding adoption and common mistakes
   - Privacy considerations required

3. **Should Code Fixer be enabled by default or require opt-in?**
   - Current: Enabled by default
   - Alternative: Require explicit gesture (right-click menu)
   - Recommendation: Keep enabled - follows standard Roslyn patterns

#### 12.6.3 Documentation Questions

1. **Where should customer-facing documentation live?**
   - Microsoft Learn articles
   - In-package README
   - Both

2. **Should there be a migration guide for existing non-thread-safe tasks?**
   - Recommendation: Yes - critical for adoption

3. **Should there be samples in the MSBuild samples repository?**
   - Recommendation: Yes - both correct and incorrect usage examples

### 12.7 Success Metrics

To measure effectiveness of the analyzer distribution:

1. **Adoption Rate**: % of task projects using `IMultiThreadableTask`
2. **Warning Frequency**: How often MSB9999 appears in builds
3. **Suppression Rate**: How often warnings are suppressed (high rate indicates false positives)
4. **Code Fixer Usage**: How often automated fixes are applied
5. **Support Tickets**: Number of analyzer-related issues reported

### 12.8 Rollout Plan Recommendation

1. **Phase 1 (Preview)**: Ship in MSBuild 17.15 Preview
   - Gather feedback from early adopters
   - Default severity: Warning

2. **Phase 2 (Stable)**: Ship in MSBuild 17.15 RTM
   - Refine based on preview feedback
   - Add migration documentation

3. **Phase 3 (Enforcement)**: Future version (17.16+)
   - Consider increasing severity to Error for new projects
   - Provide bulk suppression tools for legacy code

4. **Phase 4 (Extended)**: Future versions
   - Add strict mode
   - Add constant analysis for literal paths
   - Expand to cover more threading issues

---

## 13. Maintenance and Support

### 13.1 Updating Banned APIs

To add new banned APIs:

1. Add entry to `GetBannedApiDefinitions()`:
   ```csharp
   ("M:Namespace.Type.Method(ParamTypes)", "Reason - use alternative")
   ```

2. Update tests to cover new API
3. Update demo if representative example needed
4. Document in release notes

### 12.2 Updating Path-Related Types

To add new file system types (e.g., `System.IO.Pipes.NamedPipeClientStream`):

1. Add to `GetTypesWithPathMethods()`:
   ```csharp
   "System.IO.Pipes.NamedPipeClientStream"
   ```

2. No other changes needed (automatic detection)
3. Update tests and demo

### 12.3 Diagnostic ID Allocation

MSB9999 is allocated for this analyzer. Future related diagnostics should use:
- MSB4261, MSB4262, etc. for related thread-safety issues
- Different IDs prevent conflicting suppressions

## 14. References

### 14.1 Related Specifications

- [thread-safe-tasks-api-analysis.md](thread-safe-tasks-api-analysis.md) - Thread-Safe Tasks API Analysis Reference
- [thread-safe-tasks.md](thread-safe-tasks.md) - Thread-Safe Tasks Overview
- [multithreaded-msbuild.md](multithreaded-msbuild.md) - Multithreaded MSBuild Specification

### 14.2 Roslyn Documentation

- [Analyzer Development](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/Analyzer%20Actions%20Semantics.md)
- [Code Fix Providers](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/FixAllProvider.md)
- [Symbol Documentation IDs](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/xmldoc/)

### 14.3 Testing Resources

- Demo Project: `src/ThreadSafeTaskAnalyzer/VisualStudioDemo/`
- Expected Output: 9 warnings in ProblematicTask, 0 in CorrectTask

---

## Appendix A: Complete API Reference

### Always-Banned APIs (Documentation IDs)

```
Path APIs:
  M:System.IO.Path.GetFullPath(System.String)
  M:System.IO.Path.GetFullPath(System.String,System.String)

Environment APIs:
  P:System.Environment.CurrentDirectory
  M:System.Environment.SetEnvironmentVariable(System.String,System.String)
  M:System.Environment.SetEnvironmentVariable(System.String,System.String,System.EnvironmentVariableTarget)
  M:System.Environment.Exit(System.Int32)
  M:System.Environment.FailFast(System.String)
  M:System.Environment.FailFast(System.String,System.Exception)
  M:System.Environment.FailFast(System.String,System.Exception,System.String)

Process APIs:
  M:System.Diagnostics.Process.Kill
  M:System.Diagnostics.Process.Kill(System.Boolean)
  M:System.Diagnostics.Process.Start(System.String)
  M:System.Diagnostics.Process.Start(System.String,System.String)
  M:System.Diagnostics.ProcessStartInfo.#ctor
  M:System.Diagnostics.ProcessStartInfo.#ctor(System.String)
  M:System.Diagnostics.ProcessStartInfo.#ctor(System.String,System.String)

ThreadPool APIs:
  M:System.Threading.ThreadPool.SetMinThreads(System.Int32,System.Int32)
  M:System.Threading.ThreadPool.SetMaxThreads(System.Int32,System.Int32)

Culture APIs:
  P:System.Globalization.CultureInfo.DefaultThreadCurrentCulture
  P:System.Globalization.CultureInfo.DefaultThreadCurrentUICulture

Assembly Loading APIs:
  M:System.Reflection.Assembly.LoadFrom(System.String)
  M:System.Reflection.Assembly.LoadFile(System.String)
  M:System.Reflection.Assembly.Load(System.String)
  M:System.Reflection.Assembly.Load(System.Byte[])
  M:System.Reflection.Assembly.Load(System.Byte[],System.Byte[])
  M:System.Reflection.Assembly.LoadWithPartialName(System.String)
  M:System.Activator.CreateInstanceFrom(System.String,System.String)
  M:System.Activator.CreateInstance(System.String,System.String)
  M:System.AppDomain.Load(System.String)
  M:System.AppDomain.Load(System.Byte[])
  M:System.AppDomain.CreateInstanceFrom(System.String,System.String)
  M:System.AppDomain.CreateInstance(System.String,System.String)

Console APIs:
  P:System.Console.Out
  P:System.Console.Error
  P:System.Console.In
  M:System.Console.Write(System.String)
  M:System.Console.WriteLine(System.String)
  M:System.Console.ReadLine
  M:System.Console.ReadKey
```

### Conditionally-Banned Types (Smart Detection)

```
System.IO.File
System.IO.Directory
System.IO.FileInfo
System.IO.DirectoryInfo
System.IO.FileStream
System.IO.StreamReader
System.IO.StreamWriter
```

---

**Document Version**: 1.0  
**Last Updated**: September 30, 2025  
**Author**: MSBuild Team  
**Status**: Implementation Guide
