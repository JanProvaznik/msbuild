// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.BackEnd;

#nullable disable

namespace Microsoft.Build.CommandLine
{
    /// <summary>
    /// TaskHostResourceResponse represents the response from the parent node to a TaskHost resource request.
    /// </summary>
    internal class TaskHostResourceResponse : INodePacket
    {
        /// <summary>
        /// Unique identifier for correlating response with request.
        /// </summary>
        private int _requestId;

        /// <summary>
        /// Type of resource request this is responding to.
        /// </summary>
        private TaskHostResourceRequestType _requestType;

        /// <summary>
        /// Number of cores granted (for RequestCores) or 0 (for ReleaseCores).
        /// </summary>
        private int _numCoresGranted;

        /// <summary>
        /// Constructor for creating a new resource response.
        /// </summary>
        /// <param name="requestId">Unique identifier matching the request.</param>
        /// <param name="requestType">Type of resource request being responded to.</param>
        /// <param name="numCoresGranted">Number of cores granted.</param>
        public TaskHostResourceResponse(int requestId, TaskHostResourceRequestType requestType, int numCoresGranted)
        {
            _requestId = requestId;
            _requestType = requestType;
            _numCoresGranted = numCoresGranted;
        }

        /// <summary>
        /// Private constructor for deserialization.
        /// </summary>
        private TaskHostResourceResponse()
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
        /// Gets the number of cores granted.
        /// </summary>
        public int NumCoresGranted => _numCoresGranted;

        /// <summary>
        /// Gets the packet type.
        /// </summary>
        public NodePacketType Type => NodePacketType.TaskHostResourceResponse;

        /// <summary>
        /// Translates the packet to/from binary form.
        /// </summary>
        /// <param name="translator">The translator to use.</param>
        public void Translate(ITranslator translator)
        {
            translator.Translate(ref _requestId);
            translator.TranslateEnum(ref _requestType, (int)_requestType);
            translator.Translate(ref _numCoresGranted);
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static INodePacket FactoryForDeserialization(ITranslator translator)
        {
            TaskHostResourceResponse response = new TaskHostResourceResponse();
            response.Translate(translator);
            return response;
        }
    }
}
