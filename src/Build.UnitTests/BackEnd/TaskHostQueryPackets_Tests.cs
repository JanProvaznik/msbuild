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
    /// Unit Tests for TaskHostQueryRequest and TaskHostQueryResponse packets.
    /// </summary>
    public class TaskHostQueryPackets_Tests
    {
        /// <summary>
        /// Test the constructor for TaskHostQueryRequest.
        /// </summary>
        [Fact]
        public void TestQueryRequestConstructor()
        {
            TaskHostQueryRequest request = new TaskHostQueryRequest(42, TaskHostQueryType.IsRunningMultipleNodes);
            
            request.RequestId.ShouldBe(42);
            request.QueryType.ShouldBe(TaskHostQueryType.IsRunningMultipleNodes);
            request.Type.ShouldBe(NodePacketType.TaskHostQueryRequest);
        }

        /// <summary>
        /// Test serialization and deserialization of TaskHostQueryRequest.
        /// </summary>
        [Fact]
        public void TestQueryRequestTranslation()
        {
            TaskHostQueryRequest request = new TaskHostQueryRequest(123, TaskHostQueryType.IsRunningMultipleNodes);

            ((ITranslatable)request).Translate(TranslationHelpers.GetWriteTranslator());
            INodePacket packet = TaskHostQueryRequest.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            packet.ShouldBeOfType<TaskHostQueryRequest>();
            
            TaskHostQueryRequest deserializedRequest = (TaskHostQueryRequest)packet;
            deserializedRequest.RequestId.ShouldBe(123);
            deserializedRequest.QueryType.ShouldBe(TaskHostQueryType.IsRunningMultipleNodes);
        }

        /// <summary>
        /// Test the constructor for TaskHostQueryResponse.
        /// </summary>
        [Fact]
        public void TestQueryResponseConstructor()
        {
            TaskHostQueryResponse response = new TaskHostQueryResponse(42, TaskHostQueryType.IsRunningMultipleNodes, true);
            
            response.RequestId.ShouldBe(42);
            response.QueryType.ShouldBe(TaskHostQueryType.IsRunningMultipleNodes);
            response.BoolResult.ShouldBeTrue();
            response.Type.ShouldBe(NodePacketType.TaskHostQueryResponse);
        }

        /// <summary>
        /// Test serialization and deserialization of TaskHostQueryResponse.
        /// </summary>
        [Fact]
        public void TestQueryResponseTranslation()
        {
            TaskHostQueryResponse response = new TaskHostQueryResponse(456, TaskHostQueryType.IsRunningMultipleNodes, false);

            ((ITranslatable)response).Translate(TranslationHelpers.GetWriteTranslator());
            INodePacket packet = TaskHostQueryResponse.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            packet.ShouldBeOfType<TaskHostQueryResponse>();
            
            TaskHostQueryResponse deserializedResponse = (TaskHostQueryResponse)packet;
            deserializedResponse.RequestId.ShouldBe(456);
            deserializedResponse.QueryType.ShouldBe(TaskHostQueryType.IsRunningMultipleNodes);
            deserializedResponse.BoolResult.ShouldBeFalse();
        }

        /// <summary>
        /// Test round-trip serialization with true result.
        /// </summary>
        [Fact]
        public void TestRoundTripWithTrueResult()
        {
            TaskHostQueryResponse response = new TaskHostQueryResponse(789, TaskHostQueryType.IsRunningMultipleNodes, true);

            ((ITranslatable)response).Translate(TranslationHelpers.GetWriteTranslator());
            TaskHostQueryResponse deserializedResponse = 
                (TaskHostQueryResponse)TaskHostQueryResponse.FactoryForDeserialization(TranslationHelpers.GetReadTranslator());
            
            deserializedResponse.RequestId.ShouldBe(789);
            deserializedResponse.QueryType.ShouldBe(TaskHostQueryType.IsRunningMultipleNodes);
            deserializedResponse.BoolResult.ShouldBeTrue();
        }
    }
}
