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
    /// Unit Tests for TaskHostYieldRequest and TaskHostYieldResponse packets.
    /// </summary>
    public class TaskHostYieldPackets_Tests
    {
        /// <summary>
        /// Test the constructor for TaskHostYieldRequest with Yield.
        /// </summary>
        [Fact]
        public void TestYieldRequestConstructor_Yield()
        {
            TaskHostYieldRequest request = new TaskHostYieldRequest(42, TaskHostYieldRequestType.Yield);
            
            request.RequestId.ShouldBe(42);
            request.RequestType.ShouldBe(TaskHostYieldRequestType.Yield);
            request.Type.ShouldBe(NodePacketType.TaskHostYieldRequest);
        }

        /// <summary>
        /// Test the constructor for TaskHostYieldRequest with Reacquire.
        /// </summary>
        [Fact]
        public void TestYieldRequestConstructor_Reacquire()
        {
            TaskHostYieldRequest request = new TaskHostYieldRequest(123, TaskHostYieldRequestType.Reacquire);
            
            request.RequestId.ShouldBe(123);
            request.RequestType.ShouldBe(TaskHostYieldRequestType.Reacquire);
            request.Type.ShouldBe(NodePacketType.TaskHostYieldRequest);
        }

        /// <summary>
        /// Test serialization and deserialization of TaskHostYieldRequest.
        /// </summary>
        [Fact]
        public void TestYieldRequestTranslation()
        {
            TaskHostYieldRequest request = new TaskHostYieldRequest(456, TaskHostYieldRequestType.Yield);

            ((ITranslatable)request).Translate(TranslationHelpers.GetWriteTranslator());
            INodePacket packet = TaskHostYieldRequest.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            packet.ShouldBeOfType<TaskHostYieldRequest>();
            
            TaskHostYieldRequest deserializedRequest = (TaskHostYieldRequest)packet;
            deserializedRequest.RequestId.ShouldBe(456);
            deserializedRequest.RequestType.ShouldBe(TaskHostYieldRequestType.Yield);
        }

        /// <summary>
        /// Test the constructor for TaskHostYieldResponse.
        /// </summary>
        [Fact]
        public void TestYieldResponseConstructor()
        {
            TaskHostYieldResponse response = new TaskHostYieldResponse(42, TaskHostYieldRequestType.Yield);
            
            response.RequestId.ShouldBe(42);
            response.RequestType.ShouldBe(TaskHostYieldRequestType.Yield);
            response.Type.ShouldBe(NodePacketType.TaskHostYieldResponse);
        }

        /// <summary>
        /// Test serialization and deserialization of TaskHostYieldResponse.
        /// </summary>
        [Fact]
        public void TestYieldResponseTranslation()
        {
            TaskHostYieldResponse response = new TaskHostYieldResponse(789, TaskHostYieldRequestType.Reacquire);

            ((ITranslatable)response).Translate(TranslationHelpers.GetWriteTranslator());
            INodePacket packet = TaskHostYieldResponse.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            packet.ShouldBeOfType<TaskHostYieldResponse>();
            
            TaskHostYieldResponse deserializedResponse = (TaskHostYieldResponse)packet;
            deserializedResponse.RequestId.ShouldBe(789);
            deserializedResponse.RequestType.ShouldBe(TaskHostYieldRequestType.Reacquire);
        }

        /// <summary>
        /// Test round-trip serialization with Yield type.
        /// </summary>
        [Fact]
        public void TestRoundTripWithYieldType()
        {
            TaskHostYieldResponse response = new TaskHostYieldResponse(999, TaskHostYieldRequestType.Yield);

            ((ITranslatable)response).Translate(TranslationHelpers.GetWriteTranslator());
            TaskHostYieldResponse deserializedResponse = 
                (TaskHostYieldResponse)TaskHostYieldResponse.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            deserializedResponse.RequestId.ShouldBe(999);
            deserializedResponse.RequestType.ShouldBe(TaskHostYieldRequestType.Yield);
        }

        /// <summary>
        /// Test round-trip serialization with Reacquire type.
        /// </summary>
        [Fact]
        public void TestRoundTripWithReacquireType()
        {
            TaskHostYieldRequest request = new TaskHostYieldRequest(888, TaskHostYieldRequestType.Reacquire);

            ((ITranslatable)request).Translate(TranslationHelpers.GetWriteTranslator());
            TaskHostYieldRequest deserializedRequest = 
                (TaskHostYieldRequest)TaskHostYieldRequest.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            deserializedRequest.RequestId.ShouldBe(888);
            deserializedRequest.RequestType.ShouldBe(TaskHostYieldRequestType.Reacquire);
        }
    }
}
