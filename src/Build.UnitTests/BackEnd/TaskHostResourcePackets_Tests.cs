// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;
using Microsoft.Build.CommandLine;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests.BackEnd
{
    /// <summary>
    /// Unit Tests for TaskHostResourceRequest and TaskHostResourceResponse packets.
    /// </summary>
    public class TaskHostResourcePackets_Tests
    {
        /// <summary>
        /// Test the constructor for TaskHostResourceRequest with RequestCores.
        /// </summary>
        [Fact]
        public void TestResourceRequestConstructor_RequestCores()
        {
            TaskHostResourceRequest request = new TaskHostResourceRequest(42, TaskHostResourceRequestType.RequestCores, 4);
            
            request.RequestId.ShouldBe(42);
            request.RequestType.ShouldBe(TaskHostResourceRequestType.RequestCores);
            request.NumCores.ShouldBe(4);
            request.Type.ShouldBe(NodePacketType.TaskHostResourceRequest);
        }

        /// <summary>
        /// Test the constructor for TaskHostResourceRequest with ReleaseCores.
        /// </summary>
        [Fact]
        public void TestResourceRequestConstructor_ReleaseCores()
        {
            TaskHostResourceRequest request = new TaskHostResourceRequest(123, TaskHostResourceRequestType.ReleaseCores, 2);
            
            request.RequestId.ShouldBe(123);
            request.RequestType.ShouldBe(TaskHostResourceRequestType.ReleaseCores);
            request.NumCores.ShouldBe(2);
            request.Type.ShouldBe(NodePacketType.TaskHostResourceRequest);
        }

        /// <summary>
        /// Test serialization and deserialization of TaskHostResourceRequest.
        /// </summary>
        [Fact]
        public void TestResourceRequestTranslation()
        {
            TaskHostResourceRequest request = new TaskHostResourceRequest(456, TaskHostResourceRequestType.RequestCores, 8);

            ((ITranslatable)request).Translate(TranslationHelpers.GetWriteTranslator());
            INodePacket packet = TaskHostResourceRequest.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            packet.ShouldBeOfType<TaskHostResourceRequest>();
            
            TaskHostResourceRequest deserializedRequest = (TaskHostResourceRequest)packet;
            deserializedRequest.RequestId.ShouldBe(456);
            deserializedRequest.RequestType.ShouldBe(TaskHostResourceRequestType.RequestCores);
            deserializedRequest.NumCores.ShouldBe(8);
        }

        /// <summary>
        /// Test the constructor for TaskHostResourceResponse.
        /// </summary>
        [Fact]
        public void TestResourceResponseConstructor()
        {
            TaskHostResourceResponse response = new TaskHostResourceResponse(42, TaskHostResourceRequestType.RequestCores, 3);
            
            response.RequestId.ShouldBe(42);
            response.RequestType.ShouldBe(TaskHostResourceRequestType.RequestCores);
            response.NumCoresGranted.ShouldBe(3);
            response.Type.ShouldBe(NodePacketType.TaskHostResourceResponse);
        }

        /// <summary>
        /// Test serialization and deserialization of TaskHostResourceResponse.
        /// </summary>
        [Fact]
        public void TestResourceResponseTranslation()
        {
            TaskHostResourceResponse response = new TaskHostResourceResponse(789, TaskHostResourceRequestType.ReleaseCores, 0);

            ((ITranslatable)response).Translate(TranslationHelpers.GetWriteTranslator());
            INodePacket packet = TaskHostResourceResponse.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            packet.ShouldBeOfType<TaskHostResourceResponse>();
            
            TaskHostResourceResponse deserializedResponse = (TaskHostResourceResponse)packet;
            deserializedResponse.RequestId.ShouldBe(789);
            deserializedResponse.RequestType.ShouldBe(TaskHostResourceRequestType.ReleaseCores);
            deserializedResponse.NumCoresGranted.ShouldBe(0);
        }

        /// <summary>
        /// Test round-trip serialization with multiple cores granted.
        /// </summary>
        [Fact]
        public void TestRoundTripWithMultipleCores()
        {
            TaskHostResourceResponse response = new TaskHostResourceResponse(999, TaskHostResourceRequestType.RequestCores, 16);

            ((ITranslatable)response).Translate(TranslationHelpers.GetWriteTranslator());
            TaskHostResourceResponse deserializedResponse = 
                (TaskHostResourceResponse)TaskHostResourceResponse.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            deserializedResponse.RequestId.ShouldBe(999);
            deserializedResponse.RequestType.ShouldBe(TaskHostResourceRequestType.RequestCores);
            deserializedResponse.NumCoresGranted.ShouldBe(16);
        }

        /// <summary>
        /// Test request with zero cores.
        /// </summary>
        [Fact]
        public void TestResourceRequestWithZeroCores()
        {
            TaskHostResourceRequest request = new TaskHostResourceRequest(100, TaskHostResourceRequestType.RequestCores, 0);
            
            request.RequestId.ShouldBe(100);
            request.RequestType.ShouldBe(TaskHostResourceRequestType.RequestCores);
            request.NumCores.ShouldBe(0);
        }
    }
}
