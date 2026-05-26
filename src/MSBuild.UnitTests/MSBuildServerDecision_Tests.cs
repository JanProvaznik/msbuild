// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.CommandLine.Experimental;
using Microsoft.Build.Framework;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

namespace Microsoft.Build.CommandLine.UnitTests;

public class MSBuildServerDecision_Tests
{
    // GatherAllSwitches discards the first element as the executable path, so every
    // test command line begins with a placeholder. See CommandLineParser.cs:105-107.
    private const string Exe = "msbuild.exe";

    private static CommandLineParser NewParser() => new CommandLineParser();

    // Constructing a fresh Traits per test snapshots the env vars at that moment.
    // See src/Framework/Traits.cs: EscapeHatches fields use `readonly` initializers.
    private static Traits NewTraits() => new Traits();

    private const string MinimalProject = "<Project><Target Name='Empty'/></Project>";

    /// <summary>
    /// Sets MSBUILDUSESERVER=1, clears MSBUILDENSURESTDOUTFORTASKPROCESSES, and creates
    /// a real, transient project file so ProcessProjectSwitch does not throw
    /// ProjectNotFoundError. Returns the project file path.
    /// </summary>
    private static string SetUpServerEnabledEnvironment(TestEnvironment env)
    {
        env.SetEnvironmentVariable("MSBUILDUSESERVER", "1");
        env.SetEnvironmentVariable("MSBUILDENSURESTDOUTFORTASKPROCESSES", null);
        return env.CreateFile("test.proj", MinimalProject).Path;
    }

    // --- env var unset / "0" / anything other than "1" => no server, no telemetry ---

    [Theory]
    [InlineData(null, MSBuildServerDecision.EnvVarValueUnset, MSBuildServerDecision.DecisionReasonEnvVarUnset)]
    [InlineData("0", MSBuildServerDecision.EnvVarValueZero, MSBuildServerDecision.DecisionReasonEnvVarZero)]
    [InlineData("2", MSBuildServerDecision.EnvVarValueOther, MSBuildServerDecision.DecisionReasonEnvVarOther)]
    [InlineData("true", MSBuildServerDecision.EnvVarValueOther, MSBuildServerDecision.DecisionReasonEnvVarOther)]
    [InlineData("yes", MSBuildServerDecision.EnvVarValueOther, MSBuildServerDecision.DecisionReasonEnvVarOther)]
    public void EnvVarNotOne_ReturnsNoServer_NoTelemetryReason(string? envValue, string expectedEnvVarValue, string expectedReason)
    {
        using TestEnvironment env = TestEnvironment.Create();
        env.SetEnvironmentVariable("MSBUILDUSESERVER", envValue);
        string projectPath = env.CreateFile("test.proj", MinimalProject).Path;

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:4"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.None);
        result.ToTelemetryReason().ShouldBeNull();
        result.RequestState.ShouldBe(MSBuildServerDecision.RequestStateNotRequested);
        result.EnvVarValue.ShouldBe(expectedEnvVarValue);
        result.Decision.ShouldBe(MSBuildServerDecision.DecisionNotRequested);
        result.DecisionReason.ShouldBe(expectedReason);
        result.FinalOutcome.ShouldBe(MSBuildServerDecision.FinalOutcomeNotRequested);
    }

    // --- EscapeHatch wins over everything else ---

