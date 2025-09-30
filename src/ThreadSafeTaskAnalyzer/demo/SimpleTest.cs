// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Simple test to trigger analyzer warnings
using System;
using System.IO;
using Microsoft.Build.Framework;

namespace Test
{
    public class TestTask : IMultiThreadableTask
    {
        public IBuildEngine? BuildEngine { get; set; }
        public ITaskHost? HostObject { get; set; }
        public TaskEnvironment? TaskEnvironment { get; set; }
        
        public bool Execute()
        {
            // This should definitely trigger MSB4260 warnings:
            File.Exists("test.txt");
            Console.WriteLine("test");
            Environment.SetEnvironmentVariable("TEST", "value");
            return true;
        }
    }
    
    public class GoodTask : IMultiThreadableTask
    {
        public IBuildEngine? BuildEngine { get; set; }
        public ITaskHost? HostObject { get; set; }
        public TaskEnvironment? TaskEnvironment { get; set; }
        
        public bool Execute()
        {
            // This should NOT trigger warnings:
            var path = TaskEnvironment?.GetAbsolutePath("test.txt");
            if (path != null)
            {
                File.Exists(path);
            }
            return true;
        }
    }
}