# IMultiThreadableTask Analyzer Demo

## Purpose

This demo project validates the IMultiThreadableTask analyzer and code fixer functionality in Visual Studio. It demonstrates detection of unsafe File/Directory API usage and automated remediation through code fixes.

## Quick Start

1. **Open the solution**: `VisualStudioDemo.sln`

2. **Open the demo file**: `DemoTask.cs`

3. **Observe warnings**: ProblematicTask should show 9 green squiggles (MSB9999 diagnostics)

4. **Test code fixer**:
   - Place cursor on any warning
   - Press `Ctrl+.` (or click the lightbulb 💡)
   - Select "Wrap with TaskEnvironment.GetAbsolutePath()"
   - Verify warning disappears

## Expected Results

### ProblematicTask (Lines 12-68)

**9 MSB9999 warnings** on unwrapped File/Directory API calls:

- Line 34: `File.Exists(InputFile)`
- Line 37: `Directory.Exists(OutputDirectory)`
- Line 40: `Directory.CreateDirectory(OutputDirectory)`
- Line 44: `File.ReadAllText(InputFile)`
- Line 47: `Path.GetFullPath(InputFile)`
- Line 50: `new FileInfo(InputFile)`
- Line 53: `new DirectoryInfo(OutputDirectory)`
- Line 56: `new StreamReader(InputFile)`
- Line 62: `File.WriteAllText(...)`

### CorrectTask (Lines 70-105)

**0 MSB9999 warnings** - all paths wrapped with `TaskEnvironment.GetAbsolutePath()`

## Build Verification

```powershell
dotnet build VisualStudioDemo.csproj
```

**Expected**: 9 MSB9999 warnings

## What This Demonstrates

### Smart Path Detection
- ❌ `File.Exists(path)` → Warning
- ✅ `File.Exists(TaskEnvironment.GetAbsolutePath(path))` → No warning

### Always-Banned APIs
- ❌ `Path.GetFullPath(path)` → Always warns
- ✅ `TaskEnvironment.GetAbsolutePath(path)` → Use this instead

### Code Fixer Integration
Automated remediation via "Wrap with TaskEnvironment.GetAbsolutePath()" quick action

## Reference

- [analyzer-spec.md](../../../analyzer-spec.md) - Full specification
- [mtspec.md](../../../mtspec.md) - Thread-Safe Tasks API reference
