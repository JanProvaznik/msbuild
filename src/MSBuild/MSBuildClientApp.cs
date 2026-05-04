// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Experimental;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Telemetry;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

#if RUNTIME_TYPE_NETCORE
using System.IO;
using System.Diagnostics;
#endif

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// This class implements client for MSBuild server. It
    /// 1. starts the MSBuild server in a separate process if it does not yet exist.
    /// 2. establishes a connection with MSBuild server and sends a build request.
    /// 3. if server is busy, it falls back to old build behavior.
    /// </summary>
    internal static class MSBuildClientApp
    {
        /// <summary>
        /// This is the entry point for the MSBuild client.
        /// </summary>
        /// <param name="commandLine">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and
        /// is ignored.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A value of type <see cref="MSBuildApp.ExitType"/> that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        /// <remarks>
        /// The locations of msbuild exe/dll and dotnet.exe would be automatically detected if called from dotnet or msbuild cli. Calling this function from other executables might not work.
        /// </remarks>
        public static MSBuildApp.ExitType Execute(string[] commandLineArgs, CancellationToken cancellationToken)
        {
            string msbuildLocation = BuildEnvironmentHelper.Instance.CurrentMSBuildExePath;

            return Execute(
                commandLineArgs,
                msbuildLocation,
                cancellationToken);
        }

        /// <summary>
        /// This is the entry point for the MSBuild client.
        /// </summary>
        /// <param name="commandLineArgs">The command line to process. The first argument
        /// on the command line is assumed to be the name/path of the executable, and
        /// is ignored.</param>
        /// <param name="msbuildLocation"> Full path to current MSBuild.exe if executable is MSBuild.exe,
        /// or to version of MSBuild.dll found to be associated with the current process.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A value of type <see cref="MSBuildApp.ExitType"/> that indicates whether the build succeeded,
        /// or the manner in which it failed.</returns>
        public static MSBuildApp.ExitType Execute(string[] commandLineArgs, string msbuildLocation, CancellationToken cancellationToken)
        {
            MSBuildClientExitResult exitResult;
            try
            {
                MSBuildClient msbuildClient = new MSBuildClient(commandLineArgs, msbuildLocation);
                exitResult = msbuildClient.Execute(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !ExceptionHandling.IsCriticalException(ex))
            {
                // Defense-in-depth fallback: any unexpected exception from the client path
                // (pipe errors, handshake errors, environment problems, etc.) must NOT crash
                // the build. Log it and fall back to in-proc MSBuild. The most common case
                // historically was an uncaught TimeoutException from NamedPipeClientStream.Connect
                // (see investigation.md Thread E) but other exception classes can surface as
                // server mode evolves; this catch keeps the server an opportunistic optimization
                // rather than a hard dependency.
                //
                // Note: OperationCanceledException is explicitly excluded so cancellation
                // requests (e.g. Ctrl-C) are not converted into an in-proc retry.
                if (KnownTelemetry.PartialBuildTelemetry != null)
                {
                    KnownTelemetry.PartialBuildTelemetry.ServerFallbackReason = "ClientUnhandledException:" + ex.GetType().Name;
                }
                CommunicationsUtilities.Trace($"MSBuild server client threw an unexpected exception; falling back to in-proc build: {ex}");
                return MSBuildApp.Execute(commandLineArgs);
            }

            if (exitResult.MSBuildClientExitType == MSBuildClientExitType.ServerBusy ||
                exitResult.MSBuildClientExitType == MSBuildClientExitType.UnableToConnect ||
                exitResult.MSBuildClientExitType == MSBuildClientExitType.UnknownServerState ||
                exitResult.MSBuildClientExitType == MSBuildClientExitType.LaunchError)
            {
                if (KnownTelemetry.PartialBuildTelemetry != null)
                {
                    KnownTelemetry.PartialBuildTelemetry.ServerFallbackReason = exitResult.MSBuildClientExitType.ToString();
                }

                // Server is busy, fallback to old behavior.
                return MSBuildApp.Execute(commandLineArgs);
            }

            if (exitResult.MSBuildClientExitType == MSBuildClientExitType.Success &&
                Enum.TryParse(exitResult.MSBuildAppExitTypeString, out MSBuildApp.ExitType MSBuildAppExitType))
            {
                // The client successfully set up a build task for MSBuild server and received the result.
                // (Which could be a failure as well). Return the received exit type.
                return MSBuildAppExitType;
            }

            return MSBuildApp.ExitType.MSBuildClientFailure;
        }
    }
}
