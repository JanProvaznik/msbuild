﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Logging.LiveLogger
{
    internal class LiveLoggerProjectNode
    {
        /// <summary>
        /// Given a list of paths, this method will get the shortest not ambiguous path for a project.
        /// Example: for `/users/documents/foo/project.csproj` and `/users/documents/bar/project.csproj`, the respective non ambiguous paths would be `foo/project.csproj` and `bar/project.csproj`
        /// Still work in progress...
        /// </summary>
        private static string GetUnambiguousPath(string path)
        {
            return Path.GetFileName(path);
        }

        public int Id;
        public string ProjectPath;
        public string TargetFramework;
        public bool Finished;
        // Line to display project info
        public LiveLoggerBufferLine? Line;
        // Targets
        public int FinishedTargets;
        public LiveLoggerBufferLine? CurrentTargetLine;
        public LiveLoggerTargetNode? CurrentTargetNode;
        // Messages, errors and warnings
        public List<LiveLoggerMessageNode> AdditionalDetails = new();
        // Count messages, warnings and errors
        public int MessageCount = 0;
        public int WarningCount = 0;
        public int ErrorCount = 0;
        // Bool if node should rerender
        internal bool ShouldRerender = true;
        public LiveLoggerProjectNode(ProjectStartedEventArgs args)
        {
            Id = args.ProjectId;
            ProjectPath = args.ProjectFile!;
            Finished = false;
            FinishedTargets = 0;
            if (args.GlobalProperties != null && args.GlobalProperties.ContainsKey("TargetFramework"))
            {
                TargetFramework = args.GlobalProperties["TargetFramework"];
            }
            else
            {
                TargetFramework = "";
            }
        }

        // TODO: Rename to Render() after FancyLogger's API becomes internal
        public void Log()
        {
            if (!ShouldRerender)
            {
                return;
            }

            ShouldRerender = false;
            // Project details
            string lineContents = ANSIBuilder.Alignment.SpaceBetween(
                // Show indicator
                (Finished ? ANSIBuilder.Formatting.Color("✓", ANSIBuilder.Formatting.ForegroundColor.Green) : ANSIBuilder.Formatting.Blinking(ANSIBuilder.Graphics.Spinner())) +
                // Project
                ANSIBuilder.Formatting.Dim("Project: ") +
                // Project file path with color
                $"{ANSIBuilder.Formatting.Color(ANSIBuilder.Formatting.Bold(GetUnambiguousPath(ProjectPath)), Finished ? ANSIBuilder.Formatting.ForegroundColor.Green : ANSIBuilder.Formatting.ForegroundColor.Default)} [{TargetFramework ?? "*"}]",
                $"({MessageCount} Messages, {WarningCount} Warnings, {ErrorCount} Errors)",
                Console.WindowWidth);
            // Create or update line
            if (Line is null)
            {
                Line = LiveLoggerBuffer.WriteNewLine(lineContents, false);
            }
            else
            {
                Line.Text = lineContents;
            }

            // For finished projects
            if (Finished)
            {
                if (CurrentTargetLine is not null)
                {
                    LiveLoggerBuffer.DeleteLine(CurrentTargetLine.Id);
                }

                foreach (LiveLoggerMessageNode node in AdditionalDetails.ToList())
                {
                    // Only delete high priority messages
                    if (node.Type != LiveLoggerMessageNode.MessageType.HighPriorityMessage)
                    {
                        continue;
                    }

                    if (node.Line is not null)
                    {
                        LiveLoggerBuffer.DeleteLine(node.Line.Id);
                    }
                }
            }

            // Current target details
            if (CurrentTargetNode is null)
            {
                return;
            }

            string currentTargetLineContents = $"    └── {CurrentTargetNode.TargetName} : {CurrentTargetNode.CurrentTaskNode?.TaskName ?? String.Empty}";
            if (CurrentTargetLine is null)
            {
                CurrentTargetLine = LiveLoggerBuffer.WriteNewLineAfter(Line!.Id, currentTargetLineContents);
            }
            else
            {
                CurrentTargetLine.Text = currentTargetLineContents;
            }

            // Messages, warnings and errors
            foreach (LiveLoggerMessageNode node in AdditionalDetails)
            {
                if (Finished && node.Type == LiveLoggerMessageNode.MessageType.HighPriorityMessage)
                {
                    continue;
                }

                if (node.Line is null)
                {
                    node.Line = LiveLoggerBuffer.WriteNewLineAfter(Line!.Id, "Message");
                }

                node.Log();
            }
        }

        public LiveLoggerTargetNode AddTarget(TargetStartedEventArgs args)
        {
            CurrentTargetNode = new LiveLoggerTargetNode(args);
            return CurrentTargetNode;
        }
        public LiveLoggerTaskNode? AddTask(TaskStartedEventArgs args)
        {
            // Get target id
            int targetId = args.BuildEventContext!.TargetId;
            if (CurrentTargetNode?.Id == targetId)
            {
                return CurrentTargetNode.AddTask(args);
            }
            else
            {
                return null;
            }
        }
        public LiveLoggerMessageNode? AddMessage(BuildMessageEventArgs args)
        {
            if (args.Importance != MessageImportance.High)
            {
                return null;
            }

            MessageCount++;
            LiveLoggerMessageNode node = new LiveLoggerMessageNode(args);
            AdditionalDetails.Add(node);
            return node;
        }
        public LiveLoggerMessageNode? AddWarning(BuildWarningEventArgs args)
        {
            WarningCount++;
            LiveLoggerMessageNode node = new LiveLoggerMessageNode(args);
            AdditionalDetails.Add(node);
            return node;
        }
        public LiveLoggerMessageNode? AddError(BuildErrorEventArgs args)
        {
            ErrorCount++;
            LiveLoggerMessageNode node = new LiveLoggerMessageNode(args);
            AdditionalDetails.Add(node);
            return node;
        }
    }
}
