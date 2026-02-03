// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// TaskHostReacquire informs the parent node that the task host
    /// wants to reacquire control after previously yielding via Yield().
    /// </summary>
    internal class TaskHostReacquire : INodePacket
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public TaskHostReacquire()
        {
        }

        /// <summary>
        /// The type of this NodePacket
        /// </summary>
        public NodePacketType Type
        {
            get { return NodePacketType.TaskHostReacquire; }
        }

        /// <summary>
        /// Translates the packet to/from binary form.
        /// </summary>
        /// <param name="translator">The translator to use.</param>
        public void Translate(ITranslator translator)
        {
            // Do nothing -- this packet doesn't contain any parameters.
        }

        /// <summary>
        /// Factory for deserialization.
        /// </summary>
        internal static INodePacket FactoryForDeserialization(ITranslator translator)
        {
            TaskHostReacquire taskReacquire = new TaskHostReacquire();
            taskReacquire.Translate(translator);
            return taskReacquire;
        }
    }
}
