// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

namespace Microsoft.Build.BackEnd
{
    /// <summary>
    /// TaskHostYield informs the parent node that the task host is
    /// yielding control and the node can do other work while the task
    /// performs long-running out-of-process operations.
    /// </summary>
    internal class TaskHostYield : INodePacket
    {
        /// <summary>
        /// Constructor
        /// </summary>
        public TaskHostYield()
        {
        }

        /// <summary>
        /// The type of this NodePacket
        /// </summary>
        public NodePacketType Type
        {
            get { return NodePacketType.TaskHostYield; }
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
            TaskHostYield taskYield = new TaskHostYield();
            taskYield.Translate(translator);
            return taskYield;
        }
    }
}
