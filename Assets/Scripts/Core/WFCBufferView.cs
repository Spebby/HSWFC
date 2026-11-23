using System.Runtime.CompilerServices;
using Unity.Mathematics;


// This file contains various views over the buffers allocated in WFCKernelArgs to make working with the raw
// data easier.
namespace WFC.Core {
    readonly unsafe struct Domain {
        readonly ulong* _p;
        readonly int _k;
        // ^ I'm unhappy that we have to allocate this for every struct, it's so cheap that I know it doesn't
        // actually matter, but it still bothers me. Can't make it static b/c Burst hates static mutable state

        /// <summary>
        /// Constructor for an already determined offset
        /// </summary>
        /// <param name="p"></param>
        public Domain(ulong* p, int k) {
            _p = p;
            _k = k;
        }

        /// <summary>
        /// Easy constructor for determining offset based on domains allocation and cell index.
        /// </summary>
        /// <param name="domains"></param>
        /// <param name="cell"></param>
        public Domain(ulong* domains, int cell, int k) {
            _p = domains + (long)cell * k;
            _k = k;
        }

        public bool Has(int bit) => ((_p[bit >> 6] >> (bit & 63)) & 1UL) != 0;
        public void Set(int bit) => _p[bit >> 6] |= 1UL << (bit & 63);
        public void Clear(int bit) => _p[bit >> 6] &= ~(1UL << (bit & 63));

        public void ClearAll() {
            for (int i = 0; i < _k; i++) {
                _p[i] = 0UL;
            }
        }

        public void SetOnly(int bit) {
            for (int i = 0; i < _k; i++) _p[i] = 0;
            _p[bit >> 6] = 1UL << (bit & 63);
        }

        public bool IsEmpty {
            get {
                ulong bits = 0;
                for (int i = 0; i < _k; i++) {
                    bits |= _p[i];
                }

                return bits == 0;
            }
        }

        public bool IsSingle {
            get {
                bool found = false;
                for (int i = 0; i < _k; i++) {
                    ulong word = _p[i];
                    if (word == 0) continue;
                    if (found || (word & (word - 1)) != 0) return false;
                    found = true;
                }
                return found;
            }
        }

        public int Count() {
            int count = 0;
            for (int i = 0; i < _k; i++) {
                count += math.countbits(_p[i]);
            }
            return count;
        }

        /// <summary> Index of the lowest set bit, or -1  </summary>
        /// <remarks> SIMD friendly </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int FirstSet() {
            int result = -1;
            for (int i = 0; i < _k; i++) {
                ulong word      = _p[i];
                int   candidate = (i << 6) + math.tzcnt(word);
                result = result < 0 && word != 0 ? candidate : result;
            }
            return result;
        }

        // FYI (w << 6) is equivalent to mult by 64
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FirstSetInWord(int wordIndex, ulong word) => (wordIndex << 6) + math.tzcnt(word);

        /// <summary>
        /// Enumerate set bits (for weight picking, etc)
        /// </summary>
        /// <param name="from"></param>
        /// <returns></returns>
        public int NextSet(int from) {
            if (from < 0) from = 0;
            int limb           = from >> 6;
            if (limb >= _k) return -1;

            ulong word = _p[limb] & (ulong.MaxValue << (from & 63));
            while (true) {
                if (word != 0) return FirstSetInWord(limb, word);
                if (++limb == _k) return -1;
                word = _p[limb];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Intersects(Domain mask) {
            ulong intersection = 0;
            for (int i = 0; i < _k; i++)
                intersection |= _p[i] & mask._p[i];
            return intersection != 0;
        }

        /// <summary>
        /// Equivalent to this &= mask
        /// </summary>
        /// <param name="mask"></param>
        /// <returns>True if anything changed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AndWith(Domain mask) {
            ulong diff = 0;
            for (int i = 0; i < _k; i++) {
                ulong old = _p[i];
                ulong neu = old & mask._p[i];
                diff  |= old ^ neu;
                _p[i] =  neu;
            }
            return diff != 0;
        }

        /*
         Something akin to this may be optimal for SIMD if we can know inadvanced that k == 2.
         [MethodImpl(MethodImplOptions.AggressiveInlining)]
           public bool AndWith2(Domain mask) {
               ulong old0 = _p[0], old1 = _p[1];
               ulong new0 = old0 & mask._p[0];
               ulong new1 = old1 & mask._p[1];

               _p[0] = new0;
               _p[1] = new1;

               return (old0 != new0) | (old1 != new1);
           }
         */

        /// <summary>
        /// Equivalent to this &= ~mask
        /// </summary>
        /// <param name="mask"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AndNotWith(Domain mask) {
            for (int i = 0; i < _k; i++) _p[i] &= ~mask._p[i];
        }

