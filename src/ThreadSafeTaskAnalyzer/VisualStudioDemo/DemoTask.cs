// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.IO;
using Microsoft.Build.Framework;

namespace VisualStudioDemo
{
    /// <summary>
    /// Demo task showing PROBLEMATIC code that triggers analyzer warnings.
    /// This class demonstrates unwrapped File/Directory API usage.
    /// 
    /// Try the code fixer:
    /// 1. Place cursor on any MSB9999 warning (green squiggle)
    /// 2. Press Ctrl+. to open Quick Actions
    /// 3. Select "Wrap with TaskEnvironment.GetAbsolutePath()"
    /// 4. Watch the warning disappear!
    /// </summary>
    public class ProblematicTask : IMultiThreadableTask
    {
        public IBuildEngine BuildEngine { get; set; }
        public ITaskHost HostObject { get; set; }
        public TaskEnvironment TaskEnvironment { get; set; }

        public string InputFile { get; set; }
        public string OutputDirectory { get; set; }

        public bool Execute()
        {
            // ❌ MSB9999: File.Exists with unwrapped path - Code fixer available!
            if (File.Exists(InputFile))
            {
                // ❌ MSB9999: Directory.Exists with unwrapped path
                if (!Directory.Exists(OutputDirectory))
                {
                    // ❌ MSB9999: Directory.CreateDirectory with unwrapped path
                    Directory.CreateDirectory(OutputDirectory);
                }

                // ❌ MSB9999: File.ReadAllText with unwrapped path
                string content = File.ReadAllText(InputFile);

                // ❌ MSB9999: Path.GetFullPath - always banned (use TaskEnvironment.GetAbsolutePath instead)
                string fullPath = Path.GetFullPath(InputFile);

                // ❌ MSB9999: new FileInfo with unwrapped path
                var fileInfo = new FileInfo(InputFile);

                // ❌ MSB9999: new DirectoryInfo with unwrapped path
                var dirInfo = new DirectoryInfo(OutputDirectory);

                // ❌ MSB9999: new StreamReader with unwrapped path
                using (var reader = new StreamReader(InputFile))
                {
                    string line = reader.ReadLine();
                }

                // ❌ MSB9999: File.WriteAllText with unwrapped path
                File.WriteAllText(Path.Combine(OutputDirectory, "output.txt"), content);

                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Example of CORRECT code - no MSB9999 warnings!
    /// All File/Directory operations use absolute paths via TaskEnvironment.GetAbsolutePath().
    /// This is what your code should look like after applying the code fixer.
    /// </summary>
    public class CorrectTask : IMultiThreadableTask
    {
        public IBuildEngine BuildEngine { get; set; }
        public ITaskHost HostObject { get; set; }
        public TaskEnvironment TaskEnvironment { get; set; }

        public string InputFile { get; set; }
        public string OutputDirectory { get; set; }

        public bool Execute()
        {
            // ✅ Wrapped with TaskEnvironment.GetAbsolutePath() - NO MSB9999 warning
            if (File.Exists(TaskEnvironment.GetAbsolutePath(InputFile)))
            {
                // ✅ Wrapped path - NO MSB9999 warning
                if (!Directory.Exists(TaskEnvironment.GetAbsolutePath(OutputDirectory)))
                {
                    // ✅ Wrapped path - NO MSB9999 warning
                    Directory.CreateDirectory(TaskEnvironment.GetAbsolutePath(OutputDirectory));
                }

                // ✅ Wrapped path - NO MSB9999 warning
                string content = File.ReadAllText(TaskEnvironment.GetAbsolutePath(InputFile));
                
                // ✅ Path.Combine result wrapped - NO MSB9999 warning
                var outputFile = TaskEnvironment.GetAbsolutePath(
                    Path.Combine(TaskEnvironment.GetAbsolutePath(OutputDirectory), "output.txt"));
                File.WriteAllText(outputFile, content);

                return true;
            }

            return false;
        }
    }
}
