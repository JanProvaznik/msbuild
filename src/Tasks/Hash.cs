// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO.Hashing;
using System.Text;
using Microsoft.Build.Framework;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Generates a hash of a given ItemGroup items. Metadata is not considered in the hash.
    /// </summary>
    /// <remarks>
    /// Currently uses XxHash64. Implementation subject to change between MSBuild versions.
    /// This class is not intended as a cryptographic security measure, only uniqueness between build executions
    /// - collisions can theoretically be possible and should be handled gracefully by the caller.
    ///
    /// Usage of non-cryptographic hash brings better performance compared to cryptographic secure hash like SHA256.
    /// </remarks>
    public class Hash : TaskExtension
    {
        private const char ItemSeparatorCharacter = '\u2028';
        private static readonly Encoding s_encoding = Encoding.UTF8;
        private static readonly byte[] s_itemSeparatorCharacterBytes = s_encoding.GetBytes([ItemSeparatorCharacter]);

        // Size of buffer where bytes of the strings are stored until hash update is to be run on them.
        // It is needed to get a balance between amount of calls and amount of allocated memory.
        private const int HashBufferSize = 512;

        // Size of chunks in which ItemSpecs would be cut.
        // We have chosen this length so itemSpecChunkByteBuffer rented from ArrayPool will be close but not bigger than 512.
        private const int MaxInputChunkLength = 169;

        /// <summary>
        /// Items from which to generate a hash.
        /// </summary>
        [Required]
        public ITaskItem[] ItemsToHash { get; set; }

        /// <summary>
        /// When true, will generate a case-insensitive hash.
        /// </summary>
        public bool IgnoreCase { get; set; }

        /// <summary>
        /// Hash of the ItemsToHash ItemSpec.
        /// </summary>
        [Output]
        public string HashResult { get; set; }

        /// <summary>
        /// Execute the task.
        /// </summary>
        public override bool Execute()
        {
            if (ItemsToHash?.Length > 0)
            {
                var xxHash = new XxHash64();

                // Buffer in which bytes of the strings are to be stored until their number reaches the limit size.
                // Once the limit is reached, the hash.Append is to be run on all the bytes of this buffer.
                byte[] hashBuffer = null;

                // Buffer in which bytes of items' ItemSpec are to be stored.
                byte[] itemSpecChunkByteBuffer = null;

                try
                {
                    hashBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(HashBufferSize);
                    itemSpecChunkByteBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(s_encoding.GetMaxByteCount(MaxInputChunkLength));

                    int hashBufferPosition = 0;
                    for (int i = 0; i < ItemsToHash.Length; i++)
                    {
                        string itemSpec = IgnoreCase ? ItemsToHash[i].ItemSpec.ToUpperInvariant() : ItemsToHash[i].ItemSpec;

                        // Slice the itemSpec string into chunks of reasonable size and add them to hash buffer.
                        for (int itemSpecPosition = 0; itemSpecPosition < itemSpec.Length; itemSpecPosition += MaxInputChunkLength)
                        {
                            int charsToProcess = Math.Min(itemSpec.Length - itemSpecPosition, MaxInputChunkLength);
                            int byteCount = s_encoding.GetBytes(itemSpec, itemSpecPosition, charsToProcess, itemSpecChunkByteBuffer, 0);

                            hashBufferPosition = AddBytesToHashBuffer(xxHash, hashBuffer, hashBufferPosition, HashBufferSize, itemSpecChunkByteBuffer, byteCount);
                        }

                        hashBufferPosition = AddBytesToHashBuffer(xxHash, hashBuffer, hashBufferPosition, HashBufferSize, s_itemSeparatorCharacterBytes, s_itemSeparatorCharacterBytes.Length);
                    }

                    // Process any remaining bytes
                    if (hashBufferPosition > 0)
                    {
                        xxHash.Append(hashBuffer.AsSpan(0, hashBufferPosition));
                    }

                    byte[] hashResult = BitConverter.GetBytes(xxHash.GetCurrentHashAsUInt64());
                    
#if NET
                    HashResult = Convert.ToHexStringLower(hashResult);
#else
                    using (var stringBuilder = new ReuseableStringBuilder(hashResult.Length * 2))
                    {
                        foreach (var b in hashResult)
                        {
                            stringBuilder.Append(b.ToString("x2"));
                        }
                        HashResult = stringBuilder.ToString();
                    }
#endif
                }
                finally
                {
                    if (hashBuffer != null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(hashBuffer);
                    }
                    if (itemSpecChunkByteBuffer != null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(itemSpecChunkByteBuffer);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Add bytes to the hash buffer. Once the limit size is reached, hash.Append is called and the buffer is flushed.
        /// </summary>
        /// <param name="hash">XxHash64 hashing algorithm instance.</param>
        /// <param name="hashBuffer">The hash buffer which stores bytes of the strings. Bytes should be added to this buffer.</param>
        /// <param name="hashBufferPosition">Number of used bytes of the hash buffer.</param>
        /// <param name="hashBufferSize">The size of hash buffer.</param>
        /// <param name="byteBuffer">Bytes buffer which contains bytes to be written to hash buffer.</param>
        /// <param name="byteCount">Amount of bytes that are to be added to hash buffer.</param>
        /// <returns>Updated hashBufferPosition.</returns>
        private int AddBytesToHashBuffer(XxHash64 hash, byte[] hashBuffer, int hashBufferPosition, int hashBufferSize, byte[] byteBuffer, int byteCount)
        {
            int bytesProcessed = 0;
            while (hashBufferPosition + byteCount >= hashBufferSize)
            {
                int hashBufferFreeSpace = hashBufferSize - hashBufferPosition;

                if (hashBufferPosition == 0)
                {
                    // If hash buffer is empty and bytes number is big enough there is no need to copy bytes to hash buffer.
                    // Pass the bytes to Append right away.
                    hash.Append(byteBuffer.AsSpan(bytesProcessed, hashBufferSize));
                }
                else
                {
                    Array.Copy(byteBuffer, bytesProcessed, hashBuffer, hashBufferPosition, hashBufferFreeSpace);
                    hash.Append(hashBuffer.AsSpan(0, hashBufferSize));
                    hashBufferPosition = 0;
                }

                bytesProcessed += hashBufferFreeSpace;
                byteCount -= hashBufferFreeSpace;
            }

            Array.Copy(byteBuffer, bytesProcessed, hashBuffer, hashBufferPosition, byteCount);
            hashBufferPosition += byteCount;

            return hashBufferPosition;
        }
    }
}
