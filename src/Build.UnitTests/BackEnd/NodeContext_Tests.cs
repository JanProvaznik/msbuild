// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Framework;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests.BackEnd;

public sealed class NodeContext_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PacketReadAheadHonorsChangeWave(bool enabled)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", enabled ? null : "18.11");
        ChangeWaves.ResetStateForTests();

        using var stream = new PacketStream(CreatePackets(1000, 8));
        RecordingPacketHandler handler = await ReadPackets(stream);

        handler.Packets.Count.ShouldBe(1001);
        handler.Packets[1000].ShouldBeOfType<NodeShutdown>().Reason.ShouldBe(NodeShutdownReason.Requested);
        stream.ReadSizes[0].ShouldBe(enabled ? 64 * 1024 : 5);
        stream.ReadSizes.Count.ShouldBe(enabled ? 1 : 2002);
    }

    [Theory]
    [InlineData(1, 8, 20, false)]
    [InlineData(3, 8, 20, true)]
    [InlineData(4093, 131072, 8, true)]
    [InlineData(int.MaxValue, 17, 10000, false)]
    public async Task BufferedPacketsSupportPartialReadsAndSynchronousCompletions(int maxReadSize, int payloadSize, int packetCount, bool completeAsynchronously)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", null);
        ChangeWaves.ResetStateForTests();

        using var stream = new PacketStream(CreatePackets(packetCount, payloadSize), maxReadSize, completeAsynchronously);
        RecordingPacketHandler handler = await ReadPackets(stream);

        handler.Packets.Count.ShouldBe(packetCount + 1);
        for (int i = 0; i < packetCount; i++)
        {
            TestPacket packet = handler.Packets[i].ShouldBeOfType<TestPacket>();
            packet.Sequence.ShouldBe(i);
            packet.Payload.Length.ShouldBe(payloadSize);
            packet.Payload.ShouldAllBe(value => value == (byte)(i % 251));
        }

        handler.Packets[packetCount].ShouldBeOfType<NodeShutdown>().Reason.ShouldBe(NodeShutdownReason.Requested);
        stream.ReadSizes[0].ShouldBe(64 * 1024);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task BufferedTruncatedPacketsReportConnectionFailure(int availableBytes)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", null);
        ChangeWaves.ResetStateForTests();

        byte[] data = CreatePackets(1, 8);
        Array.Resize(ref data, availableBytes);
        using var stream = new PacketStream(data);
        RecordingPacketHandler handler = await ReadPackets(stream);

        handler.Packets.Count.ShouldBe(1);
        handler.Packets[0].ShouldBeOfType<NodeShutdown>().Reason.ShouldBe(NodeShutdownReason.ConnectionFailed);
    }

    [Fact]
    public async Task BufferedReadIOExceptionReportsConnectionFailure()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", null);
        ChangeWaves.ResetStateForTests();

        using var stream = new PacketStream([]) { ThrowOnRead = true };
        RecordingPacketHandler handler = await ReadPackets(stream);

        handler.Packets.Count.ShouldBe(1);
        handler.Packets[0].ShouldBeOfType<NodeShutdown>().Reason.ShouldBe(NodeShutdownReason.ConnectionFailed);
    }

    [Fact]
    public async Task BufferedReadRoutesSparsePacketWithoutWaitingForFullBuffer()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", null);
        ChangeWaves.ResetStateForTests();

        string pipeName = $"MSBuildReadAheadTest-{Guid.NewGuid():N}";
        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync();
        await client.ConnectAsync(10000);
        await connection;

        var handler = new RecordingPacketHandler();
        var factory = new NodePacketFactory();
        factory.RegisterPacketHandler(NodePacketType.LogMessage, TestPacket.Deserialize, handler);
        factory.RegisterPacketHandler(NodePacketType.NodeShutdown, NodeShutdown.FactoryForDeserialization, handler);
        var terminated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using Process process = Process.GetCurrentProcess();
        var context = new NodeProviderOutOfProcBase.NodeContext(1, process, client, factory, id => terminated.TrySetResult(id), 0);
        context.SendData(new NodeBuildComplete(false));
        context.BeginAsyncPacketRead();

        byte[] data = CreatePackets(1, 8);
        int firstPacketLength = 5 + BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1, 4));
        server.Write(data, 0, firstPacketLength);
        await AwaitCompletion(handler.FirstPacket.Task);
        (await handler.FirstPacket.Task).ShouldBeOfType<TestPacket>().Sequence.ShouldBe(0);

        server.Write(data, firstPacketLength, data.Length - firstPacketLength);
        await AwaitCompletion(terminated.Task);
        handler.Packets.Count.ShouldBe(2);
        handler.Packets[1].ShouldBeOfType<NodeShutdown>().Reason.ShouldBe(NodeShutdownReason.Requested);
    }

    private static async Task<RecordingPacketHandler> ReadPackets(PacketStream stream)
    {
        var handler = new RecordingPacketHandler();
        var factory = new NodePacketFactory();
        factory.RegisterPacketHandler(NodePacketType.LogMessage, TestPacket.Deserialize, handler);
        factory.RegisterPacketHandler(NodePacketType.NodeShutdown, NodeShutdown.FactoryForDeserialization, handler);
        var terminated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using Process process = Process.GetCurrentProcess();
        var context = new NodeProviderOutOfProcBase.NodeContext(1, process, stream, factory, id => terminated.TrySetResult(id), 0);

        // Stop the independent writer as well as the reader so the test leaves no waiting drain thread.
        context.SendData(new NodeBuildComplete(false));
        await AwaitCompletion(stream.Written.Task);
        context.BeginAsyncPacketRead();
        await AwaitCompletion(terminated.Task);
        (await terminated.Task).ShouldBe(1);
        return handler;
    }

    private static async Task AwaitCompletion(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.ShouldBe(task, "the packet pump must complete without blocking or losing a packet");
        await task;
    }

    private static byte[] CreatePackets(int count, int payloadSize)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int i = 0; i <= count; i++)
        {
            INodePacket packet = i == count
                ? new NodeShutdown(NodeShutdownReason.Requested)
                : new TestPacket(i, payloadSize);
            long start = stream.Position;
            writer.Write((byte)packet.Type);
            writer.Write(0);
            packet.Translate(BinaryTranslator.GetWriteTranslator(stream));
            long end = stream.Position;
            stream.Position = start + 1;
            writer.Write((int)(end - start - 5));
            stream.Position = end;
        }

        return stream.ToArray();
    }

    private sealed class RecordingPacketHandler : INodePacketHandler
    {
        public List<INodePacket> Packets { get; } = [];
        public TaskCompletionSource<INodePacket> FirstPacket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PacketReceived(int node, INodePacket packet)
        {
            Packets.Add(packet);
            FirstPacket.TrySetResult(packet);
        }
    }

    private sealed class TestPacket : INodePacket
    {
        private int _sequence;
        private byte[] _payload;

        public TestPacket(int sequence, int payloadSize)
        {
            _sequence = sequence;
            _payload = new byte[payloadSize];
            for (int i = 0; i < payloadSize; i++)
            {
                _payload[i] = (byte)(sequence % 251);
            }
        }

        public int Sequence => _sequence;
        public byte[] Payload => _payload;
        public NodePacketType Type => NodePacketType.LogMessage;

        public void Translate(ITranslator translator)
        {
            translator.Translate(ref _sequence);
            translator.Translate(ref _payload);
        }

        public static INodePacket Deserialize(ITranslator translator)
        {
            var packet = new TestPacket(0, 0);
            packet.Translate(translator);
            return packet;
        }
    }

    private sealed class PacketStream(byte[] data, int maxReadSize = int.MaxValue, bool completeAsynchronously = false) : Stream
    {
        private readonly MemoryStream _input = new(data);
        public List<int> ReadSizes { get; } = [];
        public TaskCompletionSource<bool> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ThrowOnRead { get; init; }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (ThrowOnRead)
            {
                throw new IOException("Simulated pipe read failure.");
            }

            ReadSizes.Add(count);
            return _input.Read(buffer, offset, Math.Min(count, maxReadSize));
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (completeAsynchronously)
            {
                await Task.Yield();
            }

            return Read(buffer, offset, count);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            Task<int> task = Task.Factory.StartNew(_ => Read(buffer, offset, count), state, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            if (callback is not null)
            {
                _ = task.ContinueWith(completed => callback(completed), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            return task;
        }

        public override int EndRead(IAsyncResult asyncResult) => ((Task<int>)asyncResult).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => Written.TrySetResult(true);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _input.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
