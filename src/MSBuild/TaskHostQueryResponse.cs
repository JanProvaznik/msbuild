// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// TaskHostQueryResponse represents the response from the parent node to a TaskHost query.
    /// </summary>
    internal class TaskHostQueryResponse : INodePacket
    {
        /// <summary>
        /// Unique identifier for correlating response with request.
        /// </summary>
        private int _requestId;

        /// <summary>
        /// Type of query this is responding to.
        /// </summary>
        private TaskHostQueryType _queryType;

        /// <summary>
        /// Boolean result for queries that return bool.
        /// </summary>
        private bool _boolResult;

        /// <summary>
        /// Constructor for creating a new query response.
        /// </summary>
        /// <param name="requestId">Unique identifier matching the request.</param>
        /// <param name="queryType">Type of query being responded to.</param>
        /// <param name="boolResult">Boolean result value.</param>
        public TaskHostQueryResponse(int requestId, TaskHostQueryType queryType, bool boolResult)
        {
            _requestId = requestId;
            _queryType = queryType;
            _boolResult = boolResult;
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private TaskHostQueryResponse()
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
        /// Gets the boolean result.
        /// </summary>
        public bool BoolResult => _boolResult;

        /// <summary>
        /// Gets the packet type.
        /// </summary>
        public NodePacketType Type => NodePacketType.TaskHostQueryResponse;

        /// <summary>
        /// Translates the packet to/from binary form.
        /// </summary>
        /// <param name="translator">The translator to use.</param>
        public void Translate(ITranslator translator)
        {
            translator.Translate(ref _requestId);
            translator.TranslateEnum(ref _queryType, (int)_queryType);
            translator.Translate(ref _boolResult);
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static INodePacket FactoryForDeserialization(ITranslator translator)
        {
            TaskHostQueryResponse response = new TaskHostQueryResponse();
            response.Translate(translator);
            return response;
        }
    }
}
