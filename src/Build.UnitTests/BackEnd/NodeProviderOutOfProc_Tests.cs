// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO.Pipes;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests.BackEnd
{
    /// <summary>
    /// Tests for NodeProviderOutOfProc, specifically the node over-provisioning detection feature.
    /// </summary>
    public class NodeProviderOutOfProc_Tests
    {
        /// <summary>
        /// Test helper class to expose protected methods for testing.
        /// Uses configurable overrides for testing.
        /// </summary>
        private sealed class TestableNodeProviderOutOfProcBase : NodeProviderOutOfProcBase
        {
            private readonly int _systemWideNodeCount;
            private readonly int? _thresholdOverride;

            public TestableNodeProviderOutOfProcBase(int systemWideNodeCount, int? thresholdOverride = null)
            {
                _systemWideNodeCount = systemWideNodeCount;
                _thresholdOverride = thresholdOverride;
            }

            protected override int GetNodeReuseThreshold()
            {
                // If threshold is overridden, use it; otherwise use base implementation
                return _thresholdOverride ?? base.GetNodeReuseThreshold();
            }

            protected override int CountSystemWideActiveNodes()
            {
                return _systemWideNodeCount;
            }

            public bool[] TestDetermineNodesForReuse(int nodeCount, bool enableReuse)
            {
                return DetermineNodesForReuse(nodeCount, enableReuse);
            }

            public int TestGetNodeReuseThreshold()
            {
                return GetNodeReuseThreshold();
            }
        }

        [Fact]
        public void DetermineNodesForReuse_WhenReuseDisabled_AllNodesShouldTerminate()
        {
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 10, thresholdOverride: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: false);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == false);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenThresholdIsZero_AllNodesShouldTerminate()
        {
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 10, thresholdOverride: 0);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == false);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenUnderThreshold_AllNodesShouldBeReused()
        {
            // System has 3 nodes total, threshold is 4, so we're under the limit
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 3, thresholdOverride: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == true);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenAtThreshold_AllNodesShouldBeReused()
        {
            // System has 4 nodes total, threshold is 4, so we're at the limit
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 4, thresholdOverride: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 4, enableReuse: true);
            
            result.Length.ShouldBe(4);
            result.ShouldAllBe(shouldReuse => shouldReuse == true);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenOverThreshold_ExcessNodesShouldTerminate()
        {
            // System has 10 nodes total, threshold is 4
            // This instance has 3 nodes
            // We should keep 0 nodes from this instance (since 10 - 3 = 7, which is already > threshold)
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 10, thresholdOverride: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == false);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenSlightlyOverThreshold_SomeNodesShouldBeReused()
        {
            // System has 6 nodes total, threshold is 4
            // This instance has 3 nodes
            // Other instances have 6 - 3 = 3 nodes
            // We need to reduce by 2 nodes to reach threshold
            // So we should keep 1 node from this instance
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 6, thresholdOverride: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            // First node should be reused, others should terminate
            result[0].ShouldBeTrue();
            result[1].ShouldBeFalse();
            result[2].ShouldBeFalse();
        }

        [Fact]
        public void DetermineNodesForReuse_WithSingleNode_BehavesCorrectly()
        {
            // System has 5 nodes total, threshold is 4
            // This instance has 1 node
            // We're over threshold, but only by 1
            // We should terminate this node since others already meet threshold
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 5, thresholdOverride: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 1, enableReuse: true);
            
            result.Length.ShouldBe(1);
            result[0].ShouldBeFalse();
        }

        /// <summary>
        /// Regression test for the VMR fsharp build TimeoutException crash (investigation.md Thread E).
        ///
        /// Before the fix, TryConnectToPipeStream would let TimeoutException propagate from
        /// NamedPipeClientStream.Connect when the pipe was unavailable (e.g., server in the
        /// recycling gap between BuildCompleteReuse cycles). The exception bypassed the
        /// fallback path in MSBuildClientApp.Execute and crashed the build process.
        ///
        /// After the fix, the timeout is caught and reported as HandshakeStatus.Timeout, which
        /// allows MSBuildClient.TryConnectToServer to set MSBuildClientExitType.UnableToConnect,
        /// triggering the in-proc fallback in MSBuildClientApp.Execute.
        /// </summary>
        [Fact]
        public void TryConnectToPipeStream_WhenPipeUnavailable_ReturnsTimeoutInsteadOfThrowing()
        {
            // Use a pipe name that is virtually guaranteed not to exist.
            string pipeName = "MSBuild_NonexistentPipe_" + Guid.NewGuid().ToString("N");

            using NamedPipeClientStream nodeStream = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
#if FEATURE_PIPEOPTIONS_CURRENTUSERONLY
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#else
                PipeOptions.Asynchronous);
#endif

            Handshake handshake = new Handshake(HandshakeOptions.None);

            // Use a tiny timeout to keep the test fast. The exact value doesn't matter; the
            // pipe will never exist. Pre-fix this call would throw TimeoutException.
            bool connected = NodeProviderOutOfProcBase.TryConnectToPipeStream(
                nodeStream,
                pipeName,
                handshake,
                timeout: 100,
                out HandshakeResult result);

            connected.ShouldBeFalse("Connecting to a nonexistent pipe must not succeed.");
            result.ShouldNotBeNull();
            result.Status.ShouldBe(
                HandshakeStatus.Timeout,
                "TryConnectToPipeStream must convert NamedPipeClientStream.Connect timeout into HandshakeStatus.Timeout instead of throwing. " +
                "If this assertion fails, the M1 fix in NodeProviderOutOfProcBase.TryConnectToPipeStream has regressed and the VMR fsharp TimeoutException crash will reappear under MSBuild server load.");
        }
    }
}
