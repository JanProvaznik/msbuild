// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.Framework.Telemetry;

namespace Microsoft.Build.BackEnd;

/// <summary>
/// Part of BuildTelemetry which is collected on client and needs to be sent to server,
/// so server can log BuildTelemetry once it is finished.
/// </summary>
internal sealed class PartialBuildTelemetry : ITranslatable
{
    internal const string FinalOutcomeRanOnServer = "RanOnServer";

    private DateTime _startedAt = default;
    private string? _initialServerState = default;
    private string? _serverFallbackReason = default;
    private string? _msBuildServerRequestState = default;
    private string? _msBuildServerEnvVarValue = default;
    private string? _msBuildServerDecision = default;
    private string? _msBuildServerDecisionReason = default;
    private string? _msBuildServerFallbackStage = default;
    private string? _msBuildServerFallbackDetailedReason = default;
    private string? _msBuildServerFinalOutcome = default;
    private int? _msBuildServerEffectiveMaxNodeCount = default;
    private bool? _msBuildServerNodeReuseEnabled = default;
    private string? _msBuildServerProjectKind = default;
    private bool? _msBuildServerStdOutEscapeHatchEnabled = default;
    private string? _msBuildServerClientExitType = default;

    public PartialBuildTelemetry(DateTime startedAt, string? initialServerState, string? serverFallbackReason)
    {
        _startedAt = startedAt;
        _initialServerState = initialServerState;
        _serverFallbackReason = serverFallbackReason;
    }

    public PartialBuildTelemetry(BuildTelemetry telemetry)
    {
        _startedAt = telemetry.StartAt.GetValueOrDefault();
        _initialServerState = telemetry.InitialMSBuildServerState;
        _serverFallbackReason = telemetry.ServerFallbackReason;
        _msBuildServerRequestState = telemetry.MSBuildServerRequestState;
        _msBuildServerEnvVarValue = telemetry.MSBuildServerEnvVarValue;
        _msBuildServerDecision = telemetry.MSBuildServerDecision;
        _msBuildServerDecisionReason = telemetry.MSBuildServerDecisionReason;
        _msBuildServerFallbackStage = telemetry.MSBuildServerFallbackStage;
        _msBuildServerFallbackDetailedReason = telemetry.MSBuildServerFallbackDetailedReason;
        _msBuildServerFinalOutcome = telemetry.MSBuildServerFinalOutcome;
        _msBuildServerEffectiveMaxNodeCount = telemetry.MSBuildServerEffectiveMaxNodeCount;
        _msBuildServerNodeReuseEnabled = telemetry.MSBuildServerNodeReuseEnabled;
        _msBuildServerProjectKind = telemetry.MSBuildServerProjectKind;
        _msBuildServerStdOutEscapeHatchEnabled = telemetry.MSBuildServerStdOutEscapeHatchEnabled;
        _msBuildServerClientExitType = telemetry.MSBuildServerClientExitType;
    }

    /// <summary>
    /// Constructor for deserialization
    /// </summary>
    private PartialBuildTelemetry()
    {
    }

    public DateTime? StartedAt => _startedAt;

    public string? InitialServerState => _initialServerState;

    public string? ServerFallbackReason => _serverFallbackReason;

    public void ApplyTo(BuildTelemetry telemetry)
    {
        telemetry.StartAt = StartedAt;
        telemetry.InitialMSBuildServerState = _initialServerState;
        telemetry.ServerFallbackReason = _serverFallbackReason;
        telemetry.MSBuildServerRequestState = _msBuildServerRequestState;
        telemetry.MSBuildServerEnvVarValue = _msBuildServerEnvVarValue;
        telemetry.MSBuildServerDecision = _msBuildServerDecision;
        telemetry.MSBuildServerDecisionReason = _msBuildServerDecisionReason;
        telemetry.MSBuildServerFallbackStage = _msBuildServerFallbackStage;
        telemetry.MSBuildServerFallbackDetailedReason = _msBuildServerFallbackDetailedReason;
        telemetry.MSBuildServerFinalOutcome = _msBuildServerFinalOutcome;
        telemetry.MSBuildServerEffectiveMaxNodeCount = _msBuildServerEffectiveMaxNodeCount;
        telemetry.MSBuildServerNodeReuseEnabled = _msBuildServerNodeReuseEnabled;
        telemetry.MSBuildServerProjectKind = _msBuildServerProjectKind;
        telemetry.MSBuildServerStdOutEscapeHatchEnabled = _msBuildServerStdOutEscapeHatchEnabled;
        telemetry.MSBuildServerClientExitType = _msBuildServerClientExitType;
    }

    public void Translate(ITranslator translator)
    {
        translator.Translate(ref _startedAt);
        translator.Translate(ref _initialServerState);
        translator.Translate(ref _serverFallbackReason);
        translator.Translate(ref _msBuildServerRequestState);
        translator.Translate(ref _msBuildServerEnvVarValue);
        translator.Translate(ref _msBuildServerDecision);
        translator.Translate(ref _msBuildServerDecisionReason);
        translator.Translate(ref _msBuildServerFallbackStage);
        translator.Translate(ref _msBuildServerFallbackDetailedReason);
        translator.Translate(ref _msBuildServerFinalOutcome);
        TranslateNullableInt(translator, ref _msBuildServerEffectiveMaxNodeCount);
        TranslateNullableBool(translator, ref _msBuildServerNodeReuseEnabled);
        translator.Translate(ref _msBuildServerProjectKind);
        TranslateNullableBool(translator, ref _msBuildServerStdOutEscapeHatchEnabled);
        translator.Translate(ref _msBuildServerClientExitType);
    }

    private static void TranslateNullableInt(ITranslator translator, ref int? value)
    {
        bool hasValue = value.HasValue;
        translator.Translate(ref hasValue);

        int concreteValue = value.GetValueOrDefault();
        translator.Translate(ref concreteValue);

        if (translator.Mode == TranslationDirection.ReadFromStream)
        {
            value = hasValue ? concreteValue : null;
        }
    }

    private static void TranslateNullableBool(ITranslator translator, ref bool? value)
    {
        bool hasValue = value.HasValue;
        translator.Translate(ref hasValue);

        bool concreteValue = value.GetValueOrDefault();
        translator.Translate(ref concreteValue);

        if (translator.Mode == TranslationDirection.ReadFromStream)
        {
            value = hasValue ? concreteValue : null;
        }
    }

    internal static PartialBuildTelemetry FactoryForDeserialization(ITranslator translator)
    {
        PartialBuildTelemetry partialTelemetryData = new();
        partialTelemetryData.Translate(translator);
        return partialTelemetryData;
    }
}
