// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// Type of query being requested from the TaskHost to the parent node.
    /// </summary>
    internal enum TaskHostQueryType
    {
        /// <summary>
        /// Query for IsRunningMultipleNodes property.
        /// </summary>
        IsRunningMultipleNodes,
    }

    /// <summary>
    /// TaskHostQueryRequest represents a query from the TaskHost to the parent node,
    /// such as asking whether multiple nodes are running.
    /// </summary>
    internal class TaskHostQueryRequest : INodePacket
    {
        /// <summary>
        /// Unique identifier for correlating request with response.
        /// </summary>
        private int _requestId;

        /// <summary>
        /// Type of query being requested.
        /// </summary>
        private TaskHostQueryType _queryType;

        /// <summary>
        /// Constructor for creating a new query request.
        /// </summary>
        /// <param name="requestId">Unique identifier for this request.</param>
        /// <param name="queryType">Type of query being requested.</param>
        public TaskHostQueryRequest(int requestId, TaskHostQueryType queryType)
        {
            _requestId = requestId;
            _queryType = queryType;
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private TaskHostQueryRequest()
        {
        }

        /// <summary>
        /// Gets the request ID.
        /// </summary>
        public int RequestId => _requestId;

        /// <summary>
        /// Gets the query type.
        /// </summary>
        public TaskHostQueryType QueryType => _queryType;

        /// <summary>
        /// Gets the packet type.
        /// </summary>
        public NodePacketType Type => NodePacketType.TaskHostQueryRequest;

        /// <summary>
        /// Translates the packet to/from binary form.
        /// </summary>
        /// <param name="translator">The translator to use.</param>
        public void Translate(ITranslator translator)
        {
            translator.Translate(ref _requestId);
            translator.TranslateEnum(ref _queryType, (int)_queryType);
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static INodePacket FactoryForDeserialization(ITranslator translator)
        {
            TaskHostQueryRequest request = new TaskHostQueryRequest();
            request.Translate(translator);
            return request;
        }
    }
}
