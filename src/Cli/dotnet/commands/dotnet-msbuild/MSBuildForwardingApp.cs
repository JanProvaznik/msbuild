﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.CommandLine;
using System.Diagnostics;
using Microsoft.DotNet.Cli.Telemetry;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.Tools.MSBuild
{
    public class MSBuildForwardingApp
    {
        internal const string TelemetrySessionIdEnvironmentVariableName = "DOTNET_CLI_TELEMETRY_SESSIONID";

        private MSBuildForwardingAppWithoutLogging _forwardingAppWithoutLogging;

        private static IEnumerable<string> ConcatTelemetryLogger(IEnumerable<string> argsToForward)
        {
            if (Telemetry.CurrentSessionId != null)
            {
                try
                {
                    Type loggerType = typeof(MSBuildLogger);
                    Type forwardingLoggerType = typeof(MSBuildForwardingLogger);

                    return argsToForward
                        .Concat(new[]
                        {
                            $"-distributedlogger:{loggerType.FullName},{loggerType.GetTypeInfo().Assembly.Location}*{forwardingLoggerType.FullName},{forwardingLoggerType.GetTypeInfo().Assembly.Location}"
                        });
                }
                catch (Exception)
                {
                    // Exceptions during telemetry shouldn't cause anything else to fail
                }
            }
            return argsToForward;
        }

        public MSBuildForwardingApp(IEnumerable<string> argsToForward, string msbuildPath = null, bool? executeOutOfProc = null)
        {
            _forwardingAppWithoutLogging = new MSBuildForwardingAppWithoutLogging(
                ConcatTelemetryLogger(argsToForward),
                msbuildPath,
                executeOutOfProc);

            // Add the performance log location to the environment of the target process.
            if (PerformanceLogManager.Instance != null && !string.IsNullOrEmpty(PerformanceLogManager.Instance.CurrentLogDirectory))
            {
                EnvironmentVariable(PerformanceLogManager.PerfLogDirEnvVar, PerformanceLogManager.Instance.CurrentLogDirectory);
            }
        }

        public void EnvironmentVariable(string name, string value)
        {
            _forwardingAppWithoutLogging.EnvironmentVariable(name, value);
        }

        public ProcessStartInfo GetProcessStartInfo()
        {
            EnvironmentVariable(TelemetrySessionIdEnvironmentVariableName, Telemetry.CurrentSessionId);

            return _forwardingAppWithoutLogging.GetProcessStartInfo();
        }

        public string GetConcatenatedArguments()
        {
            var argumentsUnescaped = _forwardingAppWithoutLogging.GetAllArgumentsUnescaped();
            return Cli.Utils.ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(argumentsUnescaped);
        }

        public virtual int Execute()
        {
            int exitCode;

            if (_forwardingAppWithoutLogging.ExecuteMSBuildOutOfProc)
            {
                // Ignore Ctrl-C for the remainder of the command's execution
                // Forwarding commands will just spawn the child process and exit
                Console.CancelKeyPress += (sender, e) => { e.Cancel = true; };

                ProcessStartInfo startInfo = GetProcessStartInfo();

                PerformanceLogEventSource.Log.LogMSBuildStart(startInfo.FileName, startInfo.Arguments);
                exitCode = startInfo.Execute();
                PerformanceLogEventSource.Log.MSBuildStop(exitCode);
            }
            else
            {
                string[] arguments = _forwardingAppWithoutLogging.GetAllArgumentsUnescaped();
                if (PerformanceLogEventSource.Log.IsEnabled())
                {
                    PerformanceLogEventSource.Log.LogMSBuildStart(string.Empty, ArgumentEscaper.EscapeAndConcatenateArgArrayForProcessStart(arguments));
                }
                exitCode = _forwardingAppWithoutLogging.ExecuteInProc(arguments);
                PerformanceLogEventSource.Log.MSBuildStop(exitCode);
            }

            return exitCode;
        }
    }
}
