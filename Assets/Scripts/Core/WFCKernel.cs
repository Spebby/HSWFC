using System.Runtime.CompilerServices;
using Unity.Mathematics;
using static WFC.Core.WFCSolver;


namespace WFC.Core {
    /// <summary>
    /// Holds the kernel's execution state, including working domains, propagation
    /// queues, entropy heap, and random state.
    /// </summary>
    public enum WFCStatus {
        Success = 0,
        Failed = 1,
        Unsatisfiable = 2
    }

    /// <summary>Everything the kernel reads and writes. Plain blittable data; the solver fills it from pinned arrays.</summary>
    public unsafe struct WFCKernelArgs {
        public int width, height, tileCount, windowCount, maxAttempts;
        public ulong seed;
        public byte hasPerCellWeights; // 1 = cellWeights is [width*height*count], 0 = use weight[count]
        public float* weights;
        public float* cellWeights;
        public ulong* supportMasks;        // [8 * count]
        public ulong* windowTargets;       // [windowCount]
        public byte* windowMarker;         // [windowCount]
        public ulong* windowFeasibleMasks; // [windowCount * WFCRules.FeasibleWords]
        public ulong* initialDomains;      // [width*height] starting domains
        public byte* result;               // [width*height] tile index per cell, written on success
        public int* resultInfo;            // [3]: status, failCell, attempts

        // sized by solver
        public ulong* domain;           // [N]
        public ulong* baseDomains;      // [N]
        public float* weightLogWeights; // [perCell ? N*count : count]
        public float* heapKeys;         // [N]
        public int* heap;               // [N]
        public int* heapPos;            // [N]
        public int* queue;              // [2N + 1]
        public int* changedCells;       // [N]
        public byte* queuedFlags;       // [2N]
        public byte* changedFlags;      // [N]
    }

    /// <summary>
    /// Unmanaged WFC solver kernel. Operates on caller-owned buffers to allow for runs under managed execution or Burst.
    /// </summary>
    public static class WFCKernel {
        /// <summary>
        /// Executes the solver using the supplied buffers and writes the result and execution status into the output buffers.
        /// </summary>
        public static void Run(ref WFCKernelArgs a) {
            WFCState s = new(ref a);
            s.Execute();
        }
    }

    unsafe struct WFCState {
        #region Configuration
        readonly int _w;
        readonly int _h;
        readonly int _tileCount;
        readonly int _cellCount;
        readonly int _windowCount;
        readonly int _maxAttempts;
        readonly bool _perCellWeights;
        #endregion

        #region Input buffers
        readonly float* _weight;
        readonly float* _cellWeights;
        readonly ulong* _supportMasks;
        readonly ulong* _windowTargets;
        readonly ulong* _windowFeasibleMasks;
        readonly ulong* _initialDomains;
        readonly byte* _windowMarker;
        #endregion

        #region Output buffers
        readonly byte* _result;
        readonly int* _resultInfo; // Output metadata
        #endregion

        #region Working state
        readonly ulong* _domains;
        readonly ulong* _baseDomains;
        readonly float* _weightLogWeights; // Precomputed weight * log(weight), used by Shannon entropy.
        #endregion

        #region Queue
        readonly int* _queue;        // Encoded entries: cell * 2 + kind (adjacency or window check).
        readonly byte* _queuedFlags; // Prevents duplicate queue entries; indexed by kind * cellCount + cell.
        int _qHead, _qTail, _qCount;
        readonly int _qCap;
        #endregion

        #region Changed-cell tracking
        readonly int* _changedCells;  // Cells whose domains changed since the last heap update.
        readonly byte* _changedFlags; // Prevents duplicate entries in changedCells.
        int _changedCount;
        #endregion

        #region Entropy heap
        readonly float* _heapKeys; // Entropy priorities indexed by cell.
        readonly int* _heap;
        readonly int* _heapPos; // Reverse heap lookup: cell -> heap index, or -1 if absent.
        int _heapCount;
        #endregion

