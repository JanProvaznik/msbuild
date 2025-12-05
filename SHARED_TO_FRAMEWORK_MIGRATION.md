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

Shared files that reference Utilities types:
- `AssemblyFolders/AssemblyFoldersFromConfig.cs` - uses `Microsoft.Build.Utilities.Task`
- `PlatformNegotiation.cs` - uses `Microsoft.Build.Utilities.TaskLoggingHelper`
- `PropertyParser.cs` - uses `Microsoft.Build.Utilities.TaskLoggingHelper`
- `TaskLoggingHelperExtension.cs` - uses `Microsoft.Build.Utilities.TaskLoggingHelper`

**This creates a circular dependency:**
```
Framework → Shared files → Utilities types
Utilities → Framework
```

**Cannot move these files to Framework** unless:
1. TaskLoggingHelper and related types move to Framework first (major refactoring)
2. OR these files are refactored to remove Utilities dependency
3. OR these specific files stay in Shared only

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

**Remaining 112 files cannot move** due to:
- Direct or indirect Utilities dependencies
- Complex interdependencies
- Would require moving TaskLoggingHelper and related infrastructure to Framework first

## Risks
1. Large change scope (~117 files)
2. Namespace changes may affect serialization
3. Files with Utilities dependencies create circular dependency
4. Build time may increase initially
