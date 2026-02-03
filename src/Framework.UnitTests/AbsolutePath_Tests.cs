// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests
{
    public class AbsolutePath_Tests
    {
        private static AbsolutePath GetTestBasePath()
        {
            string baseDirectory = Path.Combine(Path.GetTempPath(), "abspath_test_base");
            return new AbsolutePath(baseDirectory, ignoreRootedCheck: false);
        }

        private static void ValidatePathAcceptance(string path, bool shouldBeAccepted)
        {
            if (shouldBeAccepted)
            {
                // Should not throw - these are truly absolute paths
                var absolutePath = new AbsolutePath(path);
                absolutePath.Value.ShouldBe(path);
            }
            else
            {
                // Should throw ArgumentException for any non-absolute path
                Should.Throw<System.ArgumentException>(() => new AbsolutePath(path, ignoreRootedCheck: false),
                    $"Path '{path}' should be rejected as it's not a true absolute path");
            }
        }

        [Fact]
        public void AbsolutePath_FromAbsolutePath_ShouldPreservePath()
        {
            string absolutePathString = Path.GetTempPath();
            var absolutePath = new AbsolutePath(absolutePathString);

            absolutePath.Value.ShouldBe(absolutePathString);
            Path.IsPathRooted(absolutePath.Value).ShouldBeTrue();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AbsolutePath_NullOrEmpty_ShouldThrow(string? path)
        {
            Should.Throw<ArgumentException>(() => new AbsolutePath(path!));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AbsolutePath_NullOrEmptyWithBasePath_ShouldThrow(string? path)
        {
            var basePath = GetTestBasePath();
            Should.Throw<ArgumentException>(() => new AbsolutePath(path!, basePath));
        }

        [Theory]
        [InlineData("subfolder")]
        [InlineData("deep/nested/path")]
        [InlineData(".")]
        [InlineData("..")]
        public void AbsolutePath_FromRelativePath_ShouldResolveAgainstBase(string relativePath)
        {
            string baseDirectory = Path.Combine(Path.GetTempPath(), "testfolder");
            var basePath = new AbsolutePath(baseDirectory);
            var absolutePath = new AbsolutePath(relativePath, basePath);

            Path.IsPathRooted(absolutePath.Value).ShouldBeTrue();
            
            string expectedPath = Path.Combine(baseDirectory, relativePath);
            absolutePath.Value.ShouldBe(expectedPath);
        }

        [Fact]
        public void AbsolutePath_Equality_ShouldWorkCorrectly()
        {
            string testPath = Path.GetTempPath();
            var path1 = new AbsolutePath(testPath);
            var path2 = new AbsolutePath(testPath);
            var differentPath = new AbsolutePath(Path.Combine(testPath, "different"));

            path1.ShouldBe(path2);
            (path1 == path2).ShouldBeTrue();
            path1.ShouldNotBe(differentPath);
            (path1 == differentPath).ShouldBeFalse();
        }

        [Fact]
        public void AbsolutePath_Inequality_ShouldWorkCorrectly()
        {
            string testPath = Path.GetTempPath();
            var path1 = new AbsolutePath(testPath);
            var differentPath = new AbsolutePath(Path.Combine(testPath, "different"));

            (path1 != differentPath).ShouldBeTrue();
#pragma warning disable CS1718 // Comparison made to same variable
            (path1 != path1).ShouldBeFalse();
#pragma warning restore CS1718 // Comparison made to same variable
        }

        [Fact]
        public void AbsolutePath_GetHashCode_ShouldBeConsistentWithEquals()
        {
            string testPath = Path.GetTempPath();
            var path1 = new AbsolutePath(testPath);
            var path2 = new AbsolutePath(testPath);

            // Equal objects must have equal hash codes
            path1.Equals(path2).ShouldBeTrue();
            path1.GetHashCode().ShouldBe(path2.GetHashCode());
        }

        [Fact]
        public void AbsolutePath_Equals_WithObject_ShouldWorkCorrectly()
        {
            string testPath = Path.GetTempPath();
            var path1 = new AbsolutePath(testPath);
            object path2 = new AbsolutePath(testPath);
            object notAPath = "not a path";

            path1.Equals(path2).ShouldBeTrue();
            path1.Equals(notAPath).ShouldBeFalse();
            path1.Equals(null).ShouldBeFalse();
        }

        [WindowsOnlyFact]
        public void AbsolutePath_CaseInsensitive_OnWindows()
        {
            // On Windows, paths are case-insensitive
            var lowerPath = new AbsolutePath("C:\\foo\\bar", ignoreRootedCheck: true);
            var upperPath = new AbsolutePath("C:\\FOO\\BAR", ignoreRootedCheck: true);

            lowerPath.Equals(upperPath).ShouldBeTrue();
            (lowerPath == upperPath).ShouldBeTrue();
            lowerPath.GetHashCode().ShouldBe(upperPath.GetHashCode());
        }

        [LinuxOnlyFact]
        public void AbsolutePath_CaseSensitive_OnLinux()
        {
            // On Linux, paths are case-sensitive
            var lowerPath = new AbsolutePath("/foo/bar");
            var upperPath = new AbsolutePath("/FOO/BAR");

            lowerPath.Equals(upperPath).ShouldBeFalse();
            (lowerPath == upperPath).ShouldBeFalse();
        }

        [Theory]
        [InlineData("not/rooted/path", false, true)]
        [InlineData("not/rooted/path", true, false)]
        public void AbsolutePath_RootedValidation_ShouldBehaveProperly(string path, bool ignoreRootedCheck, bool shouldThrow)
        {
            if (shouldThrow)
            {
                Should.Throw<System.ArgumentException>(() => new AbsolutePath(path, ignoreRootedCheck: ignoreRootedCheck));
            }
            else
            {
                var absolutePath = new AbsolutePath(path, ignoreRootedCheck: ignoreRootedCheck);
                absolutePath.Value.ShouldBe(path);
            }
        }

        [WindowsOnlyTheory]
        // True Windows absolute paths - should be accepted
        [InlineData("C:\\foo", true)]                    // Standard Windows absolute path
        [InlineData("C:\\foo\\bar", true)]                // Another Windows absolute path
        [InlineData("D:\\foo\\bar", true)]                // Different drive Windows path
        [InlineData("C:\\foo\\bar\\.", true)]              // Windows absolute path with current directory
        [InlineData("C:\\foo\\bar\\..", true)]             // Windows absolute path with parent directory
        // Windows rooted but NOT absolute paths - should be rejected
        [InlineData("\\foo", false)]                     // Root-relative (missing drive)
        [InlineData("\\foo\\bar", false)]                 // Root-relative (missing drive)
        [InlineData("C:foo", false)]                    // Drive-relative (no backslash after colon)
        [InlineData("C:1\\foo", false)]                  // Drive-relative with unexpected character
        // Relative paths - should be rejected
        [InlineData("foo", false)]                       // Simple relative path
        [InlineData("foo/bar", false)]                   // Forward slash relative path
        [InlineData("foo\\bar", false)]                  // Backslash relative path
        [InlineData(".", false)]                         // Current directory
        [InlineData("..", false)]                        // Parent directory
        [InlineData("../parent", false)]                 // Parent relative path
        [InlineData("subfolder/file.txt", false)]        // Nested relative path
        public void AbsolutePath_WindowsPathValidation_ShouldAcceptOnlyTrueAbsolutePaths(string path, bool shouldBeAccepted)
        {
            ValidatePathAcceptance(path, shouldBeAccepted);
        }

        [UnixOnlyTheory]
        // True Unix absolute paths - should be accepted
        [InlineData("/foo", true)]                       // Standard Unix absolute path
        [InlineData("/foo/bar", true)]                   // Nested Unix absolute path
        [InlineData("/", true)]                          // Root directory
        [InlineData("/foo/bar/.", true)]                 // Unix absolute path with current directory
        [InlineData("/foo/bar/..", true)]                // Unix absolute path with parent directory
        // Relative paths - should be rejected (same on all platforms)
        [InlineData("foo", false)]                       // Simple relative path
        [InlineData("foo/bar", false)]                   // Forward slash relative path
        [InlineData("foo\\bar", false)]                  // Backslash relative path (unusual on Unix but still relative)
        [InlineData(".", false)]                         // Current directory
        [InlineData("..", false)]                        // Parent directory
        [InlineData("../parent", false)]                 // Parent relative path
        [InlineData("subfolder/file.txt", false)]        // Nested relative path
        public void AbsolutePath_UnixPathValidation_ShouldAcceptOnlyTrueAbsolutePaths(string path, bool shouldBeAccepted)
        {
            ValidatePathAcceptance(path, shouldBeAccepted);
        }

        #region GetCanonicalForm Tests

        [Fact]
        public void GetCanonicalForm_DefaultInstance_ShouldReturnSameInstance()
        {
            var absolutePath = default(AbsolutePath);
            var result = absolutePath.GetCanonicalForm();

            result.ShouldBe(absolutePath);
        }

        [Fact]
        public void GetCanonicalForm_EmptyPath_ShouldReturnSameInstance()
        {
            var absolutePath = new AbsolutePath(string.Empty, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();

            result.ShouldBe(absolutePath);
        }

        [WindowsOnlyTheory]
        // Current directory segments with pure separators
        [InlineData("C:\\foo\\.\\bar")]                    // Backslash: current directory
        [InlineData("C:/foo/./bar")]                       // Forward slash: current directory
        // Parent directory segments with pure separators
        [InlineData("C:\\foo\\..\\bar")]                   // Backslash: parent directory
        [InlineData("C:/foo/../bar")]                      // Forward slash: parent directory
        // Mixed separators with relative segments
        [InlineData("C:\\foo\\./bar")]                     // Backslash then forward: current
        [InlineData("C:/foo/.\\bar")]                      // Forward then backslash: current
        [InlineData("C:\\foo\\../bar")]                    // Backslash then forward: parent
        [InlineData("C:/foo/..\\bar")]                     // Forward then backslash: parent
        // Trailing relative segments
        [InlineData("C:\\foo\\bar\\.")]                    // Trailing current directory
        [InlineData("C:\\foo\\bar\\..")]                   // Trailing parent directory
        [InlineData("C:/foo/bar/.")]                       // Trailing current (forward slash)
        [InlineData("C:/foo/bar/..")]                      // Trailing parent (forward slash)
        // Root-level relative segments
        [InlineData("C:\\.")]                              // Current dir at root
        [InlineData("C:\\..")]                             // Parent dir at root
        [InlineData("C:/.")]                               // Current dir at root (forward slash)
        [InlineData("C:/..")]                              // Parent dir at root (forward slash)
        // Separator normalization only (no relative segments)
        [InlineData("C:/foo/bar")]                         // Forward slashes need normalization
        public void GetCanonicalForm_WindowsPathNormalization_ShouldMatchPathGetFullPath(string inputPath)
        {
            ValidateGetCanonicalFormMatchesSystem(inputPath);
        }

        [WindowsOnlyTheory]
        // Hidden files/folders - should NOT trigger normalization (false positive prevention)
        [InlineData("C:\\.hidden")]                        // Hidden file at root
        [InlineData("C:\\foo\\.hidden")]                   // Hidden file in folder
        [InlineData("C:\\foo\\.hidden\\bar")]              // Hidden folder
        [InlineData("C:\\.nuget\\packages")]               // .nuget folder
        [InlineData("C:\\.config\\settings")]              // .config folder
        [InlineData("C:\\foo\\.git\\config")]              // .git folder
        [InlineData("C:\\foo\\.vs\\settings")]             // .vs folder
        // Files starting with dots but not relative segments
        [InlineData("C:\\foo\\.gitignore")]                // .gitignore file
        [InlineData("C:\\foo\\.editorconfig")]             // .editorconfig file
        [InlineData("C:\\foo\\...")]                       // Triple dot (not relative)
        [InlineData("C:\\foo\\....")]                      // Quad dot (not relative)
        [InlineData("C:\\foo\\.hidden.txt")]               // Hidden file with extension
        public void GetCanonicalForm_WindowsHiddenFiles_ShouldReturnSameInstance(string inputPath)
        {
            var absolutePath = new AbsolutePath(inputPath, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();

            // Should return the exact same instance (no normalization needed)
            ReferenceEquals(result.Value, absolutePath.Value).ShouldBeTrue(
                $"Path '{inputPath}' should not trigger normalization but GetCanonicalForm returned a different instance");
            result.Value.ShouldBe(inputPath);
        }

        [WindowsOnlyTheory]
        // Simple paths already in canonical form
        [InlineData("C:\\foo\\bar")]                       // Standard Windows path
        [InlineData("C:\\")]                               // Root only
        [InlineData("D:\\folder\\subfolder\\file.txt")]    // Deep path
        public void GetCanonicalForm_WindowsAlreadyCanonical_ShouldReturnSameInstance(string inputPath)
        {
            var absolutePath = new AbsolutePath(inputPath, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();

            ReferenceEquals(result.Value, absolutePath.Value).ShouldBeTrue(
                $"Path '{inputPath}' is already canonical but GetCanonicalForm returned a different instance");
        }

        [UnixOnlyTheory]
        // Current directory segments
        [InlineData("/foo/./bar")]                         // Current directory reference
        // Parent directory segments
        [InlineData("/foo/../bar")]                        // Parent directory reference
        // Trailing relative segments
        [InlineData("/foo/bar/.")]                         // Trailing current directory
        [InlineData("/foo/bar/..")]                        // Trailing parent directory
        // Root-level relative segments
        [InlineData("/.")]                                 // Current dir at root
        [InlineData("/..")]                                // Parent dir at root
        // Multiple relative segments
        [InlineData("/foo/./bar/../baz")]                  // Mixed current and parent
        public void GetCanonicalForm_UnixPathNormalization_ShouldMatchPathGetFullPath(string inputPath)
        {
            ValidateGetCanonicalFormMatchesSystem(inputPath);
        }

        [UnixOnlyTheory]
        // Hidden files/folders - should NOT trigger normalization
        [InlineData("/.hidden")]                           // Hidden file at root
        [InlineData("/foo/.hidden")]                       // Hidden file in folder
        [InlineData("/foo/.hidden/bar")]                   // Hidden folder
        [InlineData("/.nuget/packages")]                   // .nuget folder
        [InlineData("/.config/settings")]                  // .config folder
        [InlineData("/foo/.git/config")]                   // .git folder
        [InlineData("/foo/.local/share")]                  // .local folder
        // Files starting with dots but not relative segments
        [InlineData("/foo/.gitignore")]                    // .gitignore file
        [InlineData("/foo/.bashrc")]                       // .bashrc file
        [InlineData("/foo/...")]                           // Triple dot (not relative)
        [InlineData("/foo/....")]                          // Quad dot (not relative)
        [InlineData("/foo/.hidden.txt")]                   // Hidden file with extension
        // Backslash in Unix paths (part of filename, not separator)
        [InlineData("/foo/bar\\baz")]                      // Backslash is part of name
        [InlineData("/foo/.\\hidden")]                     // Backslash after dot (not a separator on Unix)
        public void GetCanonicalForm_UnixHiddenFiles_ShouldReturnSameInstance(string inputPath)
        {
            var absolutePath = new AbsolutePath(inputPath, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();

            ReferenceEquals(result.Value, absolutePath.Value).ShouldBeTrue(
                $"Path '{inputPath}' should not trigger normalization but GetCanonicalForm returned a different instance");
            result.Value.ShouldBe(inputPath);
        }

        [UnixOnlyTheory]
        // Simple paths already in canonical form
        [InlineData("/foo/bar")]                           // Standard Unix path
        [InlineData("/")]                                  // Root only
        [InlineData("/home/user/documents/file.txt")]      // Deep path
        public void GetCanonicalForm_UnixAlreadyCanonical_ShouldReturnSameInstance(string inputPath)
        {
            var absolutePath = new AbsolutePath(inputPath, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();

            ReferenceEquals(result.Value, absolutePath.Value).ShouldBeTrue(
                $"Path '{inputPath}' is already canonical but GetCanonicalForm returned a different instance");
        }

        [Fact]
        public void GetCanonicalForm_ShouldPreserveOriginalValue()
        {
            string originalValue = "original/relative/path";
            string absoluteValue = NativeMethods.IsWindows ? "C:\\foo\\.\\bar" : "/foo/./bar";

            var absolutePath = new AbsolutePath(absoluteValue, originalValue, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();

            // Original value should be preserved even after canonicalization
            result.OriginalValue.ShouldBe(originalValue);
        }

        [WindowsOnlyTheory]
        // UNC paths
        [InlineData("\\\\server\\share\\path")]            // Basic UNC path
        [InlineData("\\\\server\\share\\.\\path")]         // UNC with current dir segment
        [InlineData("\\\\server\\share\\..\\path")]        // UNC with parent dir segment
        public void GetCanonicalForm_WindowsUNCPaths_ShouldMatchPathGetFullPath(string inputPath)
        {
            ValidateGetCanonicalFormMatchesSystem(inputPath);
        }

        [WindowsOnlyTheory]
        // Multiple consecutive separators
        [InlineData("C:\\foo\\\\bar")]                     // Double backslash
        [InlineData("C://foo//bar")]                       // Double forward slash
        public void GetCanonicalForm_MultipleConsecutiveSeparators_ShouldMatchPathGetFullPath(string inputPath)
        {
            ValidateGetCanonicalFormMatchesSystem(inputPath);
        }

        private static void ValidateGetCanonicalFormMatchesSystem(string inputPath)
        {
            var absolutePath = new AbsolutePath(inputPath, ignoreRootedCheck: true);
            var result = absolutePath.GetCanonicalForm();
            var systemResult = Path.GetFullPath(inputPath);

            // Should match Path.GetFullPath behavior exactly
            result.Value.ShouldBe(systemResult);

            // Should preserve original value
            result.OriginalValue.ShouldBe(absolutePath.OriginalValue);
        }

        #endregion
    }
}
