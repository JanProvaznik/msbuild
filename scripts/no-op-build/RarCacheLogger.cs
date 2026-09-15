// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Build.Framework;

// A lightweight logger for proving hits in timed builds without a binary logger.
// Both arms use the same logger and message policy. It never requests task inputs.
public sealed class RarCacheLogger : ILogger
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentDictionary<(int Node, int Project, int Task), DateTime> _starts = new();
    private long _rarTicks;
    private int _rarCount;
    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;
    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        eventSource.TaskStarted += (_, e) =>
        {
            if (e.TaskName == "ResolveAssemblyReference" && e.BuildEventContext is { } context)
            {
                _starts[(context.NodeId, context.ProjectContextId, context.TaskId)] = e.Timestamp;
            }
        };
        eventSource.TaskFinished += (_, e) =>
        {
            if (e.TaskName == "ResolveAssemblyReference" && e.BuildEventContext is { } context &&
                _starts.TryRemove((context.NodeId, context.ProjectContextId, context.TaskId), out DateTime start))
            {
                Interlocked.Add(ref _rarTicks, (e.Timestamp - start).Ticks);
                Interlocked.Increment(ref _rarCount);
            }
        };
        eventSource.MessageRaised += (_, e) =>
        {
            const string prefix = "RAR result cache ";
            if (e.Message is not string message || !message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return;
            }
            int end = message.IndexOf(':', prefix.Length);
            if (end < 0) { return; }
            _counts.AddOrUpdate(message[prefix.Length..end], 1, (_, count) => count + 1);
            _messages.Enqueue(message);
        };
    }

    public void Shutdown()
    {
        if (string.IsNullOrEmpty(Parameters))
        {
            throw new LoggerException("RAR cache metrics need an output file.");
        }
        File.WriteAllText(Parameters, JsonSerializer.Serialize(new
        {
            counts = _counts, messages = _messages, rarInvocations = _rarCount,
            rarMilliseconds = TimeSpan.FromTicks(_rarTicks).TotalMilliseconds
        }));
    }
}
