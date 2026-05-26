// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.CommandLine.Experimental;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using static Microsoft.Build.Execution.BuildManager;

#if NETFRAMEWORK
using Directory = Microsoft.IO.Directory;
#endif

namespace Microsoft.Build.CommandLine;

/// <summary>
/// Enumerates the reasons why MSBuild may decline to dispatch a build to MSBuild Server
/// even when the caller is otherwise eligible. The value is surfaced (as a string) on
/// build telemetry via <see cref="Framework.Telemetry.BuildTelemetry.ServerFallbackReason"/>.
/// </summary>
internal enum MSBuildServerFallbackReason
{
    /// <summary>
    /// No fallback reason should be reported. Either the server will be used, or
    /// the caller never opted in via <c>MSBUILDUSESERVER</c>.
    /// </summary>
    None = 0,

    /// <summary>
    /// The command-line switches passed to MSBuild are incompatible with running on
    /// the server (e.g., <c>-help</c>, <c>-version</c>, <c>-nodemode</c>, the project
    /// is a binary log replay, or <c>-nodereuse:false</c>).
    /// </summary>
    Arguments,

    /// <summary>
    /// An exception was thrown while parsing the command line. The decision falls back
    /// to in-proc execution because the parse error is reported there.
    /// </summary>
    ErrorParsingCommandLine,

    /// <summary>
    /// The build was requested with effective <c>-m:1</c> (either an explicit value of
    /// one or no <c>-m</c> switch at all). Server overhead is not justified for a
    /// single-node build, so the server is skipped even when the caller opted in via
    /// <c>MSBUILDUSESERVER=1</c>.
    /// </summary>
    SingleNode,

    /// <summary>
    /// The escape hatch <c>MSBUILDENSURESTDOUTFORTASKPROCESSES</c> is set, which is
    /// incompatible with the server's redirected output handling.
    /// </summary>
    EscapeHatch,
}

/// <summary>
/// Describes the outcome of <see cref="MSBuildServerDecision.Decide"/>.
/// </summary>
/// <param name="UseServer">
/// <see langword="true"/> when the build should be dispatched to MSBuild Server.
/// </param>
/// <param name="FallbackReason">
/// The reason the server is not being used, or <see cref="MSBuildServerFallbackReason.None"/>
    /// when either the server is being used or no coarse fallback reason should be written.
/// </param>
internal readonly record struct MSBuildServerDecisionResult(
    bool UseServer,
    MSBuildServerFallbackReason FallbackReason,
    string RequestState,
    string EnvVarValue,
    string Decision,
    string DecisionReason,
    string FinalOutcome,
    string? FallbackStage,
    string? FallbackDetailedReason,
    int? EffectiveMaxNodeCount,
    bool? NodeReuseEnabled,
    string? ProjectKind,
    bool StdOutEscapeHatchEnabled,
    string? ClientExitType = null)
{
    /// <summary>
    /// Converts the fallback reason to the string form expected by telemetry. Returns
    /// <see langword="null"/> when no fallback-reason telemetry should be written.
    /// </summary>
    public string? ToTelemetryReason() => FallbackReason switch
    {
        MSBuildServerFallbackReason.None => null,
        _ => FallbackReason.ToString(),
    };

    public void ApplyTo(BuildTelemetry telemetry)
    {
        if (ToTelemetryReason() is { } fallbackReason)
        {
            telemetry.ServerFallbackReason = fallbackReason;
        }

        telemetry.MSBuildServerRequestState = RequestState;
        telemetry.MSBuildServerEnvVarValue = EnvVarValue;
        telemetry.MSBuildServerDecision = Decision;
        telemetry.MSBuildServerDecisionReason = DecisionReason;
        telemetry.MSBuildServerFinalOutcome = FinalOutcome;
        telemetry.MSBuildServerFallbackStage = FallbackStage;
        telemetry.MSBuildServerFallbackDetailedReason = FallbackDetailedReason;
        telemetry.MSBuildServerEffectiveMaxNodeCount = EffectiveMaxNodeCount;
        telemetry.MSBuildServerNodeReuseEnabled = NodeReuseEnabled;
        telemetry.MSBuildServerProjectKind = ProjectKind;
        telemetry.MSBuildServerStdOutEscapeHatchEnabled = StdOutEscapeHatchEnabled;
        telemetry.MSBuildServerClientExitType = ClientExitType;
    }
}

