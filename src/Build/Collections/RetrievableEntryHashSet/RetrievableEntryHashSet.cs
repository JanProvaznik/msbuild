﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Collections
{
    /// <summary>
    /// A set of <see cref="IKeyed"/> entries that behaves like a dictionary keyed on <see cref="IKeyed.Key"/>
    /// ("last add wins"), and that can be queried with a substring of a larger string without allocating it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Data-oriented layout. The table is split into three flat arrays:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>_buckets</c>: an open-addressing (linear probing) index whose length is a power of two.
    ///   Each word packs the entry index into its low <c>log2(_buckets.Length)</c> bits (index + 1, so that
    ///   0 means empty) and a fingerprint of the hash into the remaining high bits. A probe therefore
    ///   rejects almost every non-matching bucket by looking only at this one int array, without
    ///   touching entry memory.</item>
    ///   <item><c>_values</c>: the entries, densely packed in insertion order. Enumeration and
    ///   <see cref="CopyTo(T[], int, int)"/> are straight array scans with no "is this slot used" checks.</item>
    ///   <item><c>_hashes</c>: the mixed hash of each entry, parallel to <c>_values</c> (structure of arrays).
    ///   It is cold data, read only when the table is resized or an entry is removed, so it is kept out of
    ///   the arrays that lookups touch.</item>
    /// </list>
    /// <para>
    /// Removal uses backward-shift deletion (no tombstones), and moves the last entry into the freed
    /// entry slot so that <c>_values</c> stays dense. Without removals, enumeration order is insertion order.
    /// </para>
    /// <para>
    /// This replaces a fork of the .NET Framework HashSet that used prime-sized bucket arrays (integer
    /// modulo on every probe), chained slots (array of structs holding hash, value and next index), and
    /// whose lookup loop fell back to a full string comparison whenever the stored hash did not match.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of the entries.</typeparam>
    [DebuggerTypeProxy(typeof(Microsoft.Build.Collections.HashSetDebugView<>))]
    [DebuggerDisplay("Count = {Count}")]
    [SuppressMessage("Microsoft.Naming", "CA1710:IdentifiersShouldHaveCorrectSuffix", Justification = "By design")]
    [Serializable()]
#if FEATURE_SECURITY_PERMISSIONS
    [System.Security.Permissions.HostProtection(MayLeakOnAbort = true)]
#endif
    internal class RetrievableEntryHashSet<T> : IRetrievableEntryHashSet<T> // CodeQL [SM02227] The dangerous method is called only in debug build. It's safe for release build.
        where T : class, IKeyed
    {
        private const String CapacityName = "Capacity";
        private const String ElementsName = "Elements";
        private const String ComparerName = "Comparer";
        private const String VersionName = "Version";

        /// <summary>
        /// 2^32 / golden ratio. Multiplying by it spreads the comparer's hash over all 32 bits
        /// (Fibonacci hashing), so the top bits can pick the bucket and the low bits can serve as the fingerprint.
        /// </summary>
        private const uint GoldenRatio = 0x9E3779B9u;

        /// <summary>
        /// Smallest bucket array. Must be a power of two.
        /// </summary>
        private const int MinBucketCount = 8;

        /// <summary>
        /// Packed index: low <see cref="_indexBits"/> bits are entry index + 1 (0 = empty), the rest is a fingerprint.
        /// </summary>
        private int[] _buckets;

        /// <summary>
        /// Entries, dense, in insertion order (until a removal moves the last one into the hole).
        /// </summary>
        private T[] _values;

        /// <summary>
        /// Mixed hash of each entry, parallel to <see cref="_values"/>. Cold: only used for resize and removal.
        /// </summary>
        private uint[] _hashes;

        private int _count;

        /// <summary>
        /// log2(_buckets.Length). Number of low bits of a bucket word that hold the entry index.
        /// </summary>
        private int _indexBits;

        private IEqualityComparer<string> _comparer;
        private IConstrainedEqualityComparer<string> _constrainedComparer;
        private int _version;
        private bool _readOnly;

        // temporary variable needed during deserialization
        private SerializationInfo _siInfo;

        #region Constructors

        public RetrievableEntryHashSet(IEqualityComparer<string> comparer)
        {
            Assumed.NotNull(comparer, "use explicit comparer");

            _comparer = comparer;
            _constrainedComparer = comparer as IConstrainedEqualityComparer<string>;
        }

        public RetrievableEntryHashSet(IEnumerable<T> collection, IEqualityComparer<string> comparer, bool readOnly = false)
            : this(collection, comparer)
        {
            _readOnly = true; // Set after possible initialization from another collection
        }

        public RetrievableEntryHashSet(IEnumerable<KeyValuePair<string, T>> collection, IEqualityComparer<string> comparer, bool readOnly = false)
            : this(collection.Values(), comparer, readOnly)
        {
            _readOnly = true; // Set after possible initialization from another collection
        }

        /// <summary>
        /// Creates a set sized so that <paramref name="suggestedCapacity"/> entries fit without a resize.
        /// </summary>
        public RetrievableEntryHashSet(int suggestedCapacity, IEqualityComparer<string> comparer)
            : this(comparer)
        {
            Initialize(suggestedCapacity);
        }

        /// <summary>
        /// Creates a set containing the entries of <paramref name="collection"/>; later entries win over earlier ones with an equal key.
        /// </summary>
        public RetrievableEntryHashSet(IEnumerable<T> collection, IEqualityComparer<string> comparer)
            : this(comparer)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            if (collection is RetrievableEntryHashSet<T> other && ReferenceEquals(other._comparer, comparer))
            {
                CopyFrom(other);
                return;
            }

            Initialize(collection is ICollection<T> coll ? coll.Count : 0);
            UnionWith(collection);
        }

        /// <summary>
        /// Creates a copy of <paramref name="other"/> with the same comparer and entries (not a deep copy).
        /// </summary>
        protected RetrievableEntryHashSet(RetrievableEntryHashSet<T> other)
            : this(other._comparer)
        {
            CopyFrom(other);
        }

        /// <summary>
        /// Same comparer means same hashes and no duplicates, so copying is three flat array copies
        /// instead of rehashing and reinserting every key.
        /// </summary>
        private void CopyFrom(RetrievableEntryHashSet<T> other)
        {
            if (other._count == 0)
            {
                return;
            }

            _buckets = (int[])other._buckets.Clone();
            _values = (T[])other._values.Clone();
            _hashes = (uint[])other._hashes.Clone();
            _indexBits = other._indexBits;
            _count = other._count;
        }

        /// <summary>
        /// The comparer used for keys.
        /// </summary>
        internal IEqualityComparer<string> Comparer => _comparer;

        protected RetrievableEntryHashSet(SerializationInfo info, StreamingContext context)
        {
            // We can't do anything with the keys and values until the entire graph has been
            // deserialized and we have a reasonable estimate that GetHashCode is not going to
            // fail.  For the time being, we'll just cache this.  The graph is not valid until
            // OnDeserialization has been called.
            _siInfo = info;
        }

        #endregion

        // Convenience to minimise change to callers used to dictionaries
        public ICollection<string> Keys
        {
            get
            {
                var keys = new string[_count];
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i] = _values[i].Key;
                }

                return keys;
            }
        }

        // Convenience to minimise change to callers used to dictionaries
        public ICollection<T> Values => this;

        #region ICollection<T> methods

        // Convenience to minimise change to callers used to dictionaries
        internal T this[string name]
        {
            get => Get(name);

            set
            {
                Debug.Assert(String.Equals(name, value.Key, StringComparison.Ordinal));
                Add(value);
            }
        }

        void ICollection<T>.Add(T item) => AddEvenIfPresent(item);

        /// <summary>
        /// Remove all items from this set. This clears the elements but keeps the arrays.
        /// Follow this call by TrimExcess to release these.
        /// </summary>
        public void Clear()
        {
            if (_readOnly)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_NotSupportedReadOnlyCollection");
            }

            if (_count > 0)
            {
                Array.Clear(_values, 0, _count);
                Array.Clear(_buckets, 0, _buckets.Length);
                _count = 0;
            }

            _version++;
        }

        // Convenience
        internal bool Contains(string key) => Get(key) != null;

        bool ICollection<KeyValuePair<string, T>>.Contains(KeyValuePair<string, T> entry)
        {
            Debug.Assert(String.Equals(entry.Key, entry.Value.Key, StringComparison.Ordinal));
            return Get(entry.Value.Key) != null;
        }

        public bool ContainsKey(string key) => Get(key) != null;

        T IDictionary<string, T>.this[string name]
        {
            get => Get(name);
            set => Add(value);
        }

        /// <summary>
        /// Checks if this hashset contains an item with the same key.
        /// </summary>
        public bool Contains(T item) => Get(item.Key) != null;

        // Convenience to minimise change to callers used to dictionaries
        public bool TryGetValue(string key, out T item)
        {
            item = Get(key);
            return item != null;
        }

        /// <summary>
        /// Gets the item if any with the given name
        /// </summary>
        /// <param name="key">key to check for containment</param>
        /// <returns>The item, if it was found. Otherwise, default(T).</returns>
        public T Get(string key)
        {
            int i = FindEntry(key, 0, key?.Length ?? 0, out _);
            return i >= 0 ? _values[i] : null;
        }

        /// <summary>
        /// Gets the item if any with the given name
        /// </summary>
        /// <param name="key">key to check for containment</param>
        /// <param name="index">The position of the substring within <paramref name="key"/>.</param>
        /// <param name="length">The maximum number of characters in the <paramref name="key"/> to lookup.</param>
        /// <returns>The item, if it was found. Otherwise, default(T).</returns>
        public T Get(string key, int index, int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            if (index < 0 || index > (key == null ? 0 : key.Length) - length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (_constrainedComparer == null)
            {
                throw new InvalidOperationException("Cannot do a constrained lookup on this collection.");
            }

            int i = FindEntry(key, index, length, out _);
            return i >= 0 ? _values[i] : null;
        }

        /// <summary>
        /// Copy items in this hashset to array, starting at arrayIndex
        /// </summary>
        public void CopyTo(T[] array, int arrayIndex) => CopyTo(array, arrayIndex, _count);

        /// <summary>
        /// Remove entry that compares equal to T
        /// </summary>
        public bool Remove(T item) => Remove(item.Key);

        bool ICollection<KeyValuePair<string, T>>.Remove(KeyValuePair<string, T> entry)
        {
            Debug.Assert(String.Equals(entry.Key, entry.Value.Key, StringComparison.Ordinal));
            return Remove(entry.Value);
        }

        /// <summary>
        /// Remove item from this hashset
        /// </summary>
        /// <param name="item">item to remove</param>
        /// <returns>true if removed; false if not (i.e. if the item wasn't in the HashSet)</returns>
        public bool Remove(string item)
        {
            if (_readOnly)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_NotSupportedReadOnlyCollection");
            }

            int entry = FindEntryWholeKey(item, out int bucket, out _);
            if (entry < 0)
            {
                return false;
            }

            DeleteBucket(bucket);

            // Keep _values dense: move the last entry into the hole and repoint its bucket.
            int last = _count - 1;
            if (entry != last)
            {
                int lastBucket = FindBucketOfEntry(last);
                _values[entry] = _values[last];
                _hashes[entry] = _hashes[last];
                uint word = (uint)_buckets[lastBucket];
                _buckets[lastBucket] = (int)((word & ~IndexMask) | (uint)(entry + 1));
            }

            _values[last] = null;
            _count = last;
            _version++;
            return true;
        }

        /// <summary>
        /// Number of elements in this hashset
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Whether this is readonly
        /// </summary>
        public bool IsReadOnly => _readOnly;

        /// <summary>
        /// Permanently prevent changes to the set.
        /// </summary>
        internal void MakeReadOnly() => _readOnly = true;

        #endregion

        #region IEnumerable methods

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<KeyValuePair<string, T>> IEnumerable<KeyValuePair<string, T>>.GetEnumerator()
        {
            foreach (var entry in this)
            {
                yield return new KeyValuePair<string, T>(entry.Key, entry);
            }
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        #endregion

        #region ISerializable methods

        // [SecurityPermissionAttribute(SecurityAction.LinkDemand, Flags = SecurityPermissionFlag.SerializationFormatter)]
        [SecurityCritical]
        public virtual void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }

            // need to serialize version to avoid problems with serializing while enumerating
            info.AddValue(VersionName, _version);
            info.AddValue(ComparerName, _comparer, typeof(IEqualityComparer<string>));
            info.AddValue(CapacityName, _values == null ? 0 : _values.Length);
            if (_values != null)
            {
                info.AddValue(ElementsName, ToArray(), typeof(T[]));
            }
        }

        #endregion

        #region IDeserializationCallback methods

        public virtual void OnDeserialization(Object sender)
        {
            if (_siInfo == null)
            {
                // It might be necessary to call OnDeserialization from a container if the
                // container object also implements OnDeserialization. However, remoting will
                // call OnDeserialization again. We can return immediately if this function is
                // called twice. Note we set m_siInfo to null at the end of this method.
                return;
            }

            int capacity = _siInfo.GetInt32(CapacityName);
            _comparer = (IEqualityComparer<string>)_siInfo.GetValue(ComparerName, typeof(IEqualityComparer<string>));
            _constrainedComparer = _comparer as IConstrainedEqualityComparer<string>;

            if (capacity != 0)
            {
                T[] array = (T[])_siInfo.GetValue(ElementsName, typeof(T[]));

                if (array == null)
                {
                    throw new SerializationException();
                }

                Initialize(Math.Max(capacity, array.Length));
                for (int i = 0; i < array.Length; i++)
                {
                    AddEvenIfPresent(array[i]);
                }
            }

            _version = _siInfo.GetInt32(VersionName);
            _siInfo = null;
        }

        #endregion

        #region HashSet methods

        /// <summary>
        /// Add item to this HashSet.
        /// *** MSBUILD NOTE: Always added - overwrite semantics
        /// </summary>
        public void Add(T item) => AddEvenIfPresent(item);

        void IDictionary<string, T>.Add(string key, T item)
        {
            if (key != item.Key)
            {
                throw new InvalidOperationException();
            }

            AddEvenIfPresent(item);
        }

        void ICollection<KeyValuePair<string, T>>.Add(KeyValuePair<string, T> entry)
        {
            Debug.Assert(String.Equals(entry.Key, entry.Value.Key, StringComparison.Ordinal));

            AddEvenIfPresent(entry.Value);
        }

        /// <summary>
        /// Take the union of this HashSet with other. Modifies this set.
        /// </summary>
        /// <param name="other">enumerable with items to add</param>
        public void UnionWith(IEnumerable<T> other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            if (other is ICollection<T> coll)
            {
                EnsureCapacity(_count + coll.Count);
            }

            foreach (T item in other)
            {
                AddEvenIfPresent(item);
            }
        }

        // Copy all elements into array starting at zero based index specified
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "Decently informative for an exception that will probably never actually see the light of day")]
        void ICollection<KeyValuePair<string, T>>.CopyTo(KeyValuePair<string, T>[] array, int index)
        {
            if (index < 0 || Count > array.Length - index)
            {
                throw new ArgumentException("index");
            }

            for (int i = 0; i < _count; i++)
            {
                T entry = _values[i];
                array[index + i] = new KeyValuePair<string, T>(entry.Key, entry);
            }
        }

        public void CopyTo(T[] array) => CopyTo(array, 0, _count);

        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "Decently informative for an exception that will probably never actually see the light of day")]
        public void CopyTo(T[] array, int arrayIndex, int count)
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            // check array index valid index into array
            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }

            // also throw if count less than 0
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            // will array, starting at arrayIndex, be able to hold elements? Note: not
            // checking arrayIndex >= array.Length (consistency with list of allowing
            // count of 0; subsequent check takes care of the rest)
            if (arrayIndex > array.Length || count > array.Length - arrayIndex)
            {
                throw new ArgumentException("arrayIndex");
            }

            int toCopy = Math.Min(count, _count);
            if (toCopy > 0)
            {
                Array.Copy(_values, 0, array, arrayIndex, toCopy);
            }
        }

        /// <summary>
        /// Shrinks the arrays to the smallest size that holds the current entries,
        /// unless count is 0, in which case we release them.
        /// </summary>
        public void TrimExcess()
        {
            Debug.Assert(_count >= 0, "_count is negative");

            if (_count == 0)
            {
                _buckets = null;
                _values = null;
                _hashes = null;
                _indexBits = 0;
                _version++;
            }
            else
            {
                int bucketCount = BucketCountFor(_count);
                if (bucketCount < _buckets.Length)
                {
                    Resize(bucketCount);
                }
            }
        }

        #endregion

        #region Helper methods

        /// <summary>
        /// Mask selecting the entry index (+1) part of a bucket word.
        /// </summary>
        private uint IndexMask
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (1u << _indexBits) - 1;
        }

        /// <summary>
        /// Maximum number of entries for a bucket array of <paramref name="bucketCount"/> words.
        /// Keeps the load factor at or below 3/4, which also keeps entry index + 1 strictly below 2^indexBits.
        /// </summary>
        private static int MaxEntriesFor(int bucketCount) => bucketCount - (bucketCount >> 2);

        /// <summary>
        /// Smallest power-of-two bucket count that holds <paramref name="entries"/> entries.
        /// </summary>
        private static int BucketCountFor(int entries)
        {
            int bucketCount = MinBucketCount;
            while (MaxEntriesFor(bucketCount) < entries)
            {
                bucketCount <<= 1;
            }

            return bucketCount;
        }

        private void Initialize(int capacity)
        {
            Debug.Assert(_buckets == null, "Initialize was called but _buckets was non-null");
            Allocate(BucketCountFor(capacity));
        }

        private void Allocate(int bucketCount)
        {
            int capacity = MaxEntriesFor(bucketCount);
            _buckets = new int[bucketCount];
            _values = new T[capacity];
            _hashes = new uint[capacity];
            _indexBits = Log2(bucketCount);
        }

        private static int Log2(int powerOfTwo)
        {
            int bits = 0;
            while ((1 << bits) < powerOfTwo)
            {
                bits++;
            }

            return bits;
        }

        private void EnsureCapacity(int entries)
        {
            if (_buckets == null)
            {
                Initialize(entries);
            }
            else if (entries > _values.Length)
            {
                Resize(BucketCountFor(entries));
            }
        }

        /// <summary>
        /// Rebuild the bucket index for a new size. Entries keep their positions, and hashes are
        /// read from the cold hash array, so no key is rehashed.
        /// </summary>
        private void Resize(int bucketCount)
        {
            Debug.Assert(MaxEntriesFor(bucketCount) >= _count, "Resize would drop entries");

            T[] oldValues = _values;
            uint[] oldHashes = _hashes;
            Allocate(bucketCount);
            Array.Copy(oldValues, _values, _count);
            Array.Copy(oldHashes, _hashes, _count);

            int[] buckets = _buckets;
            int shift = 32 - _indexBits;
            int mask = buckets.Length - 1;
            for (int i = 0; i < _count; i++)
            {
                uint mixed = _hashes[i];
                int pos = (int)(mixed >> shift);
                while (buckets[pos] != 0)
                {
                    pos = (pos + 1) & mask;
                }

                buckets[pos] = (int)((mixed << _indexBits) | (uint)(i + 1));
            }
        }

        /// <summary>
        /// Adds value to HashSet if not contained already
        /// Returns true if added and false if already present
        /// ** MSBUILD: Modified so that it DOES add even if present. It will return false in that case, though.**
        /// </summary>
        private bool AddEvenIfPresent(T value)
        {
            if (_readOnly)
            {
                ErrorUtilities.ThrowInvalidOperation("OM_NotSupportedReadOnlyCollection");
            }

            if (_buckets == null)
            {
                Initialize(0);
            }

            string key = value.Key;
            int existing = FindEntryWholeKey(key, out int bucket, out uint mixed);
            if (existing >= 0)
            {
                // NOTE: this must add EVEN IF it is already present,
                // as it may be a different object with the same name,
                // and we want "last wins" semantics
                _values[existing] = value;
                return false;
            }

            if (_count == _values.Length)
            {
                Resize(_buckets.Length << 1);
                bucket = (int)(mixed >> (32 - _indexBits));
                int mask = _buckets.Length - 1;
                while (_buckets[bucket] != 0)
                {
                    bucket = (bucket + 1) & mask;
                }
            }

            int index = _count;
            _values[index] = value;
            _hashes[index] = mixed;
            _buckets[bucket] = (int)((mixed << _indexBits) | (uint)(index + 1));
            _count = index + 1;
            _version++;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Mix(int hash) => unchecked((uint)hash * GoldenRatio);

        private int WholeKeyHash(string key)
        {
            // Workaround Comparers that throw ArgumentNullException for GetHashCode(null).
            return key == null ? 0 : _comparer.GetHashCode(key);
        }

        /// <summary>
        /// Lookup used by Add and Remove: compares whole keys with the main comparer.
        /// Returns the entry index, or -1. <paramref name="bucket"/> is the bucket holding the entry,
        /// or, if not found, the empty bucket where it would be inserted. <paramref name="mixed"/> is the key's mixed hash.
        /// </summary>
        private int FindEntryWholeKey(string key, out int bucket, out uint mixed)
        {
            mixed = Mix(WholeKeyHash(key));
            if (_buckets == null)
            {
                bucket = -1;
                return -1;
            }

            int[] buckets = _buckets;
            T[] values = _values;
            int indexBits = _indexBits;
            uint indexMask = (1u << indexBits) - 1;
            uint fingerprint = mixed << indexBits;
            int mask = buckets.Length - 1;
            int pos = (int)(mixed >> (32 - indexBits));

            while (true)
            {
                uint word = (uint)buckets[pos];
                if (word == 0)
                {
                    bucket = pos;
                    return -1;
                }

                if ((word & ~indexMask) == fingerprint)
                {
                    int i = (int)(word & indexMask) - 1;
                    if (_comparer.Equals(values[i].Key, key))
                    {
                        bucket = pos;
                        return i;
                    }
                }

                pos = (pos + 1) & mask;
            }
        }

        /// <summary>
        /// Lookup used by Get: uses the constrained comparer when there is one, so that
        /// a substring of <paramref name="key"/> can be looked up without allocating it.
        /// </summary>
        private int FindEntry(string key, int index, int length, out int bucket)
        {
            if (_buckets == null)
            {
                bucket = -1;
                return -1;
            }

            IConstrainedEqualityComparer<string> constrained = _constrainedComparer;
            if (constrained == null)
            {
                return FindEntryWholeKey(key, out bucket, out _);
            }

            uint mixed = Mix(constrained.GetHashCode(key, index, length));
            int[] buckets = _buckets;
            T[] values = _values;
            int indexBits = _indexBits;
            uint indexMask = (1u << indexBits) - 1;
            uint fingerprint = mixed << indexBits;
            int mask = buckets.Length - 1;
            int pos = (int)(mixed >> (32 - indexBits));

            while (true)
            {
                uint word = (uint)buckets[pos];
                if (word == 0)
                {
                    bucket = pos;
                    return -1;
                }

                if ((word & ~indexMask) == fingerprint)
                {
                    int i = (int)(word & indexMask) - 1;
                    if (constrained.Equals(values[i].Key, key, index, length))
                    {
                        bucket = pos;
                        return i;
                    }
                }

                pos = (pos + 1) & mask;
            }
        }

        /// <summary>
        /// Finds the bucket that points at entry <paramref name="entry"/>, by probing from its home position.
        /// </summary>
        private int FindBucketOfEntry(int entry)
        {
            uint mixed = _hashes[entry];
            uint target = (uint)(entry + 1);
            uint indexMask = IndexMask;
            int mask = _buckets.Length - 1;
            int pos = (int)(mixed >> (32 - _indexBits));
            while (((uint)_buckets[pos] & indexMask) != target)
            {
                Debug.Assert(_buckets[pos] != 0, "entry is not in the bucket index");
                pos = (pos + 1) & mask;
            }

            return pos;
        }

        /// <summary>
        /// Empties a bucket using backward-shift deletion, so probe sequences never need tombstones.
        /// </summary>
        private void DeleteBucket(int hole)
        {
            int[] buckets = _buckets;
            int mask = buckets.Length - 1;
            int shift = 32 - _indexBits;
            uint indexMask = IndexMask;
            int pos = hole;

            while (true)
            {
                pos = (pos + 1) & mask;
                uint word = (uint)buckets[pos];
                if (word == 0)
                {
                    break;
                }

                int home = (int)(_hashes[(int)(word & indexMask) - 1] >> shift);

                // The entry at pos can move into the hole if its home is not cyclically in (hole, pos].
                if (((pos - home) & mask) >= ((pos - hole) & mask))
                {
                    buckets[hole] = (int)word;
                    hole = pos;
                }
            }

            buckets[hole] = 0;
        }

        /// <summary>
        /// Equality comparer against another of this type.
        /// Compares entries by reference - not merely by using the comparer on the key
        /// </summary>
        internal bool EntriesAreReferenceEquals(RetrievableEntryHashSet<T> other)
        {
            if (Object.ReferenceEquals(this, other))
            {
                return true;
            }

            if (this.Count != other.Count)
            {
                return false;
            }

            for (int i = 0; i < other._count; i++)
            {
                T element = other._values[i];
                if (!TryGetValue(element.Key, out T ours) || !Object.ReferenceEquals(element, ours))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Copies this to an array. Used for DebugView
        /// </summary>
        internal T[] ToArray()
        {
            T[] newArray = new T[Count];
            CopyTo(newArray);
            return newArray;
        }

        #endregion

#if !SILVERLIGHT
        [Serializable()]
#if FEATURE_SECURITY_PERMISSIONS
        [System.Security.Permissions.HostProtection(MayLeakOnAbort = true)]
#endif
#endif
        public struct Enumerator : IEnumerator<T>, System.Collections.IEnumerator
        {
            private readonly RetrievableEntryHashSet<T> _set;
            private readonly int _version;
            private int _index;
            private T _current;

            internal Enumerator(RetrievableEntryHashSet<T> set)
            {
                _set = set;
                _index = 0;
                _version = set._version;
                _current = default(T);
            }

            public void Dispose()
            {
            }

            public bool MoveNext()
            {
                if (_version != _set._version)
                {
                    throw new InvalidOperationException();
                }

                if (_index < _set._count)
                {
                    _current = _set._values[_index];
                    _index++;
                    return true;
                }

                _index = _set._count + 1;
                _current = default(T);
                return false;
            }

            public T Current => _current;

            Object System.Collections.IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _set._count + 1)
                    {
                        throw new InvalidOperationException();
                    }

                    return Current;
                }
            }

            void System.Collections.IEnumerator.Reset()
            {
                if (_version != _set._version)
                {
                    throw new InvalidOperationException();
                }

                _index = 0;
                _current = default(T);
            }
        }
    }
}
