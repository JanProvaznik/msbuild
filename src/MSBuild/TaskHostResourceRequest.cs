// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// Type of resource request from the TaskHost to the parent node.
    /// </summary>
    internal enum TaskHostResourceRequestType
    {
        /// <summary>
        /// Request to acquire cores.
        /// </summary>
        RequestCores,

        /// <summary>
        /// Request to release cores.
        /// </summary>
        ReleaseCores,
    }

    /// <summary>
    /// TaskHostResourceRequest represents a resource request from the TaskHost to the parent node,
    /// such as requesting or releasing CPU cores.
    /// </summary>
    internal class TaskHostResourceRequest : INodePacket
    {
        /// <summary>
        /// Unique identifier for correlating request with response.
        /// </summary>
        private int _requestId;

        /// <summary>
        /// Type of resource request.
        /// </summary>
        private TaskHostResourceRequestType _requestType;

        /// <summary>
        /// Number of cores involved in this request.
        /// </summary>
        private int _numCores;

        /// <summary>
        /// Constructor for creating a new resource request.
        /// </summary>
        /// <param name="requestId">Unique identifier for this request.</param>
        /// <param name="requestType">Type of resource request.</param>
        /// <param name="numCores">Number of cores to request or release.</param>
        public TaskHostResourceRequest(int requestId, TaskHostResourceRequestType requestType, int numCores)
        {
            _requestId = requestId;
            _requestType = requestType;
            _numCores = numCores;
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private TaskHostResourceRequest()
        {
        }

        /// <summary>
        /// Gets the request ID.
        /// </summary>
        public int RequestId => _requestId;

        /// <summary>
        /// Gets the request type.
        /// </summary>
        public TaskHostResourceRequestType RequestType => _requestType;

        /// <summary>
        /// Gets the number of cores.
        /// </summary>
        public int NumCores => _numCores;

        /// <summary>
        /// Gets the packet type.
        /// </summary>
        public NodePacketType Type => NodePacketType.TaskHostResourceRequest;

        /// <summary>
        /// Translates the packet to/from binary form.
        /// </summary>
        /// <param name="translator">The translator to use.</param>
        public void Translate(ITranslator translator)
        {
            translator.Translate(ref _requestId);
            translator.TranslateEnum(ref _requestType, (int)_requestType);
            translator.Translate(ref _numCores);
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static INodePacket FactoryForDeserialization(ITranslator translator)
        {
            TaskHostResourceRequest request = new TaskHostResourceRequest();
            request.Translate(translator);
            return request;
        }
    }
}