    [Fact]
    public void EscapeHatchOn_ReturnsNoServer_EscapeHatchReason()
    {
        using TestEnvironment env = TestEnvironment.Create();
        env.SetEnvironmentVariable("MSBUILDUSESERVER", "1");
        env.SetEnvironmentVariable("MSBUILDENSURESTDOUTFORTASKPROCESSES", "1");
        string projectPath = env.CreateFile("test.proj", MinimalProject).Path;

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:4"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.EscapeHatch);
        result.ToTelemetryReason().ShouldBe("EscapeHatch");
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonEscapeHatch);
        result.FallbackStage.ShouldBe(MSBuildServerDecision.FallbackStagePreLaunch);
        result.FallbackDetailedReason.ShouldBe(MSBuildServerDecision.DecisionReasonEscapeHatch);
        result.StdOutEscapeHatchEnabled.ShouldBeTrue();
    }

    // --- Exclusionary switches => Arguments ---

    [Theory]
    [InlineData("-help", MSBuildServerDecision.DecisionReasonHelp)]
    [InlineData("-h", MSBuildServerDecision.DecisionReasonHelp)]
    [InlineData("-?", MSBuildServerDecision.DecisionReasonHelp)]
    [InlineData("-version", MSBuildServerDecision.DecisionReasonVersion)]
    [InlineData("-nodemode:1", MSBuildServerDecision.DecisionReasonNodeMode)]
    public void ExclusionarySwitch_ReturnsNoServer_ArgumentsReason(string excludingSwitch, string expectedDecisionReason)
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, excludingSwitch, projectPath, "-m:4"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.Arguments);
        result.ToTelemetryReason().ShouldBe("Arguments");
        result.DecisionReason.ShouldBe(expectedDecisionReason);
        result.FallbackDetailedReason.ShouldBe(expectedDecisionReason);
        result.EffectiveMaxNodeCount.ShouldBe(4);
        result.NodeReuseEnabled.ShouldBe(true);
        result.ProjectKind.ShouldBe(MSBuildServerDecision.ProjectKindProject);
    }

    [Fact]
    public void NodeReuseFalse_ReturnsNoServer_ArgumentsReason()
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:4", "-nodereuse:false"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.Arguments);
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonNodeReuseDisabled);
        result.NodeReuseEnabled.ShouldBe(false);
        result.EffectiveMaxNodeCount.ShouldBe(4);
    }

    [Fact]
    public void BinlogProject_ReturnsNoServer_ArgumentsReason()
    {
        using TestEnvironment env = TestEnvironment.Create();
        env.SetEnvironmentVariable("MSBUILDUSESERVER", "1");
        env.SetEnvironmentVariable("MSBUILDENSURESTDOUTFORTASKPROCESSES", null);

        // Binlog "project" must exist on disk so ProcessProjectSwitch can read it.
        string binlogPath = env.CreateFile("build.binlog", string.Empty).Path;

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, binlogPath, "-m:4"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.Arguments);
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonBinaryLogReplay);
        result.ProjectKind.ShouldBe(MSBuildServerDecision.ProjectKindBinaryLog);
    }

    [Fact]
    public void ParseFailure_ReturnsNoServer_ErrorReason()
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        // Invalid value for -m triggers CommandLineSwitchException (InvalidMaxCPUCountValue)
        // inside ProcessMaxCPUCountSwitch, which is called from Decide.
        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:notANumber"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.ErrorParsingCommandLine);
        result.ToTelemetryReason().ShouldBe("ErrorParsingCommandLine");
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonErrorParsingCommandLine);
        result.FallbackStage.ShouldBe(MSBuildServerDecision.FallbackStagePreLaunch);
    }

    // --- Effective -m:1 (no -m or explicit -m:1) => SingleNode ---

    [Fact]
    public void NoMaxCpuSwitch_ReturnsNoServer_SingleNodeReason()
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.SingleNode);
        result.ToTelemetryReason().ShouldBe("SingleNode");
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonSingleNode);
        result.FallbackDetailedReason.ShouldBe(MSBuildServerDecision.DecisionReasonSingleNode);
        result.EffectiveMaxNodeCount.ShouldBe(1);
        result.NodeReuseEnabled.ShouldBe(true);
        result.ProjectKind.ShouldBe(MSBuildServerDecision.ProjectKindProject);
    }

    [Fact]
    public void ExplicitMOne_ReturnsNoServer_SingleNodeReason()
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:1"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.SingleNode);
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonSingleNode);
        result.EffectiveMaxNodeCount.ShouldBe(1);
    }

    // --- Multi-node => server is used ---

    [Fact]
    public void MaxCpuTwo_UsesServer()
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:2"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeTrue();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.None);
        result.ToTelemetryReason().ShouldBeNull();
        result.RequestState.ShouldBe(MSBuildServerDecision.RequestStateRequested);
        result.EnvVarValue.ShouldBe(MSBuildServerDecision.EnvVarValueOne);
        result.Decision.ShouldBe(MSBuildServerDecision.DecisionAttemptServer);
        result.DecisionReason.ShouldBe(MSBuildServerDecision.DecisionReasonEligible);
        result.FinalOutcome.ShouldBe(MSBuildServerDecision.FinalOutcomeAttemptServer);
        result.EffectiveMaxNodeCount.ShouldBe(2);
        result.NodeReuseEnabled.ShouldBe(true);
        result.ProjectKind.ShouldBe(MSBuildServerDecision.ProjectKindProject);
    }

    [Fact]
    public void BareMaxCpu_ExpandsToCoresAndUsesServer()
    {
        // The parser rewrites bare -m to -m:<NumberOfCores>. On multi-core hosts the
        // server should be used. Single-core hosts would correctly fall back to SingleNode.
        if (Environment.ProcessorCount <= 1)
        {
            return;
        }

        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeTrue();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.None);
    }

    // --- Ordering tests ---

    [Fact]
    public void EscapeHatchBeatsEverything()
    {
        // EscapeHatch short-circuits before the parse, so it wins over Arguments and
        // SingleNode. Matches today's inline ordering in Main.
        using TestEnvironment env = TestEnvironment.Create();
        env.SetEnvironmentVariable("MSBUILDUSESERVER", "1");
        env.SetEnvironmentVariable("MSBUILDENSURESTDOUTFORTASKPROCESSES", "1");
        string projectPath = env.CreateFile("test.proj", MinimalProject).Path;

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-m:1", "-help"],
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.EscapeHatch);
    }

    [Fact]
    public void ArgumentsBeatSingleNode()
    {
        using TestEnvironment env = TestEnvironment.Create();
        string projectPath = SetUpServerEnabledEnvironment(env);

        MSBuildServerDecisionResult result = MSBuildServerDecision.Decide(
            [Exe, projectPath, "-help"], // no -m, would also be SingleNode
            NewParser(),
            NewTraits());

        result.UseServer.ShouldBeFalse();
        result.FallbackReason.ShouldBe(MSBuildServerFallbackReason.Arguments);
    }
}
