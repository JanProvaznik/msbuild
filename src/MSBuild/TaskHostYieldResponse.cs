// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// TaskHostYieldResponse represents the response from the parent node to a TaskHost yield request.
    /// </summary>
    internal class TaskHostYieldResponse : INodePacket
    {
        /// <summary>
        /// Unique identifier for correlating response with request.
        /// </summary>
        private int _requestId;

        /// <summary>
        /// Type of yield request this is responding to.
        /// </summary>
        private TaskHostYieldRequestType _requestType;

        /// <summary>
        /// Constructor for creating a new yield response.
        /// </summary>
        /// <param name="requestId">Unique identifier matching the request.</param>
        /// <param name="requestType">Type of yield request being responded to.</param>
        public TaskHostYieldResponse(int requestId, TaskHostYieldRequestType requestType)
        {
            _requestId = requestId;
            _requestType = requestType;
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private TaskHostYieldResponse()
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
        public NodePacketType Type => NodePacketType.TaskHostYieldResponse;

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
            TaskHostYieldResponse response = new TaskHostYieldResponse();
            response.Translate(translator);
            return response;
        }
    }
}