        /// <returns>True if the domain contains exactly this one bit</returns>
        /// <remarks>Accumulates mismatches rather than early exit for Burst friendlyness</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsOnly(int bit) {
            int   wordIndex = bit >> 6;
            ulong expected  = 1UL << (bit & 63);
            ulong diff      = 0;

            for (int i = 0; i < _k; i++) {
                diff |= _p[i] ^ (i == wordIndex ? expected : 0UL);
            }

            return diff == 0;
        }

        public ulong Word(int i) => _p[i];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetWord(int index, ulong word) => _p[index] = word;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OrWord(int index, ulong word) => _p[index] |= word;

        public void CopyFrom(Domain src) {
            for (int i = 0; i < _k; i++) {
                _p[i] = src._p[i];
            }
        }

        public static void CopyRange(ulong* dst, ulong* src, int count, int k) {
            for (int i = 0; i < (long)count * k; i++) {
                dst[i] = src[i];
            }
        }
    }

    unsafe struct PropagationQueue {
        readonly int _cellCount;
        readonly int _capacity;
        readonly int* _entries;
        readonly byte* _queuedFlags;

        int _head, _tail;

        public int Count { get; private set; }

        public PropagationQueue(int* entries, byte* queuedFlags, int cellCount) {
            _entries     = entries;
            _queuedFlags = queuedFlags;
            _cellCount   = cellCount;
            _capacity    = 2 * cellCount + 1;
            _head        = 0;
            _tail        = 0;
            Count       = 0;
        }

        public void Reset() {
            for (int i = 0; i < 2 * _cellCount; i++) _queuedFlags[i] = 0;
            _head  = 0;
            _tail  = 0;
            Count = 0;
        }

        public void Enqueue(int cell, int kind) {
            int flag = kind * _cellCount + cell;
            if (_queuedFlags[flag] != 0) return;

            _queuedFlags[flag] = 1;
            _entries[_tail]    = cell * 2 + kind;
            if (++_tail == _capacity) _tail = 0;
            Count++;
        }

        public int Dequeue() {
            int entry                       = _entries[_head];
            if (++_head == _capacity) _head = 0;
            Count--;

            int kind = entry & 1;
            int cell = entry >> 1;
            _queuedFlags[kind * _cellCount + cell] = 0;

            return entry;
        }
    }

    unsafe struct EntropyHeap {
        readonly float* _keys;
        readonly int* _buff;
        readonly int* _positions;
        int _count;

        public int Count => _count;
        public int Min => _buff[0];

        public EntropyHeap(float* keys, int* buffer, int* positions) {
            _keys = keys;
            _buff = buffer;
            _positions = positions;
            _count = 0;
        }

        public void Reset(int capacity) {
            _count = 0;
            for (int i = 0; i < capacity; i++) {
                _positions[i] = -1;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(int cell, float key) {
            _keys[cell] = key;
            int pos = _positions[cell];

            if (pos < 0) {
                pos            = _count++;
                _buff[pos]     = cell;
                _positions[cell] = pos;
                SiftUp(pos);
            } else {
                SiftUp(pos);
                SiftDown(_positions[cell]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(int cell) {
            int pos = _positions[cell];
            if (pos < 0) return;

            int lastHeapIndex = --_count;
            _positions[cell] = -1;
            if (pos == lastHeapIndex) return;

            int movedCell = _buff[lastHeapIndex];
            _buff[pos]          = movedCell;
            _positions[movedCell] = pos;

            SiftUp(pos);
            SiftDown(_positions[movedCell]);
        }

        #region Helpers
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Swap(int a, int b) {
            int ca = _buff[a], cb = _buff[b];
            _buff[a]     = cb;
            _buff[b]     = ca;
            _positions[cb] = a;
            _positions[ca] = b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SiftUp(int i) {
            while (i > 0) {
                int p = (i - 1) >> 1;
                if (_keys[_buff[i]] < _keys[_buff[p]]) {
                    Swap(i, p);
                    i = p;
                } else break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SiftDown(int heapIndex) {
            while (true) {
                int leftChild  = 2 * heapIndex + 1;
                int rightChild = leftChild + 1;
                int smallest   = heapIndex;

                if (leftChild < Count &&
                    _keys[_buff[leftChild]] < _keys[_buff[smallest]]) {
                    smallest = leftChild;
                }

                if (rightChild < Count &&
                    _keys[_buff[rightChild]] < _keys[_buff[smallest]]) {
                    smallest = rightChild;
                }

                if (smallest == heapIndex) break;

                Swap(heapIndex, smallest);
                heapIndex = smallest;
            }
        }
        #endregion
    }
}