        #region Misc
        SplitMix64 _rng;
        int _failedCell;
        #endregion

        public WFCState(ref WFCKernelArgs a) {
            _w                   = a.width;
            _h                   = a.height;
            _tileCount           = a.tileCount;
            _cellCount           = a.width * a.height;
            _windowCount         = a.windowCount;
            _maxAttempts         = a.maxAttempts < 1 ? 1 : a.maxAttempts;
            _perCellWeights      = a.hasPerCellWeights != 0;
            _weight              = a.weights;
            _cellWeights         = a.cellWeights;
            _weightLogWeights    = a.weightLogWeights;
            _heapKeys            = a.heapKeys;
            _supportMasks        = a.supportMasks;
            _windowTargets       = a.windowTargets;
            _windowFeasibleMasks = a.windowFeasibleMasks;
            _initialDomains      = a.initialDomains;
            _domains             = a.domain;
            _baseDomains         = a.baseDomains;
            _windowMarker        = a.windowMarker;
            _result              = a.result;
            _queuedFlags         = a.queuedFlags;
            _changedFlags        = a.changedFlags;
            _resultInfo          = a.resultInfo;
            _heap                = a.heap;
            _heapPos             = a.heapPos;
            _queue               = a.queue;
            _changedCells        = a.changedCells;
            _rng                 = new SplitMix64(a.seed);
            _qHead               = 0;
            _qTail               = 0;
            _qCount              = 0;
            _qCap                = 2 * a.width * a.height + 1;
            _changedCount        = 0;
            _heapCount           = 0;
            _failedCell          = -1;
        }

        public void Execute() {
            int    weightCount = _perCellWeights ? _cellCount * _tileCount : _tileCount;
            float* srcWeights  = _perCellWeights ? _cellWeights : _weight;
            for (int i = 0; i < weightCount; i++) {
                float weight = srcWeights[i];
                _weightLogWeights[i] = weight > 0f ? weight * math.log(weight) : 0f;
            }

            // check for cells with no initially valid tiles.
            for (int i = 0; i < _cellCount; i++) {
                _domains[i] = _initialDomains[i];
                if (_domains[i] != 0UL) continue;
                Finish((int)WFCStatus.Unsatisfiable, i, 0);
                return;
            }

            #region Initial Propagation Pass
            // establish all deductions possible from initial domains before letting the solver work
            ResetSearchState();
            for (int i = 0; i < _cellCount; i++) {
                Enqueue(i, 0);
                if (_windowCount > 0) Enqueue(i, 1);
            }

            if (!Propagate()) {
                Finish((int)WFCStatus.Unsatisfiable, _failedCell, 0);
                return;
            }
            #endregion

            for (int i = 0; i < _cellCount; i++) {
                _baseDomains[i] = _domains[i];
            }

            for (int attempt = 1; attempt <= _maxAttempts; attempt++) {
                for (int i = 0; i < _cellCount; i++) _domains[i] = _baseDomains[i];
                ResetSearchState();
                // init entropy heat w/ all cells that still have multiple candidates
                for (int i = 0; i < _cellCount; i++) {
                    ulong domain        = _domains[i];
                    bool  isUncollapsed = (domain & (domain - 1UL)) != 0UL;
                    if (isUncollapsed) HeapUpdate(i, Entropy(i));
                }

                bool ok = true;
                // collapse lowest-entropy cell
                while (_heapCount > 0) {
                    int cell = _heap[0];
                    int pick = PickTile(cell);

                    // collapse cell & propagate constraints
                    _domains[cell] = 1UL << pick;
                    OnDomainChanged(cell);
                    if (!Propagate()) {
                        ok = false;
                        break;
                    }

                    // commit pending domain changes to entropy heap
                    FlushChanged();
                }

                // if failed, discard attempt and retry
                if (!ok) continue;

                // extract resolved tile index & report success
                for (int i = 0; i < _cellCount; i++) _result[i] = (byte)math.tzcnt(_domains[i]);
                Finish((int)WFCStatus.Success, -1, attempt);
                return;
            }

            Finish((int)WFCStatus.Failed, _failedCell, _maxAttempts);
        }

