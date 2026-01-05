// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// Type of yield request from the TaskHost to the parent node.
    /// </summary>
    internal enum TaskHostYieldRequestType
    {
        /// <summary>
        /// Request to yield execution.
        /// </summary>
        Yield,

        /// <summary>
        /// Request to reacquire after yielding.
        /// </summary>
        Reacquire,
    }

    /// <summary>
    /// TaskHostYieldRequest represents a yield/reacquire request from the TaskHost to the parent node.
    /// </summary>
    internal class TaskHostYieldRequest : INodePacket
    {
        /// <summary>
        /// Unique identifier for correlating request with response.
        /// </summary>
        private int _requestId;

        /// <summary>
        /// Type of yield request.
        /// </summary>
        private TaskHostYieldRequestType _requestType;

        /// <summary>
        /// Constructor for creating a new yield request.
        /// </summary>
        /// <param name="requestId">Unique identifier for this request.</param>
        /// <param name="requestType">Type of yield request.</param>
        public TaskHostYieldRequest(int requestId, TaskHostYieldRequestType requestType)
        {
            _requestId = requestId;
            _requestType = requestType;
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private TaskHostYieldRequest()
        {
        }

        /// <summary>
        /// Gets the request ID.
        /// </summary>
        public int RequestId => _requestId;

        /// <summary>
        /// Gets the request type.
        /// </summary>
        public TaskHostYieldRequestType RequestType => _requestType;

        /// <summary>
        /// Gets the packet type.
        /// </summary>
        public NodePacketType Type => NodePacketType.TaskHostYieldRequest;

        /// <summary>
        /// Translates the packet to/from binary form.
        /// </summary>
        /// <param name="translator">The translator to use.</param>
        public void Translate(ITranslator translator)
        {
            translator.Translate(ref _requestId);
            translator.TranslateEnum(ref _requestType, (int)_requestType);
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static INodePacket FactoryForDeserialization(ITranslator translator)
        {
            TaskHostYieldRequest request = new TaskHostYieldRequest();
            request.Translate(translator);
            return request;
        }
    }
}
