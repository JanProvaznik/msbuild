// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Tasks.Demo
{
    /// <summary>
    /// Demo task using the REAL IMultiThreadableTask interface - should trigger warnings!
    /// </summary>
    public class ProblematicDemoTask : IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        public string InputFile { get; set; } = "input.txt";
        public string OutputFile { get; set; } = "output.txt";

        public bool Execute()
        {
            // PROBLEMATIC: Uses current working directory - SHOULD trigger MSB4260 warning
            if (!File.Exists(InputFile))
            {
                return false;
            }

            // PROBLEMATIC: Modifies process-level state - SHOULD trigger MSB4260 warning
            Environment.CurrentDirectory = @"C:\temp";

            // PROBLEMATIC: Uses relative path resolution - SHOULD trigger MSB4260 warning
            string absoluteInput = Path.GetFullPath(InputFile);
            
            // PROBLEMATIC: Directory operations - SHOULD trigger MSB4260 warning
            if (!Directory.Exists("output"))
            {
                Directory.CreateDirectory("output");
            }

            // PROBLEMATIC: Console output - SHOULD trigger MSB4260 warning
            Console.WriteLine("Processing file...");

            // PROBLEMATIC: Environment variable modification - SHOULD trigger MSB4260 warning
            Environment.SetEnvironmentVariable("DEMO_VAR", "test");

            // PROBLEMATIC: Process launching - SHOULD trigger MSB4260 warning
            Process.Start("notepad.exe");

            return true;
        }
    }

    /// <summary>
    /// Demo task showing correct usage - should NOT trigger warnings
    /// </summary>
    public class GoodDemoTask : IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; }

        public string InputFile { get; set; } = "input.txt";
        public string OutputFile { get; set; } = "output.txt";

        public bool Execute()
        {
            // CORRECT: Use TaskEnvironment for path resolution - should NOT trigger warning
            var inputPath = TaskEnvironment.GetAbsolutePath(InputFile);
            
            // CORRECT: Check file existence using absolute path - should NOT trigger warning
            if (!File.Exists(inputPath))
            {
                return false;
            }

            // CORRECT: Use TaskEnvironment for environment variables - should NOT trigger warning
            string tempDir = TaskEnvironment.GetEnvironmentVariable("TEMP");

            // CORRECT: Use TaskEnvironment for process configuration - should NOT trigger warning
            var startInfo = TaskEnvironment.GetProcessStartInfo();
            startInfo.FileName = "notepad.exe";

            return true;
        }
    }

    /// <summary>
    /// Regular task not implementing IMultiThreadableTask - should NOT trigger warnings
    /// even with problematic code
    /// </summary>
    public class RegularDemoTask
    {
        public bool Execute()
        {
            // This should NOT trigger warnings because it doesn't implement IMultiThreadableTask
            if (!File.Exists("test.txt"))
            {
                Console.WriteLine("File not found");
                Environment.CurrentDirectory = @"C:\temp";
                Directory.CreateDirectory("test");
            }
            return true;
        }
    }
}