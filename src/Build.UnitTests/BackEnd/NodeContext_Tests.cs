// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    [InlineData(true, 3)]
    public void PacketReadAheadHonorsChangeWaveAndPreservesPackets(bool enabled, int maxReadSize)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDDISABLEFEATURESFROMVERSION", enabled ? null : "18.11");
        ChangeWaves.ResetStateForTests();

        // Six-byte frames cross the 64 KiB read-ahead boundary in the middle of a header.
        const int packetCount = 20000;
        using var input = new MemoryStream();
        using var writer = new BinaryWriter(input);
        for (int i = 0; i < packetCount; i++)
        {
            writer.Write((byte)NodePacketType.NodeBuildComplete);
            writer.Write(1);
            writer.Write(i % 2 == 0);
        }

        using var shutdown = new MemoryStream();
        new NodeShutdown(NodeShutdownReason.Requested).Translate(BinaryTranslator.GetWriteTranslator(shutdown));
        writer.Write((byte)NodePacketType.NodeShutdown);
        writer.Write((int)shutdown.Length);
        writer.Write(shutdown.ToArray());

        using var written = new ManualResetEventSlim();
        using var closed = new ManualResetEventSlim();
        using var stream = new PacketStream(input.ToArray(), maxReadSize, written);
        using Process process = Process.GetCurrentProcess();
        var handler = new PacketHandler();
        var factory = new NodePacketFactory();
        factory.RegisterPacketHandler(NodePacketType.NodeBuildComplete, NodeBuildComplete.FactoryForDeserialization, handler);
        factory.RegisterPacketHandler(NodePacketType.NodeShutdown, NodeShutdown.FactoryForDeserialization, handler);
        var context = new NodeProviderOutOfProcBase.NodeContext(1, process, stream, factory, _ => closed.Set(), 0);
        context.SendData(new NodeBuildComplete(false));
        written.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        context.BeginAsyncPacketRead();
        closed.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();

        stream.FirstReadSize.ShouldBe(enabled ? 64 * 1024 : 5);
        handler.Packets.Count.ShouldBe(packetCount + 1);
        for (int i = 0; i < packetCount; i++)
        {
            handler.Packets[i].ShouldBeOfType<NodeBuildComplete>().PrepareForReuse.ShouldBe(i % 2 == 0);
        }

        handler.Packets[packetCount].ShouldBeOfType<NodeShutdown>().Reason.ShouldBe(NodeShutdownReason.Requested);
    }

    private sealed class PacketHandler : INodePacketHandler
    {
        public List<INodePacket> Packets { get; } = [];
        public void PacketReceived(int node, INodePacket packet) => Packets.Add(packet);
    }

    private sealed class PacketStream(byte[] data, int maxReadSize, ManualResetEventSlim written) : MemoryStream(data)
    {
        public int FirstReadSize { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (FirstReadSize == 0)
            {
                FirstReadSize = count;
            }

            return base.Read(buffer, offset, Math.Min(count, maxReadSize));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            Task<int> read = Task.Factory.StartNew(_ => Read(buffer, offset, count), state, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            _ = read.ContinueWith(completed => callback?.Invoke(completed), TaskScheduler.Default);
            return read;
        }

        public override int EndRead(IAsyncResult result) => ((Task<int>)result).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => written.Set();
    }
}