        void Finish(int status, int cell, int attempts) {
            _resultInfo[0] = status;
            _resultInfo[1] = cell;
            _resultInfo[2] = attempts;
        }

        /// <summary>
        /// Clear transient state used by the solver's search (prop queues, queued flags, bookkeeping).
        /// </summary>
        void ResetSearchState() {
            for (int i = 0; i < 2 * _cellCount; i++) _queuedFlags[i] = 0;
            for (int i = 0; i < _cellCount; i++) {
                _changedFlags[i] = 0;
                _heapPos[i]      = -1;
            }

            _qHead        = 0;
            _qTail        = 0;
            _qCount       = 0;
            _changedCount = 0;
            _heapCount    = 0;
        }

        // ---- work queue: entries are cell*2 + kind, kind 0 = push the cell's domain to its neighbours, 1 = check windows centred on it
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Enqueue(int cell, int kind) {
            int f = kind * _cellCount + cell;
            if (_queuedFlags[f] != 0) return;
            _queuedFlags[f] = 1;
            _queue[_qTail]  = cell * 2 + kind;
            if (++_qTail == _qCap) _qTail = 0;
            _qCount++;
        }

        int Dequeue() {
            int entry = _queue[_qHead];
            if (++_qHead == _qCap) {
                _qHead = 0;
            }

            _qCount--;

            int kind = entry & 1;
            int cell = entry >> 1;
            _queuedFlags[kind * _cellCount + cell] = 0;
            return entry;
        }

        void OnDomainChanged(int cell) {
            if (_changedFlags[cell] == 0) {
                _changedFlags[cell]               = 1;
                _changedCells[_changedCount++] = cell;
            }

            Enqueue(cell, 0);
            if (_windowCount <= 0) return;

            // enqueue window neighbours
            int jx = cell % _w, jy = cell / _w;
            for (int dy = -1; dy <= 1; dy++) {
                for (int dx = -1; dx <= 1; dx++) {
                    int nx = jx + dx, ny = jy + dy;
                    if (IsOutOfBounds(nx, ny, _w, _h)) continue;
                    Enqueue(ny * _w + nx, 1);
                }
            }
        }

        bool Propagate() {
            while (_qCount > 0) {
                int  entry         = Dequeue();
                int  cell          = entry >> 1;
                bool isWindowCheck = (entry & 1) != 0;

                if (isWindowCheck) {
                    if (!CheckWindows(cell)) return false;
                } else if (!PropagateAdjacency(cell)) {
                    return false;
                }
            }

            return true;
        }

