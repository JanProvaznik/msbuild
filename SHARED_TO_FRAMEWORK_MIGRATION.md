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

## Conclusion: Migration Mostly Feasible

**Goal**: Remove all Shared file references from Utilities, Tasks, Build, MSBuild projects

**Status**: MOSTLY FEASIBLE - Only 4 files blocked

**Key Finding**: ErrorUtilities, ResourceUtilities, FileUtilities have NO Utilities dependencies!
- They only depend on Framework
- Can be moved to Framework
- This unblocks ~50+ other files

**TaskLoggingHelper Block**: Only affects 4 files
1. TaskLoggingHelper.cs - references Utilities types (public API)
2. PropertyParser.cs - uses TaskLoggingHelper
3. PlatformNegotiation.cs - uses TaskLoggingHelper
4. TaskLoggingHelperExtension.cs - uses TaskLoggingHelper
5. AssemblyFoldersFromConfig.cs - uses Task from Utilities

**Remaining 112 files CAN move** except these 4-5:
- ErrorUtilities has no circular dependency
- Move ErrorUtilities, ResourceUtilities, FileUtilities first
- Then move all dependent files
- Only exclude TaskLoggingHelper-dependent files

## Recommendations

### Option 1: Large-Scale Migration (Recommended)
**Move ~54 Category 1 files to Framework:**
1. Move ErrorUtilities, ResourceUtilities, FileUtilities to Framework (foundational)
2. Move all Category 1 files EXCEPT 4-5 TaskLoggingHelper-dependent ones
3. Update all project references
4. **Benefits**: ~46% reduction in duplication (54 of 117 files)
5. **No breaking changes** - only internal reorganization

**Exclude from migration:**
- TaskLoggingHelper.cs
- PropertyParser.cs
- PlatformNegotiation.cs
- TaskLoggingHelperExtension.cs
- AssemblyFoldersFromConfig.cs

### Option 2: Conservative Approach
- Keep current 5 files moved (11.1% reduction)
- Move additional simple files (RegistryDelegates, RegistryHelper, etc.)
- ~15-20% reduction
- Lower risk

### Option 3: Breaking Change for Complete Migration
**If breaking changes acceptable:**
1. Move TaskLoggingHelper to Framework as public API
2. Update namespace from `Microsoft.Build.Utilities` to `Microsoft.Build.Framework`
3. Move remaining 4-5 files
4. Requires major version bump
5. **Achieves 100% migration**

## Answer to User Questions

1. **Can all shared files move to Framework?**
   - MOSTLY YES - Only TaskLoggingHelper-dependent files are blocked (4 files)
   - ErrorUtilities, ResourceUtilities, FileUtilities have no Utilities dependencies
   - ~54 Category 1 files can move (excluding 4 TaskLoggingHelper-dependent ones)

2. **Does Utilities contain anything referenced by Shared?**
   - YES - TaskLoggingHelper (public API) only
   - Only affects: PropertyParser, PlatformNegotiation, TaskLoggingHelperExtension, AssemblyFoldersFromConfig

3. **Are all shared files internal?**
   - CORRECTED: Most are internal
   - Only 2 shared files have public visibility:
     - IMSBuildElementLocation.cs (only compiled in Framework, not truly shared)
     - NodeEngineShutdownReason.cs (can move file, namespace stays)
   - FileMatcher, ReadOnlyEmptyDictionary, WindowsNative are INTERNAL (previous analysis was wrong)