/// <summary>
/// Centralizes the decision of whether an msbuild.exe invocation should be dispatched
/// to MSBuild Server. Previously, this decision was scattered between <c>XMake.Main</c>
/// (env var + escape-hatch checks) and a private <c>CanRunServerBasedOnCommandLineSwitches</c>
/// helper (command-line argument exclusions).
/// </summary>
/// <remarks>
/// <para>
/// This class is the single source of truth for the top-level server-eligibility check.
/// Direct callers of <see cref="MSBuildClientApp.Execute(string[], System.Threading.CancellationToken)"/>
/// bypass this check; they are expected to have made their own eligibility decision.
/// </para>
/// <para>
/// MSBuild Server is opt-in via the <c>MSBUILDUSESERVER</c> environment variable. When
/// set to <c>"1"</c>, the server is used unless one of the exclusion conditions below
/// applies. Any other value (including unset and <c>"0"</c>) means "no server" and
    /// does not attempt to use the server.
/// </para>
/// <para>
/// Exclusions (each writes a distinct <see cref="MSBuildServerFallbackReason"/>):
/// <list type="bullet">
///   <item><description><b>EscapeHatch</b>: <c>MSBUILDENSURESTDOUTFORTASKPROCESSES=1</c>.</description></item>
///   <item><description><b>Arguments</b>: <c>-help</c>, <c>-version</c>, <c>-nodemode</c>,
///   binlog-as-project, or <c>-nodereuse:false</c>.</description></item>
///   <item><description><b>ErrorParsingCommandLine</b>: parse threw an exception.</description></item>
///   <item><description><b>SingleNode</b>: effective <c>-m:1</c> (explicit <c>-m:1</c> or no <c>-m</c>
///   at all). Server overhead is not justified for a single-node build.</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class MSBuildServerDecision
{
    internal const string RequestStateNotRequested = "NotRequested";
    internal const string RequestStateRequested = "Requested";

    internal const string EnvVarValueUnset = "Unset";
    internal const string EnvVarValueZero = "0";
    internal const string EnvVarValueOne = "1";
    internal const string EnvVarValueOther = "Other";

    internal const string DecisionNotRequested = "NotRequested";
    internal const string DecisionSkippedBeforeLaunch = "SkippedBeforeLaunch";
    internal const string DecisionAttemptServer = "AttemptServer";

    internal const string DecisionReasonEnvVarUnset = "EnvVarUnset";
    internal const string DecisionReasonEnvVarZero = "EnvVarZero";
    internal const string DecisionReasonEnvVarOther = "EnvVarOther";
    internal const string DecisionReasonEligible = "Eligible";
    internal const string DecisionReasonEscapeHatch = "EscapeHatch";
    internal const string DecisionReasonHelp = "Help";
    internal const string DecisionReasonVersion = "Version";
    internal const string DecisionReasonNodeMode = "NodeMode";
    internal const string DecisionReasonBinaryLogReplay = "BinaryLogReplay";
    internal const string DecisionReasonNodeReuseDisabled = "NodeReuseDisabled";
    internal const string DecisionReasonSingleNode = "SingleNode";
    internal const string DecisionReasonErrorParsingCommandLine = "ErrorParsingCommandLine";

    internal const string FallbackStagePreLaunch = "PreLaunch";
    internal const string FallbackStagePostLaunch = "PostLaunch";

    internal const string FinalOutcomeNotRequested = "NotRequested";
    internal const string FinalOutcomeSkippedBeforeLaunch = "SkippedBeforeLaunch";
    internal const string FinalOutcomeAttemptServer = "AttemptServer";
    internal const string FinalOutcomeRanOnServer = "RanOnServer";
    internal const string FinalOutcomeFallbackToInProc = "FallbackToInProc";
    internal const string FinalOutcomeClientFailure = "ClientFailure";

    internal const string ProjectKindProject = "Project";
    internal const string ProjectKindSolution = "Solution";
    internal const string ProjectKindSolutionFilter = "SolutionFilter";
    internal const string ProjectKindBinaryLog = "BinaryLog";
    internal const string ProjectKindUnknown = "Unknown";

    /// <summary>
    /// Decides whether the current msbuild.exe invocation should run on MSBuild Server.
    /// </summary>
    /// <param name="commandLine">The full command line as received by <c>Main</c>, including the executable path as the first element.</param>
    /// <param name="commandLineParser">The shared <see cref="CommandLineParser"/> instance. Safe to call repeatedly; <c>GatherAllSwitches</c> resets its internal gathering state.</param>
    /// <param name="traits">The <see cref="Traits"/> instance whose escape hatches and feature flags participate in the decision.</param>
    /// <returns>A <see cref="MSBuildServerDecisionResult"/> describing the decision and an optional fallback reason for telemetry.</returns>
    public static MSBuildServerDecisionResult Decide(
        string[] commandLine,
        CommandLineParser commandLineParser,
        Traits traits)
    {
        // Step 1: server is fully opt-in via MSBUILDUSESERVER=1. When the user did
        // not opt in, return a "None" fallback reason so the compatibility
        // ServerFallbackReason field remains unset while the granular fields can
        // still explain why the server was not requested.
        string? envVarValue = Environment.GetEnvironmentVariable(Traits.UseMSBuildServerEnvVarName);
        string normalizedEnvVarValue = NormalizeServerEnvVarValue(envVarValue);
        bool stdoutEscapeHatchEnabled = traits.EscapeHatches.EnsureStdOutForChildNodesIsPrimaryStdout;

        if (envVarValue != "1")
        {
            return CreateNotRequestedResult(normalizedEnvVarValue, GetNotRequestedReason(normalizedEnvVarValue), stdoutEscapeHatchEnabled);
        }

        // Step 2: known incompatibility with redirected stdout for child task processes.
        if (stdoutEscapeHatchEnabled)
        {
            return CreateSkippedResult(
                MSBuildServerFallbackReason.EscapeHatch,
                normalizedEnvVarValue,
                DecisionReasonEscapeHatch,
                effectiveMaxNodeCount: null,
                nodeReuseEnabled: null,
                projectKind: null,
                stdoutEscapeHatchEnabled);
        }

        // Step 3: argument-based exclusions. Parse the command line with a LOCAL
        // deferred-message list so this dry-run does not pollute the global list
        // that the real build parse will populate later in XMake.Execute.
        int cpuCount;
        bool nodeReuse;
        string projectKind;
        try
        {
            List<DeferredBuildMessage> localDeferredMessages = new();

            commandLineParser.GatherAllSwitches(
                commandLine,
                localDeferredMessages,
                out CommandLineSwitches switchesFromAutoResponseFile,
                out CommandLineSwitches switchesNotFromAutoResponseFile,
                out string fullCommandLine,
                out _);

            CommandLineSwitches commandLineSwitches = MSBuildApp.CombineSwitchesRespectingPriority(
                switchesFromAutoResponseFile,
                switchesNotFromAutoResponseFile,
                fullCommandLine);

            if (commandLineParser.CheckAndGatherProjectAutoResponseFile(switchesFromAutoResponseFile, commandLineSwitches, false, fullCommandLine))
            {
                commandLineSwitches = MSBuildApp.CombineSwitchesRespectingPriority(
                    switchesFromAutoResponseFile,
                    switchesNotFromAutoResponseFile,
                    fullCommandLine);
            }

            string projectFile = MSBuildApp.ProcessProjectSwitch(
                commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.Project],
                commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.IgnoreProjectExtensions],
                Directory.GetFiles);
            projectKind = GetProjectKind(projectFile);

            nodeReuse = MSBuildApp.ProcessNodeReuseSwitch(commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.NodeReuse]);
            cpuCount = MSBuildApp.ProcessMaxCPUCountSwitch(
                commandLineSwitches[CommandLineSwitches.ParameterizedSwitch.MaxCPUCount]);

            if (commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.Help])
            {
                return CreateArgumentsResult(normalizedEnvVarValue, DecisionReasonHelp, cpuCount, nodeReuse, projectKind, stdoutEscapeHatchEnabled);
            }

            if (commandLineSwitches.IsParameterizedSwitchSet(CommandLineSwitches.ParameterizedSwitch.NodeMode))
            {
                return CreateArgumentsResult(normalizedEnvVarValue, DecisionReasonNodeMode, cpuCount, nodeReuse, projectKind, stdoutEscapeHatchEnabled);
            }

            if (commandLineSwitches[CommandLineSwitches.ParameterlessSwitch.Version])
            {
                return CreateArgumentsResult(normalizedEnvVarValue, DecisionReasonVersion, cpuCount, nodeReuse, projectKind, stdoutEscapeHatchEnabled);
            }

            if (FileUtilities.IsBinaryLogFilename(projectFile))
            {
                return CreateArgumentsResult(normalizedEnvVarValue, DecisionReasonBinaryLogReplay, cpuCount, nodeReuse, projectKind, stdoutEscapeHatchEnabled);
            }

            if (!nodeReuse)
            {
                return CreateArgumentsResult(normalizedEnvVarValue, DecisionReasonNodeReuseDisabled, cpuCount, nodeReuse, projectKind, stdoutEscapeHatchEnabled);
            }
        }
        catch (Exception ex)
        {
            CommunicationsUtilities.Trace(
                $"Unexpected exception during command line parsing. Can not determine if it is allowed to use Server. Fall back to old behavior. Exception: {ex}");
            return CreateSkippedResult(
                MSBuildServerFallbackReason.ErrorParsingCommandLine,
                normalizedEnvVarValue,
                DecisionReasonErrorParsingCommandLine,
                effectiveMaxNodeCount: null,
                nodeReuseEnabled: null,
                projectKind: null,
                stdoutEscapeHatchEnabled);
        }

        // Step 4: skip server when effective cpuCount == 1. The parser rewrites bare
        // -m to -m:<NumberOfCores>, so cpuCount == 1 truly means "single-node build
        // was requested" or "-m was omitted entirely" (e.g., msbuild.exe invoked from
        // Visual Studio). Server overhead is not justified for a single-node build.
        if (cpuCount == 1)
        {
            return CreateSkippedResult(
                MSBuildServerFallbackReason.SingleNode,
                normalizedEnvVarValue,
                DecisionReasonSingleNode,
                cpuCount,
                nodeReuse,
                projectKind,
                stdoutEscapeHatchEnabled);
        }

        return new MSBuildServerDecisionResult(
            UseServer: true,
            FallbackReason: MSBuildServerFallbackReason.None,
            RequestState: RequestStateRequested,
            EnvVarValue: normalizedEnvVarValue,
            Decision: DecisionAttemptServer,
            DecisionReason: DecisionReasonEligible,
            FinalOutcome: FinalOutcomeAttemptServer,
            FallbackStage: null,
            FallbackDetailedReason: null,
            EffectiveMaxNodeCount: cpuCount,
            NodeReuseEnabled: nodeReuse,
            ProjectKind: projectKind,
            StdOutEscapeHatchEnabled: stdoutEscapeHatchEnabled);
    }

    private static MSBuildServerDecisionResult CreateNotRequestedResult(
        string envVarValue,
        string decisionReason,
        bool stdoutEscapeHatchEnabled)
    {
        return new MSBuildServerDecisionResult(
            UseServer: false,
            FallbackReason: MSBuildServerFallbackReason.None,
            RequestState: RequestStateNotRequested,
            EnvVarValue: envVarValue,
            Decision: DecisionNotRequested,
            DecisionReason: decisionReason,
            FinalOutcome: FinalOutcomeNotRequested,
            FallbackStage: null,
            FallbackDetailedReason: null,
            EffectiveMaxNodeCount: null,
            NodeReuseEnabled: null,
            ProjectKind: null,
            StdOutEscapeHatchEnabled: stdoutEscapeHatchEnabled);
    }

    private static MSBuildServerDecisionResult CreateArgumentsResult(
        string envVarValue,
        string decisionReason,
        int? effectiveMaxNodeCount,
        bool? nodeReuseEnabled,
        string? projectKind,
        bool stdoutEscapeHatchEnabled)
    {
        return CreateSkippedResult(
            MSBuildServerFallbackReason.Arguments,
            envVarValue,
            decisionReason,
            effectiveMaxNodeCount,
            nodeReuseEnabled,
            projectKind,
            stdoutEscapeHatchEnabled);
    }

    private static MSBuildServerDecisionResult CreateSkippedResult(
        MSBuildServerFallbackReason fallbackReason,
        string envVarValue,
        string decisionReason,
        int? effectiveMaxNodeCount,
        bool? nodeReuseEnabled,
        string? projectKind,
        bool stdoutEscapeHatchEnabled)
    {
        return new MSBuildServerDecisionResult(
            UseServer: false,
            FallbackReason: fallbackReason,
            RequestState: RequestStateRequested,
            EnvVarValue: envVarValue,
            Decision: DecisionSkippedBeforeLaunch,
            DecisionReason: decisionReason,
            FinalOutcome: FinalOutcomeSkippedBeforeLaunch,
            FallbackStage: FallbackStagePreLaunch,
            FallbackDetailedReason: decisionReason,
            EffectiveMaxNodeCount: effectiveMaxNodeCount,
            NodeReuseEnabled: nodeReuseEnabled,
            ProjectKind: projectKind,
            StdOutEscapeHatchEnabled: stdoutEscapeHatchEnabled);
    }

    private static string NormalizeServerEnvVarValue(string? envVarValue)
    {
        return envVarValue switch
        {
            null => EnvVarValueUnset,
            "0" => EnvVarValueZero,
            "1" => EnvVarValueOne,
            _ => EnvVarValueOther,
        };
    }

    private static string GetNotRequestedReason(string normalizedEnvVarValue)
    {
        return normalizedEnvVarValue switch
        {
            EnvVarValueUnset => DecisionReasonEnvVarUnset,
            EnvVarValueZero => DecisionReasonEnvVarZero,
            _ => DecisionReasonEnvVarOther,
        };
    }

    private static string GetProjectKind(string? projectFile)
    {
        if (projectFile is null || projectFile.Length == 0)
        {
            return ProjectKindUnknown;
        }

        if (FileUtilities.IsBinaryLogFilename(projectFile))
        {
            return ProjectKindBinaryLog;
        }

        if (FileUtilities.IsSolutionFilterFilename(projectFile))
        {
            return ProjectKindSolutionFilter;
        }

        if (FileUtilities.IsSolutionFilename(projectFile))
        {
            return ProjectKindSolution;
        }

        return ProjectKindProject;
    }
}
