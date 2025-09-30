// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Diagnostics;

namespace Microsoft.Build.Tasks.Demo
{
    // Mock interfaces for demo purposes (would normally come from Framework assembly)
    public interface ITask { bool Execute(); }
    
    public interface IMultiThreadableTask : ITask 
    { 
        TaskEnvironment TaskEnvironment { get; set; } 
    }

    public class TaskEnvironment
    {
        public string GetAbsolutePath(string path) => Path.GetFullPath(path);
        public string GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
        public ProcessStartInfo GetProcessStartInfo() => new ProcessStartInfo();
    }

    /// <summary>
    /// Demo task showing problematic API usage that should trigger analyzer warnings.
    /// This demonstrates common threading issues in MSBuild tasks.
    /// Expected: Multiple MSB4260 warnings from the ThreadSafeTaskAnalyzer
    /// </summary>
    public class ProblematicTask : IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new TaskEnvironment();

        public string InputFile { get; set; } = "input.txt";
        public string OutputFile { get; set; } = "output.txt";
        public string? WorkingDirectory { get; set; }

        public bool Execute()
        {
            try
            {
                // PROBLEMATIC: Uses current working directory - should trigger MSB4260 warning
                if (!File.Exists(InputFile))
                {
                    Console.WriteLine($"Input file not found: {InputFile}");
                    return false;
                }

                // PROBLEMATIC: Modifies process-level state - should trigger MSB4260 warning
                if (!string.IsNullOrEmpty(WorkingDirectory))
                {
                    Environment.CurrentDirectory = WorkingDirectory;
                }

                // PROBLEMATIC: Uses relative path resolution - should trigger MSB4260 warning
                string absoluteInput = Path.GetFullPath(InputFile);
                
                // PROBLEMATIC: More file operations with potential relative paths - should trigger MSB4260 warning
                string content = File.ReadAllText(InputFile);
                content = content.ToUpper();
                
                // PROBLEMATIC: Directory operations - should trigger MSB4260 warning
                string outputDir = Path.GetDirectoryName(OutputFile);
                if (!Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                File.WriteAllText(OutputFile, content);

                // PROBLEMATIC: Process launching without proper configuration - should trigger MSB4260 warning
                var process = Process.Start("notepad.exe", OutputFile);
                process?.WaitForExit();

                // PROBLEMATIC: Console output - should trigger MSB4260 warning
                Console.WriteLine($"Processed {InputFile} -> {OutputFile}");

                // PROBLEMATIC: Environment variable modification - should trigger MSB4260 warning
                Environment.SetEnvironmentVariable("LAST_PROCESSED", OutputFile);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Demo task showing correct thread-safe implementation using TaskEnvironment.
    /// This demonstrates the proper way to write multithreadable tasks.
    /// Expected: NO analyzer warnings
    /// </summary>
    public class ThreadSafeTask : IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new TaskEnvironment();

        public string InputFile { get; set; } = "input.txt";
        public string OutputFile { get; set; } = "output.txt";

        public bool Execute()
        {
            try
            {
                // CORRECT: Use TaskEnvironment for path resolution - no analyzer warning
                var inputPath = TaskEnvironment.GetAbsolutePath(InputFile);
                
                // CORRECT: Check file existence using absolute path - no analyzer warning
                if (!File.Exists(inputPath))
                {
                    // Note: In real implementation, would use Log.LogError instead
                    return false;
                }

                // CORRECT: Use TaskEnvironment for environment variables - no analyzer warning
                string? tempDir = TaskEnvironment.GetEnvironmentVariable("TEMP");

                // CORRECT: Resolve output path properly - no analyzer warning
                var outputPath = TaskEnvironment.GetAbsolutePath(OutputFile);
                
                // CORRECT: Use absolute paths for all file operations - no analyzer warning
                string content = File.ReadAllText(inputPath);
                content = content.ToUpper();
                
                // CORRECT: Create directories using absolute paths - no analyzer warning
                string? outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                File.WriteAllText(outputPath, content);

                // CORRECT: Use TaskEnvironment for process configuration - no analyzer warning
                var startInfo = TaskEnvironment.GetProcessStartInfo();
                startInfo.FileName = "notepad.exe";
                startInfo.Arguments = $"\"{outputPath}\"";
                
                using var process = Process.Start(startInfo);
                process?.WaitForExit(5000);

                // Note: In real implementation, would use Log.LogMessage instead of Console

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Task that shows assembly loading warnings.
    /// Expected: MSB4260 warnings for assembly operations
    /// </summary>
    public class AssemblyTask : IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new TaskEnvironment();

        public string AssemblyPath { get; set; } = "MyAssembly.dll";

        public bool Execute()
        {
            // PROBLEMATIC: Assembly loading - should trigger MSB4260 warning
            var assembly = System.Reflection.Assembly.LoadFrom(AssemblyPath);

            // PROBLEMATIC: More assembly operations - should trigger MSB4260 warning  
            var type = assembly.GetType("SomeType");
            if (type != null)
            {
                var instance = Activator.CreateInstance(type);
            }

            return true;
        }
    }

    /// <summary>
    /// Regular task (not implementing IMultiThreadableTask).
    /// Expected: NO analyzer warnings even with problematic code
    /// </summary>
    public class RegularTask : ITask
    {
        public string InputFile { get; set; } = "input.txt";

        public bool Execute()
        {
            // This code is problematic but should NOT trigger warnings 
            // because this task doesn't implement IMultiThreadableTask
            if (!File.Exists(InputFile))
            {
                Console.WriteLine("File not found");
                return false;
            }

            Environment.CurrentDirectory = Path.GetDirectoryName(InputFile) ?? "";
            string content = File.ReadAllText(Path.GetFileName(InputFile));
            
            return true;
        }
    }
}