        bool PropagateAdjacency(int cell) {
            ulong sourceDomain = _domains[cell];
            int   x            = cell % _w, y = cell / _w;

            for (int direction = 0; direction < 8; direction++) {
                int nx = x + Dx(direction), ny = y + Dy(direction);
                if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                int neighbour = ny * _w + nx;
                ulong supportedTiles = GetSupportMask(
                    sourceDomain, direction, _supportMasks, _tileCount
                );

                if (!ReduceDomain(neighbour, supportedTiles)) return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong GetSupportMask(ulong domain, int direction, ulong* supportMasks, int tileCount) {
            ulong* support = supportMasks + direction * tileCount;
            ulong  mask    = 0UL;

            while (domain != 0UL) {
                int tile = math.tzcnt(domain);
                mask   |= support[tile];
                domain &= domain - 1UL;
            }

            return mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ReduceDomain(int cell, ulong supportedTiles) {
            ulong domain        = _domains[cell];
            ulong reducedDomain = domain & supportedTiles;

            if (reducedDomain == domain) return true;
            if (reducedDomain == 0UL) {
                _failedCell = cell;
                return false;
            }

            _domains[cell] = reducedDomain;
            OnDomainChanged(cell);
            return true;
        }

        bool CheckWindows(int cell) {
            int cx = cell % _w, cy = cell / _w;

            for (int window = 0; window < _windowCount; window++) {
                ulong centreDomain = _domains[cell];
                ulong targetTiles  = _windowTargets[window];

                if ((centreDomain & targetTiles) == 0UL) continue;

                ulong markerBit = 1UL << _windowMarker[window];
                GetWindowMarkerMasks(cx, cy, markerBit, out int mustMask, out int mayMask);

                ulong* feasible = _windowFeasibleMasks + (long)window * WFCRules.FEASIBLE_WORDS;

                if (!IsWindowRenderable(feasible, mustMask, mayMask)) {
                    if (!PruneWindowCentre(cell, centreDomain, targetTiles)) return false;
                    continue;
                }

                // Neighbour pruning is only useful once the centre is committed.
                bool centreIsCollapsed = (centreDomain & (centreDomain - 1UL)) == 0UL;
                if (!centreIsCollapsed) continue;

                for (int direction = 0; direction < 8; direction++) {
                    int nx = cx + Dx(direction), ny = cy + Dy(direction);
                    if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                    int neighbour = ny * _w + nx;
                    if (!PruneWindowNeighbour(
                            neighbour, direction, markerBit, mustMask, mayMask, feasible
                        )) return false;
                }
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void GetWindowMarkerMasks(int cx, int cy, ulong markerBit, out int mustMask, out int mayMask) {
            mustMask = 0;
            mayMask  = 0;

            for (int d = 0; d < 8; d++) {
                int nx = cx + Dx(d), ny = cy + Dy(d);
                if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                ulong neighbourDomain = _domains[ny * _w + nx];
                if ((neighbourDomain & markerBit) == 0UL) continue;

                mayMask |= 1 << d;
                if (neighbourDomain == markerBit) mustMask |= 1 << d;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsWindowRenderable(ulong* feasible, int mustMask, int mayMask) =>
            Bit(feasible, (mustMask << 8) | mayMask);

        bool PruneWindowCentre(int cell, ulong centreDomain, ulong targetTiles) {
            ulong reducedDomain = centreDomain & ~targetTiles;

            if (reducedDomain == 0UL) {
                _failedCell = cell;
                return false;
            }

            _domains[cell] = reducedDomain;
            OnDomainChanged(cell);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool PruneWindowNeighbour(int cell, int direction, ulong markerBit, int mustMask, int mayMask, ulong* feasible) {
            ulong neighbourDomain = _domains[cell];
            if ((neighbourDomain & markerBit) == 0UL || neighbourDomain == markerBit) {
                return true;
            }

            int directionBit         = 1 << direction;
            int mustWithoutDirection = mustMask & ~directionBit;
            int mayWithoutDirection  = mayMask & ~directionBit;

            bool markerIsFeasible = IsWindowRenderable(
                feasible,
                mustWithoutDirection | directionBit,
                mayWithoutDirection | directionBit
            );

            bool otherIsFeasible = IsWindowRenderable(
                feasible, mustWithoutDirection, mayWithoutDirection
            );

            switch (markerIsFeasible) {
                case false when !otherIsFeasible:
                    _failedCell = cell;
                    return false;
                case false:
                    _domains[cell] = neighbourDomain & ~markerBit;
                    break;
                default: {
                    if (otherIsFeasible) return true;
                    _domains[cell] = markerBit;
                    break;
                }
            }

            OnDomainChanged(cell);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Bit(ulong* t, int i) => ((t[i >> 6] >> (i & 63)) & 1UL) != 0UL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Dx(int d) {
            return d switch {
                1 or 2 or 3 => 1,
                5 or 6 or 7 => -1,
                _           => 0
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Dy(int d) {
            return d switch {
                0 or 1 or 7 => 1,
                3 or 4 or 5 => -1,
                _           => 0
            };
        }

        // ---- weights, entropy, collapse
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float WeightOf(int cell, int t) => _perCellWeights ? _cellWeights[(long)cell * _tileCount + t] : _weight[t];

        float Entropy(int cell) {
            ulong remainingTiles = _domains[cell];
            float weightSum      = 0f, weightLogWeightSum = 0f;
            float* weightLogWeights = _perCellWeights
                ? _weightLogWeights + (long)cell * _tileCount
                : _weightLogWeights;

            while (remainingTiles != 0UL) {
                int tile = math.tzcnt(remainingTiles);
                remainingTiles &= remainingTiles - 1UL;

                weightSum          += WeightOf(cell, tile);
                weightLogWeightSum += weightLogWeights[tile];
            }

            return math.log(weightSum) - weightLogWeightSum / weightSum + _rng.NextFloat() * 1e-3f;
        }

        int PickTile(int cell) {
            ulong remainingTiles = _domains[cell];
            float weightSum      = 0f;

            while (remainingTiles != 0UL) {
                int tile = math.tzcnt(remainingTiles);
                remainingTiles &= remainingTiles - 1UL;
                weightSum      += WeightOf(cell, tile);
            }

            float randomWeight = _rng.NextFloat() * weightSum;
            int   selectedTile = 0;
            remainingTiles = _domains[cell];

            while (remainingTiles != 0UL) {
                int tile = math.tzcnt(remainingTiles);
                remainingTiles &= remainingTiles - 1UL;

                selectedTile =  tile;
                randomWeight -= WeightOf(cell, tile);
                if (randomWeight <= 0f) break;
            }

            return selectedTile;
        }

        void FlushChanged() {
            for (int i = 0; i < _changedCount; i++) {
                int cell = _changedCells[i];
                _changedFlags[cell] = 0;

                ulong domain      = _domains[cell];
                bool  isCollapsed = (domain & (domain - 1UL)) == 0UL;
                if (isCollapsed) HeapRemove(cell);
                else HeapUpdate(cell, Entropy(cell));
            }

            _changedCount = 0;
        }

        // ---- indexed min-heap over key[]
        void HeapSwap(int a, int b) {
            int ca = _heap[a], cb = _heap[b];
            _heap[a]     = cb;
            _heap[b]     = ca;
            _heapPos[cb] = a;
            _heapPos[ca] = b;
        }

        void SiftUp(int i) {
            while (i > 0) {
                int p = (i - 1) >> 1;
                if (_heapKeys[_heap[i]] < _heapKeys[_heap[p]]) {
                    HeapSwap(i, p);
                    i = p;
                } else break;
            }
        }

        void SiftDown(int heapIndex) {
            while (true) {
                int leftChild  = 2 * heapIndex + 1;
                int rightChild = leftChild + 1;
                int smallest   = heapIndex;

                if (leftChild < _heapCount &&
                    _heapKeys[_heap[leftChild]] < _heapKeys[_heap[smallest]]) {
                    smallest = leftChild;
                }

                if (rightChild < _heapCount &&
                    _heapKeys[_heap[rightChild]] < _heapKeys[_heap[smallest]]) {
                    smallest = rightChild;
                }

                if (smallest == heapIndex) break;

                HeapSwap(heapIndex, smallest);
                heapIndex = smallest;
            }
        }

        void HeapUpdate(int cell, float key) {
            _heapKeys[cell] = key;
            int pos = _heapPos[cell];

            if (pos < 0) {
                pos            = _heapCount++;
                _heap[pos]     = cell;
                _heapPos[cell] = pos;
                SiftUp(pos);
            } else {
                SiftUp(pos);
                SiftDown(_heapPos[cell]);
            }
        }

        void HeapRemove(int cell) {
            int pos = _heapPos[cell];
            if (pos < 0) return;

            int lastHeapIndex = --_heapCount;
            _heapPos[cell] = -1;
            if (pos == lastHeapIndex) return;

            int movedCell = _heap[lastHeapIndex];
            _heap[pos]          = movedCell;
            _heapPos[movedCell] = pos;

            SiftUp(pos);
            SiftDown(_heapPos[movedCell]);
        }
    }
}
