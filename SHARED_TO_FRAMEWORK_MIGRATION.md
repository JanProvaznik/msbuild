# Shared Files to Framework Migration Analysis

## Goal
Move ALL shared files from `src/Shared/` to `src/Framework/` so that:
- Utilities, Tasks, Build, and MSBuild projects get shared code through Framework only
- MSBuildTaskHost continues to compile from Shared (cannot depend on Framework)
- Shared files remain in Shared directory for MSBuildTaskHost compilation

## Current State

### Projects Referencing Shared Files
- **Utilities**: 25 shared files
- **Tasks**: 62 shared files  
- **Build**: 76 shared files
- **MSBuild**: 53 shared files
- **MSBuildTaskHost**: 47 shared files
- **Framework**: 5 shared files (moved files)

**Total unique shared files**: ~100 files

## Issues Identified

### 1. Public Visibility (Should be Internal)
These shared files have public types:
- `FileMatcher.cs`
- `ReadOnlyEmptyDictionary.cs`
- `IMSBuildElementLocation.cs`
- `NodeEngineShutdownReason.cs`
- `FileSystem/WindowsNative.cs`

**Action Required**: Flag for review - shared files should be internal only.

### 2. Shared Files Referencing Utilities Types
These files use types from Microsoft.Build.Utilities:
- `AssemblyFolders/AssemblyFoldersFromConfig.cs` - uses Task, Utilities
- `PlatformNegotiation.cs` - uses TaskLoggingHelper
- `PropertyParser.cs` - uses TaskLoggingHelper
- `TaskLoggingHelperExtension.cs` - uses TaskLoggingHelper

**Issue**: Framework cannot reference Utilities (circular dependency).

**Solutions**:
1. Keep these specific files in Shared only (don't move to Framework)
2. Move TaskLoggingHelper to Framework (but it's complex)
3. Refactor to remove Utilities dependency

### 3. Files with Complex Dependencies
Many shared files reference each other and depend on:
- `ErrorUtilities` - foundational error handling
- `ResourceUtilities` - resource string management
- `FileUtilities` - file system operations
- `IFileSystem` - abstraction layer

## Migration Strategy

### Phase 1: Copy All Shared Files to Framework
1. Copy all ~100 shared files from `src/Shared/` to `src/Framework/`
2. Change namespace from `Microsoft.Build.Shared` to `Microsoft.Build.Framework`
3. Add necessary using statements
4. Keep original files in `src/Shared/` for MSBuildTaskHost

### Phase 2: Remove Shared References from Projects
1. Remove ALL `<Compile Include="..\Shared\..."/>` from:
   - Utilities/Microsoft.Build.Utilities.csproj
   - Tasks/Microsoft.Build.Tasks.csproj
   - Build/Microsoft.Build.csproj
   - MSBuild/MSBuild.csproj
2. These projects will get shared code through Framework dependency

### Phase 3: Handle Files with Utilities Dependencies
Files that reference Utilities types cannot move to Framework (circular dependency):
- Keep in Shared only
- Projects referencing them will need to keep those specific Shared includes
- OR refactor to remove Utilities dependency

### Phase 4: Validate
1. Ensure all shared files are internal (or document exceptions)
2. Ensure Utilities doesn't expose types needed by Shared
3. Build and test

## Estimated Impact
- ~100 files copied to Framework
- ~200 Shared references removed from 4 projects
- Significant reduction in compilation duplication
- Cleaner architectural separation

## Critical Blocker: Circular Dependency

**Utilities DOES contain types referenced by Shared!**

### Shared Files Referencing Utilities Types

1. `AssemblyFolders/AssemblyFoldersFromConfig.cs` - uses `Microsoft.Build.Utilities.Task`
2. `PlatformNegotiation.cs` - uses `Microsoft.Build.Utilities.TaskLoggingHelper`
3. `PropertyParser.cs` - uses `Microsoft.Build.Utilities.TaskLoggingHelper`
4. `TaskLoggingHelperExtension.cs` - uses `Microsoft.Build.Utilities.TaskLoggingHelper`

### The Circular Dependency Problem

```
Framework → Shared files (PropertyParser, etc.) → Utilities.TaskLoggingHelper (public API)
Utilities → Framework
```

### Why TaskLoggingHelper Cannot Move

`TaskLoggingHelper` is currently in `src/Shared/TaskLoggingHelper.cs` with conditional compilation:
- In Utilities DLL: `public class` in `Microsoft.Build.Utilities` namespace
- In Build DLL: `internal class` in `Microsoft.Build.BackEnd` namespace

**TaskLoggingHelper is PUBLIC API** - Tasks inherit from Task class which exposes TaskLoggingHelper through the Log property. Moving it to Framework would be a **breaking change**.

### Cannot Move These Files to Framework

Files that depend on TaskLoggingHelper cannot move without:
1. Moving TaskLoggingHelper to Framework (breaking change)
2. Refactoring to remove Utilities dependency (major work)
3. Keeping them in Shared only (partial migration)

## Recommendation

Given the circular dependency, the complete migration is **blocked** unless:
1. Major refactoring to move TaskLoggingHelper infrastructure to Framework
2. Accept partial migration - move files without Utilities dependencies only

## Current Achievement

Successfully moved 5 files with zero dependencies:
- CanonicalError.cs
- FileDelegates.cs
- VersionUtilities.cs
- AssemblyFolderCollection.cs
- AssemblyFolderItem.cs

## Conclusion: Migration Blocked

**Goal**: Remove all Shared file references from Utilities, Tasks, Build, MSBuild projects

**Status**: BLOCKED by architectural constraints

**Reason**: Shared files reference `TaskLoggingHelper` which is:
1. Part of Utilities public API (`Microsoft.Build.Utilities.TaskLoggingHelper`)
2. Cannot move to Framework without breaking changes
3. Creates circular dependency: Framework → Utilities types → Framework

**Files Affected**: At least 4 shared files directly depend on TaskLoggingHelper, many more transitively

**Remaining 112 files cannot move** due to:
- Direct or indirect Utilities dependencies (TaskLoggingHelper)
- Complex interdependencies
- Would require breaking API changes

## Recommendations

### Option 1: Accept Current State (Recommended)
- Keep 5 files moved to Framework (11.1% reduction)
- Accept that full migration is not feasible without breaking changes
- Document the architectural constraint

### Option 2: Breaking Change Migration
**If breaking changes are acceptable:**
1. Move TaskLoggingHelper to Framework as public API
2. Update namespace from `Microsoft.Build.Utilities` to `Microsoft.Build.Framework`
3. This allows moving PropertyParser, PlatformNegotiation, and related files
4. Major version bump required
5. All external tasks would need recompilation

### Option 3: Refactoring
**Long-term solution:**
1. Refactor Shared files to not depend on TaskLoggingHelper
2. Create abstractions/interfaces
3. Significant engineering effort
4. No breaking changes

## Answer to User Questions

1. **Can all shared files move to Framework?**
   - NO - Blocked by circular dependency through TaskLoggingHelper

2. **Does Utilities contain anything referenced by Shared?**
   - YES - TaskLoggingHelper (public API)
   - PropertyParser, PlatformNegotiation, TaskLoggingHelperExtension use it

3. **Are all shared files internal?**
   - NO - Some have public visibility:
     - FileMatcher.cs
     - ReadOnlyEmptyDictionary.cs
     - IMSBuildElementLocation.cs
     - NodeEngineShutdownReason.cs
     - FileSystem/WindowsNative.cs
