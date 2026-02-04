// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

#nullable disable

namespace Microsoft.Build.UnitTests.BackEnd
{
    /// <summary>
    /// Regression tests for the race condition in MT mode results transfer.
    /// Issue: https://github.com/dotnet/msbuild/issues/13188
    /// 
    /// The race condition occurred when:
    /// 1. A configuration is built on Node N, ResultsNodeId = N
    /// 2. Multiple other nodes need results from that configuration concurrently
    /// 3. Each node modifies ResultsNodeId (shared state in MT mode)
    /// 4. The assertion checking ResultsNodeId == currentNodeId fails
    ///    
    /// The fix: Skip the ResultsNodeId assertion in MT mode since the results
    /// cache is shared and results are available once unblocked.
    /// </summary>
    public class ResultsTransferRace_Tests : IDisposable
    {
        private readonly MockLogger _logger;
        private readonly BuildManager _buildManager;
        private readonly BuildParameters _parameters;
        private readonly TestEnvironment _env;
        private readonly ITestOutputHelper _output;

        public ResultsTransferRace_Tests(ITestOutputHelper output)
        {
            _output = output;
            BuildManager.DefaultBuildManager.Dispose();

            _logger = new MockLogger(output)
            {
                AllowEngineCrashes = true,
            };
            _parameters = new BuildParameters
            {
                ShutdownInProcNodeOnBuildFinish = true,
                Loggers = new ILogger[] { _logger },
                EnableNodeReuse = false,
            };
            _buildManager = new BuildManager();
            _env = TestEnvironment.Create(output);
        }

        public void Dispose()
        {
            try
            {
                _buildManager.Dispose();
            }
            finally
            {
                _env.Dispose();
            }
        }

        /// <summary>
        /// Regression test for issue #13188.
        /// Runs many iterations of a parallel build that would trigger the race condition.
        /// The test passes if no race failures occur.
        /// </summary>
        [Theory]
        [InlineData(1000)]
        public void FSharpStyleBuild_DuplicateProjectReferences(int iterations)
        {
            // Create a shared project that will be referenced multiple times
            string sharedProj = _env.CreateFile("Shared.proj").Path;
            File.WriteAllText(sharedProj, @"
<Project>
    <PropertyGroup>
        <TargetFramework Condition=""'$(TargetFramework)' == ''"">net8.0</TargetFramework>
    </PropertyGroup>
    <Target Name='Build'>
        <Message Text='Shared building for $(TargetFramework)' Importance='High' />
    </Target>
    <Target Name='Pack' DependsOnTargets='Build'>
        <Message Text='Shared packing for $(TargetFramework)' Importance='High' />
    </Target>
</Project>");

            // Create multiple "orchestrator" projects that each reference the shared project MULTIPLE times
            // This mimics the F# pattern where DependentProjects contains duplicates
            var topProjects = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                string top = _env.CreateFile($"Top{i}.proj").Path;
                File.WriteAllText(top, $@"
<Project>
    <ItemGroup>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=netstandard2.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=netstandard2.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=netstandard2.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=netstandard2.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=net8.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=net8.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=net8.0</AdditionalProperties></DependentProjects>
        <DependentProjects Include='{sharedProj}'><AdditionalProperties>TargetFramework=net8.0</AdditionalProperties></DependentProjects>
    </ItemGroup>
    <Target Name='Build'>
        <Message Text='Top{i} building' Importance='High' />
        <MSBuild Projects='@(DependentProjects)' Targets='Pack' BuildInParallel='true' />
        <Message Text='Top{i} done' Importance='High' />
    </Target>
</Project>");

                topProjects.Add(top);
            }

            // Root project that builds all top projects in parallel
            string rootProj = _env.CreateFile("Root.proj").Path;
            string topRefs = string.Join(";", topProjects);
            File.WriteAllText(rootProj, $@"
<Project>
    <ItemGroup>
        <TopRefs Include='{topRefs}' />
    </ItemGroup>
    <Target Name='Build'>
        <Message Text='Root starting' Importance='High' />
        <MSBuild Projects='@(TopRefs)' Targets='Build' BuildInParallel='true' />
        <Message Text='Root complete' Importance='High' />
    </Target>
</Project>");

            var buildParams = _parameters.Clone();
            buildParams.MaxNodeCount = Math.Max(Environment.ProcessorCount, 8);
            buildParams.MultiThreaded = true;
            buildParams.DisableInProcNode = false;

            int raceFailures = 0;
            int otherFailures = 0;

            for (int i = 0; i < iterations; i++)
            {
                _logger.ClearLog();

                try
                {
                    var result = _buildManager.Build(
                        buildParams,
                        new BuildRequestData(rootProj, new Dictionary<string, string>(), null, ["Build"], null));

                    if (result.OverallResult != BuildResultCode.Success)
                    {
                        string log = _logger.FullLog;
                        if (log.Contains("Results for configuration") && log.Contains("were not retrieved from node"))
                        {
                            raceFailures++;
                            _output.WriteLine($"Iteration {i + 1}: RACE CONDITION DETECTED");
                        }
                        else
                        {
                            otherFailures++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    string exStr = ex.ToString();
                    if (exStr.Contains("Results for configuration") && exStr.Contains("were not retrieved from node"))
                    {
                        raceFailures++;
                        _output.WriteLine($"Iteration {i + 1}: RACE CONDITION DETECTED (exception)");
                    }
                    else if (exStr.Contains("InternalErrorException") || exStr.Contains("MSB0001"))
                    {
                        raceFailures++;
                        _output.WriteLine($"Iteration {i + 1}: RACE CONDITION DETECTED (InternalErrorException)");
                    }
                    else
                    {
                        otherFailures++;
                    }
                }
            }

            _output.WriteLine($"Completed {iterations} iterations:");
            _output.WriteLine($"        Race failures: {raceFailures}");
            _output.WriteLine($"        Other failures: {otherFailures}");

            raceFailures.ShouldBe(0, "Race condition should not occur with the fix");
            otherFailures.ShouldBe(0, "No other failures should occur");
        }
    }
}
