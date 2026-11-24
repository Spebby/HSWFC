using System.Runtime.CompilerServices;
using Unity.Mathematics;
using static WFC.Core.Dirs;
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
        public int wordsPerDomain; // ceil(tileCount / 64)
        public ulong seed;
        public byte hasPerCellWeights;     // 1 = cellWeights is [width*height*count], 0 = use weight[count]
        public float* weights;             // [tileCount]
        public float* cellWeights;         // [cellCount * tileCount], optional
        public ulong* supportMasks;        // [8][tileCount][wordsPerDomain]
        public ulong* windowTargets;       // [windowCount][wordsPerDomain]
        public ulong* windowMarkers;       // [windowCount][wordsPerDomain]
        public ulong* windowFeasibleMasks; // [windowCount][WFCRules.FEASIBLE_WORDS]
        public ulong* initialDomains;      // [cellCount][wordsPerDomain]
        public int* result;                // [cellCount] tile (route) index per cell, written on success
        public int* resultInfo;            // [3]: status, failCell, attempts

        // working domain buffers: [cellCount][wordsPerDomain]
        public ulong* domain;
        public ulong* baseDomains;
        public ulong* scratch;

        // [cellCount * tileCount] with per-cell weights, otherwise [tileCount].
        public float* weightLogWeights;

        // Indexed min-heap; keys and pos indexed by cell
        public float* heapKeys; // [cellCount]
        public int* heap;       // [cellCount]
        public int* heapPos;    // [cellCount]

        // ring queue
        public int* queue; // [2 * cellCount + 1]

        public int* changedCells;  // [cellCount]
        public byte* queuedFlags;  // [2 * cellCount] adjacency and window flags
        public byte* changedFlags; // [cellCount]

        // Hierarchy. Tiles are routes: route r's incoming edges occupy [routeOffsets[r], routeOffsets[r + 1])
        // of routeEdges, root first (-1). Flat WFC is a root with one leaf per tile.
        public int* routeOffsets;  // [tileCount + 1]
        public int* routeEdges;    // [total route length]
        public float* edgeWeights; // [edgeCount] authored weight of each child edge
        public int* edgeList;      // [edgeCount] scratch
        public int* edgeCounts;    // [edgeCount] scratch
        public float* edgeSums;    // [edgeCount] scratch
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
        readonly ulong* _windowMarkers;
        #endregion

        #region Hierarchy buffers
        readonly int* _routeOffsets;
        readonly int* _routeEdges;
        readonly float* _edgeWeights;
        readonly int* _edgeList;
        readonly int* _edgeCounts;
        readonly float* _edgeSums;
        #endregion

        #region Output buffers
        readonly int* _result;
        readonly int* _resultInfo; // Output metadata
        #endregion

        #region Working state
        readonly int _wordsPerDomain;
        readonly ulong* _domains;
        readonly ulong* _baseDomains;
        readonly ulong* _scratch;
        readonly float* _weightLogWeights; // Precomputed weight * log(weight), used by Shannon entropy.
        #endregion

        #region Changed-cell tracking
        readonly int* _changedCells;  // Cells whose domains changed since the last heap update.
        readonly byte* _changedFlags; // Prevents duplicate entries in changedCells.
        int _changedCount;
        #endregion

        PropagationQueue _queue;
        EntropyHeap _heap;

        #region Misc
        SplitMix64 _rng;
        int _failedCell;
        #endregion

        Domain D(int cell) => new(_domains, cell, _wordsPerDomain);

        // support masks for a source tile in a given direction
        Domain S(Direction dir, int tile) => new(_supportMasks, (int)dir * _tileCount + tile, _wordsPerDomain);

        // target tile mask for a window rule
        Domain Wt(int window) => new(_windowTargets, window, _wordsPerDomain);

        // marker tile mask for a window rule
        Domain Wm(int window) => new(_windowMarkers, window, _wordsPerDomain);

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
            _supportMasks        = a.supportMasks;
            _windowTargets       = a.windowTargets;
            _windowFeasibleMasks = a.windowFeasibleMasks;
            _initialDomains      = a.initialDomains;
            _wordsPerDomain      = a.wordsPerDomain;
            _domains             = a.domain;
            _baseDomains         = a.baseDomains;
            _scratch             = a.scratch;
            _windowMarkers       = a.windowMarkers;
            _routeOffsets        = a.routeOffsets;
            _routeEdges          = a.routeEdges;
            _edgeWeights         = a.edgeWeights;
            _edgeList            = a.edgeList;
            _edgeCounts          = a.edgeCounts;
            _edgeSums            = a.edgeSums;
            _result              = a.result;
            _changedFlags        = a.changedFlags;
            _resultInfo          = a.resultInfo;
            _changedCells        = a.changedCells;
            _rng                 = new SplitMix64(a.seed);
            _changedCount        = 0;
            _failedCell          = -1;

            _queue = new PropagationQueue(a.queue, a.queuedFlags, _cellCount);
            _heap  = new EntropyHeap(a.heapKeys, a.heap, a.heapPos);
        }

        public void Execute() {
            int    weightCount = _perCellWeights ? _cellCount * _tileCount : _tileCount;
            float* srcWeights  = _perCellWeights ? _cellWeights : _weight;
            for (int i = 0; i < weightCount; i++) {
                float weight = srcWeights[i];
                _weightLogWeights[i] = weight > 0f ? weight * math.log(weight) : 0f;
            }

            // check for cells with no initially valid tiles.
            Domain.CopyRange(_domains, _initialDomains, _cellCount, _wordsPerDomain);
            for (int i = 0; i < _cellCount; i++) {
                if (!D(i).IsEmpty) continue;
                Finish((int)WFCStatus.Unsatisfiable, i, 0);
                return;
            }

            #region Initial Propagation Pass
            // establish all deductions possible from initial domains before letting the solver work
            ResetSearchState();
            for (int i = 0; i < _cellCount; i++) {
                _queue.Enqueue(i, 0);
                if (_windowCount > 0) _queue.Enqueue(i, 1);
            }

            if (!Propagate()) {
                Finish((int)WFCStatus.Unsatisfiable, _failedCell, 0);
                return;
            }
            #endregion

            Domain.CopyRange(_baseDomains, _domains, _cellCount, _wordsPerDomain);
            for (int attempt = 1; attempt <= _maxAttempts; attempt++) {
                Domain.CopyRange(_domains, _baseDomains, _cellCount, _wordsPerDomain);
                ResetSearchState();
                // init entropy heat w/ all cells that still have multiple candidates
                for (int i = 0; i < _cellCount; i++) {
                    if (!D(i).IsCollapsed) _heap.Update(i, Entropy(i));
                }

                bool ok = true;
                // collapse lowest-entropy cell
                while (_heap.Count > 0) {
                    int cell = _heap.Min;

                    // collapse cell & propagate constraints
                    CollapseCell(cell);
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

                // extract resolved tile (route) index & report success
                for (int i = 0; i < _cellCount; i++) _result[i] = D(i).FirstSet();
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
        /// Clear transient propagation and domain-change state between search attempts.
        /// </summary>
        void ResetSearchState() {
            _queue.Reset();

            for (int i = 0; i < _cellCount; i++) {
                _changedFlags[i] = 0;
            }

            _changedCount = 0;
            _heap.Reset(_cellCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void OnDomainChanged(int cell) {
            if (_changedFlags[cell] == 0) {
                _changedFlags[cell]            = 1;
                _changedCells[_changedCount++] = cell;
            }

            _queue.Enqueue(cell, 0);
            if (_windowCount <= 0) return;

            // enqueue window neighbours b/c a changed cell can affect windows centred on itself or any of its neighbours
            int jx = cell % _w, jy = cell / _w;
            for (int dy = -1; dy <= 1; dy++) {
                for (int dx = -1; dx <= 1; dx++) {
                    int nx = jx + dx, ny = jy + dy;
                    if (IsOutOfBounds(nx, ny, _w, _h)) continue;
                    _queue.Enqueue(ny * _w + nx, 1);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Propagate() {
            while (_queue.Count > 0) {
                int  entry         = _queue.Dequeue();
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool PropagateAdjacency(int cell) {
            Domain source  = D(cell);
            Domain scratch = new(_scratch, _wordsPerDomain);
            int    x       = cell % _w, y = cell / _w;

            for (int d = 0; d < Count; d++) {
                Direction dir = (Direction)d;
                int       nx  = x + Dx(dir), ny = y + Dy(dir);
                if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                // build union of tiles supported by every tile in the src domain
                scratch.ClearAll();
                for (int w = 0; w < _wordsPerDomain; w++) {
                    ulong bits = source.Word(w);

                    while (bits != 0UL) {
                        int tile = Domain.FirstSetInWord(w, bits);
                        bits &= bits - 1UL; // clear lowest set bit

                        Domain support = S(dir, tile);
                        for (int j = 0; j < _wordsPerDomain; j++) {
                            scratch.OrWord(j, support.Word(j));
                        }
                    }
                }

                // neighbour can only contain tiles supported by src domain
                if (!ReduceDomain(ny * _w + nx, scratch)) return false;
            }

            return true;
        }

        /// <summary>
        /// Intersects a cell's domain with its supported tiles and schedules propagation if it changed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool ReduceDomain(int cell, Domain supported) {
            Domain d = D(cell);
            if (!d.AndWith(supported)) return true;
            if (d.IsEmpty) {
                _failedCell = cell;
                return false;
            }

            OnDomainChanged(cell);
            return true;
        }

        #region Window Constraints
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool CheckWindows(int cell) {
            int    cx     = cell % _w, cy = cell / _w;
            Domain centre = D(cell);

            for (int window = 0; window < _windowCount; window++) {
                Domain target = Wt(window);
                if (!centre.Intersects(target)) continue;

                Domain marker = Wm(window);
                GetWindowMarkerMasks(cx, cy, marker, out int mustMask, out int mayMask);
                ulong* feasible = _windowFeasibleMasks + (long)window * WFCRules.FEASIBLE_WORDS;

                if (!IsWindowRenderable(feasible, mustMask, mayMask)) {
                    centre.AndNotWith(target);
                    if (centre.IsEmpty) {
                        _failedCell = cell;
                        return false;
                    }

                    OnDomainChanged(cell);
                    continue;
                }

                if (!centre.IsCollapsed) continue;

                for (int d = 0; d < Count; d++) {
                    Direction dir = (Direction)d;
                    int       nx  = cx + Dx(dir), ny = cy + Dy(dir);
                    if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                    int neighbour = ny * _w + nx;
                    if (!PruneWindowNeighbour(neighbour, dir, marker, mustMask, mayMask, feasible)) {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>Builds the marker occupancy bounds around a window centre.</summary>
        /// <param name="mustMask">Contains directions where the marker is guaranteed.</param>
        /// <param name="mayMask">Contains directions where the marker is possible.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void GetWindowMarkerMasks(int cx, int cy, Domain marker, out int mustMask, out int mayMask) {
            mustMask = 0;
            mayMask  = 0;
            for (int d = 0; d < Count; d++) {
                Direction dir = (Direction)d;
                int       nx  = cx + Dx(dir), ny = cy + Dy(dir);
                if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                Domain nd = D(ny * _w + nx);
                if (!nd.Intersects(marker)) continue;

                mayMask |= 1 << d;
                if (nd.IsSubsetOf(marker)) mustMask |= 1 << d;
            }
        }

        /// <summary>
        /// Feasibility is indexed by a 16-bit pair. mustMask in the high byte and mayMask in the low byte.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsWindowRenderable(ulong* feasible, int mustMask, int mayMask) =>
            Bit(feasible, PackWindowMasks(mustMask, mayMask));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool PruneWindowNeighbour(int cell, Direction direction, Domain marker, int mustMask, int mayMask,
                                  ulong* feasible) {
            Domain nd = D(cell);
            if (!nd.Intersects(marker) || nd.IsSubsetOf(marker)) return true;

            int directionBit         = 1 << (int)direction;
            int mustWithoutDirection = mustMask & ~directionBit;
            int mayWithoutDirection  = mayMask & ~directionBit;

            // test two possibilities for this neighbour
            // 1. it contains the marker
            // 2. it does not contain the marker
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
                    nd.AndNotWith(marker);
                    break;
                default: {
                    if (otherIsFeasible) return true;
                    nd.AndWith(marker);
                    break;
                }
            }

            OnDomainChanged(cell);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool Bit(ulong* t, int i) => ((t[i >> 6] >> (i & 63)) & 1UL) != 0UL;
        #endregion

        #region Weights, Entropy & Collapse
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float WeightOf(int cell, int t) => _perCellWeights ? _cellWeights[(long)cell * _tileCount + t] : _weight[t];

        /// <remarks> A small random offset breaks ties between otherwise equivalent cells. </remarks>
        /// <returns>Shannon entropy of a cell's current weighted domain.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        float Entropy(int cell) {
            Domain domain    = D(cell);
            float  weightSum = 0f, weightLogWeightSum = 0f;
            float* weightLogWeights = _perCellWeights
                ? _weightLogWeights + (long)cell * _tileCount
                : _weightLogWeights;

            for (int w = 0; w < _wordsPerDomain; w++) {
                ulong bits = domain.Word(w);

                while (bits != 0UL) {
                    int tile = Domain.FirstSetInWord(w, bits);
                    bits &= bits - 1UL;

                    weightSum          += WeightOf(cell, tile);
                    weightLogWeightSum += weightLogWeights[tile];
                }
            }

            return math.log(weightSum) - weightLogWeightSum / weightSum + _rng.NextFloat() * 1e-3f;
        }

        /// <summary>
        /// Takes one step down the hierarchy for a cell. The cell's remaining routes share a prefix from the
        /// root; the first place they differ is a branch. Each distinct child edge there is weighted by its
        /// authored weight times the mean tile weight of the routes that still use it, one is chosen, and
        /// the routes through the others are dropped. A flat hierarchy has a single branch at the root, whose
        /// children are the tiles themselves, so this is a weighted pick among them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CollapseCell(int cell) {
            Domain domain = D(cell);
            int    first  = domain.FirstSet();

            // First position at which the remaining routes use different edges.
            int firstStart = _routeOffsets[first], firstLength = _routeOffsets[first + 1] - firstStart;
            int branch     = firstLength;
            for (int route = domain.NextSet(first + 1); route >= 0; route = domain.NextSet(route + 1)) {
                int start = _routeOffsets[route];
                int limit = math.min(firstLength, _routeOffsets[route + 1] - start);
                int i     = 0;
                while (i < limit && _routeEdges[firstStart + i] == _routeEdges[start + i]) i++;
                if (i < branch) branch = i;
            }

            if (branch >= firstLength) return; // a single route has nothing left to choose

            // Group the remaining routes by the edge they take at the branch.
            for (int route = first; route >= 0; route = domain.NextSet(route + 1)) {
                int edge = _routeEdges[_routeOffsets[route] + branch];
                _edgeCounts[edge] = 0;
                _edgeSums[edge]   = 0f;
            }

            int groups = 0;
            for (int route = first; route >= 0; route = domain.NextSet(route + 1)) {
                int edge                                          = _routeEdges[_routeOffsets[route] + branch];
                if (_edgeCounts[edge]++ == 0) _edgeList[groups++] = edge;
                _edgeSums[edge] += WeightOf(cell, route);
            }

            float total = 0f;
            for (int g = 0; g < groups; g++) {
                int edge = _edgeList[g];
                total += _edgeWeights[edge] * (_edgeSums[edge] / _edgeCounts[edge]);
            }

            // Weighted roulette over the groups. When every authored weight is zero, choose uniformly.
            int chosen;
            if (total > 0f) {
                float randomWeight = _rng.NextFloat() * total;
                chosen = -1;
                for (int g = 0; g < groups; g++) {
                    int   edge   = _edgeList[g];
                    float weight = _edgeWeights[edge] * (_edgeSums[edge] / _edgeCounts[edge]);
                    if (weight <= 0f) continue;

                    chosen       =  edge;
                    randomWeight -= weight;
                    if (randomWeight <= 0f) break;
                }
            } else {
                chosen = _edgeList[math.min((int)(_rng.NextFloat() * groups), groups - 1)];
            }

            for (int route = first; route >= 0; route = domain.NextSet(route + 1)) {
                if (_routeEdges[_routeOffsets[route] + branch] != chosen) domain.Clear(route);
            }
        }

        /// <summary> Commit accumulated domain changes to the entropy heap after propagation. </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void FlushChanged() {
            for (int i = 0; i < _changedCount; i++) {
                int cell = _changedCells[i];
                _changedFlags[cell] = 0;

                if (D(cell).IsCollapsed) _heap.Remove(cell);
                else _heap.Update(cell, Entropy(cell));
            }

            _changedCount = 0;
        }
        #endregion
    }
}