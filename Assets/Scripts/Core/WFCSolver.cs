using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;


namespace WFC.Core {
    /// <summary>Executes the WFC kernel using a given execution backend.</summary>
    public interface IWFCExecutor {
        void Execute(ref WFCKernelArgs args);
    }

    /// <summary>
    /// Executes the WFC kernel directly on the managed thread.
    /// </summary>
    public sealed class ManagedIWFCExecutor : IWFCExecutor {
        public void Execute(ref WFCKernelArgs args) => WFCKernel.Run(ref args);
    }

    /// <summary>
    /// Managed owner and interface for the unmanaged WFC solver kernel.
    /// Owns and pins the buffers passed to the kernel for the lifetime of the solver.
    /// </summary>
    public sealed unsafe class WFCSolver : IDisposable {
        readonly WFCRules _rules; // route level: tile i is route i of the graph
        readonly CompiledSemanticGraph _graph;
        readonly int _width;
        readonly int _height;
        readonly IWFCExecutor _executor;

        readonly int _terrainCount, _tileCount, _cellCount;
        readonly List<GCHandle> _pinnedHandles = new();
        readonly ulong[] _initialDomains;
        readonly int[] _initialSemanticNodes;
        readonly int[] _resultRoutes;
        readonly Dictionary<int, ulong[]> _routesThroughNode = new();
        readonly int[] _resultInfo = new int[3];
        readonly bool _hasPerCellWeights;
        WFCKernelArgs _args;
        bool _isDisposed;
        readonly int _wordsPerDomain;

        /// <summary>Terrain index per cell, index = y * Width + x. Overwritten by every successful Solve.</summary>
        public int[] Layout { get; }

        /// <summary>
        /// Leaf node per cell after a successful Solve. Otherwise the node each cell starts at: the root, or the
        /// node given to Solve.
        /// </summary>
        public int[] SemanticNodes { get; }

        /// <summary>Number of search attempts used by the last Solve.</summary>
        public int LastAttempts { get; private set; }

        /// <summary>Cell where the last failure occurred, or -1 if none.</summary>
        public int LastFailCell { get; private set; }

        /// <summary>
        /// Creates solver for the terrain rules, a semantic graph over those terrains, and grid dimensions.
        /// Allocates and pins the kernel's working buffers. Without a graph, every terrain is a direct child of the
        /// root, which is plain WFC.
        /// </summary>
        /// <remarks>
        /// The kernel solves over the graph's routes. Expanding the terrain rules to routes happens here, once per solver.
        /// </remarks>
        public WFCSolver(WFCRules rules, int width, int height, bool hasPerCellWeights = false, IWFCExecutor executor = null, CompiledSemanticGraph semanticGraph = null) {
            if (width < 1 || height < 1) throw new ArgumentException("grid must be at least 1x1");
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            _width        = width;
            _height       = height;
            _terrainCount = rules.count;
            _graph        = semanticGraph ?? SemanticGraphCompiler.Flat(rules.count);
            _rules        = RouteRules.Expand(rules, _graph);
            _tileCount    = _rules.count;
            _cellCount    = checked(width * height);
            _executor     = executor ?? new ManagedIWFCExecutor();

            // NOTE: there's a fair few pins here, if there's a throw then it'll leak, so may be good to be more defensive
            _wordsPerDomain  = (_tileCount + 63) / 64;
            int domainWordCount = checked(_cellCount * _wordsPerDomain);

            Layout          = new int[_cellCount];
            SemanticNodes   = new int[_cellCount];
            _resultRoutes   = new int[_cellCount];
            _initialDomains = new ulong[domainWordCount];
            _initialSemanticNodes = new int[_cellCount];
            Array.Fill(SemanticNodes, _graph.RootId);
            Array.Fill(_initialSemanticNodes, _graph.RootId);
            Array.Fill(_resultRoutes, -1);

            ResetInitialDomains();
            _hasPerCellWeights = hasPerCellWeights;

            int weightCount = hasPerCellWeights
                ? checked(_cellCount * _tileCount)
                : _tileCount;

            int edgeCount = Math.Max(1, _graph.ChildWeights.Length);

            _args = new WFCKernelArgs {
                width               = width, height = height, tileCount = _tileCount, windowCount = _rules.windowCount,
                weights             = Pin(_rules.weight),
                supportMasks        = Pin(_rules.support),
                windowTargets       = Pin(_rules.windowCount == 0 ? new ulong[_wordsPerDomain] : _rules.windowTargets),
                windowMarkers       = Pin(_rules.windowCount == 0 ? new ulong[_wordsPerDomain] : _rules.windowMarkers),
                windowFeasibleMasks = Pin(_rules.windowFeasible),
                initialDomains      = Pin(_initialDomains),
                result              = Pin(_resultRoutes),
                resultInfo          = Pin(_resultInfo),
                routeOffsets        = Pin(_graph.RouteNodeOffsets),
                routeEdges          = Pin(_graph.RouteEdgeIndices),
                edgeWeights         = Pin(_graph.ChildWeights.Length == 0 ? new float[1] : _graph.ChildWeights),
                edgeList            = Pin(new int[edgeCount]),
                edgeCounts          = Pin(new int[edgeCount]),
                edgeSums            = Pin(new float[edgeCount]),
                wordsPerDomain      = _wordsPerDomain,
                domain              = Pin(new ulong[domainWordCount]),
                baseDomains         = Pin(new ulong[domainWordCount]),
                scratch             = Pin(new ulong[domainWordCount]),
                heapKeys            = Pin(new float[_cellCount]),
                heap                = Pin(new int[_cellCount]),
                heapPos             = Pin(new int[_cellCount]),
                queue               = Pin(new int[2 * _cellCount + 1]),
                changedCells        = Pin(new int[_cellCount]),
                queuedFlags         = Pin(new byte[2 * _cellCount]),
                changedFlags        = Pin(new byte[_cellCount]),
                hasPerCellWeights   = (byte)(hasPerCellWeights ? 1 : 0),
                cellWeights         = hasPerCellWeights ? Pin(new float[weightCount]) : null,
                weightLogWeights    = Pin(new float[weightCount])
            };
        }

        /// <summary> Pins array and returns a pointer to its memory. Retained until solver is disposed. </summary>
        T* Pin<T>(T[] arr) where T : unmanaged {
            GCHandle h = GCHandle.Alloc(arr, GCHandleType.Pinned);
            _pinnedHandles.Add(h);
            return (T*)h.AddrOfPinnedObject();
        }

        /// <summary>
        /// Solves the grid using the supplied seed and optional constraints.
        /// Initial domains restrict which routes may occupy each cell, one entry per cell per route word.
        /// Cell weights hold one weight per terrain per cell and bias selection independently per cell.
        /// initialSemanticNodes gives each cell a node to start from, or -1 for the root; the cell can then only
        /// become routes through that node. Boundary constraints remove tiles whose MustExist neighbours would
        /// fall outside the grid. Window constraints can be disabled with useWindows.
        /// </summary>
        public WFCStatus Solve(ulong seed, ReadOnlySpan<ulong> initialDomains = default, ReadOnlySpan<float> cellWeights = default, int maxAttempts = 100, bool useWindows = true, ReadOnlySpan<int> initialSemanticNodes = default) {
            const string INIT_DOMAIN_LENGTH_MISMATCH = "initialDomains needs one entry per cell per domain word";
            const string CELL_WEIGHT_COUNT_MISMATCH  = "cellWeights needs one entry per terrain per cell";
            const string SOLVER_CELL_WEIGHT_ERROR    = "This solver was not configured for per-cell weights";
            const float  MIN_TILE_WEIGHT             = 1e-4f;

            if (_isDisposed) {
                throw new ObjectDisposedException(nameof(WFCSolver));
            }

            if (!initialDomains.IsEmpty && initialDomains.Length != _initialDomains.Length) {
                throw new ArgumentException(INIT_DOMAIN_LENGTH_MISMATCH, nameof(initialDomains));
            }

            if (!initialSemanticNodes.IsEmpty) {
                if (initialSemanticNodes.Length != _cellCount)
                    throw new ArgumentException("Provide one starting node per cell.", nameof(initialSemanticNodes));

                foreach (int node in initialSemanticNodes) {
                    if (node < -1 || node >= _graph.NodeCount)
                        throw new ArgumentOutOfRangeException(nameof(initialSemanticNodes), "Starting node IDs must exist in the semantic graph.");
                }
            }

            // config per cell weights: one weight per terrain, shared by all of its routes
            if (_hasPerCellWeights) {
                if (cellWeights.Length != checked(_cellCount * _terrainCount)) {
                    throw new ArgumentException(CELL_WEIGHT_COUNT_MISMATCH, nameof(cellWeights));
                }

                float* weights = _args.cellWeights;
                for (int cell = 0; cell < _cellCount; cell++) {
                    for (int route = 0; route < _tileCount; route++) {
                        float weight = cellWeights[cell * _terrainCount + _graph.RouteTerrainIds[route]];
                        weights[cell * _tileCount + route] = Math.Max(MIN_TILE_WEIGHT, weight);
                    }
                }
            } else if (!cellWeights.IsEmpty) {
                throw new ArgumentException(SOLVER_CELL_WEIGHT_ERROR, nameof(cellWeights));
            }

            if (initialDomains.IsEmpty) {
                ResetInitialDomains();
            } else {
                initialDomains.CopyTo(_initialDomains);
                MaskInitialDomains();
            }

            ApplyStartingNodes(initialSemanticNodes);
            ApplyBoundary();

            // config kernel args
            _args.seed        = seed;
            _args.maxAttempts = maxAttempts;
            _args.windowCount = useWindows ? _rules.windowCount : 0;

            _resultInfo[0] = (int)WFCStatus.Failed;
            _resultInfo[1] = -1;
            _resultInfo[2] = 0;
            Array.Fill(_resultRoutes, -1);

            // execute
            _executor.Execute(ref _args);
            LastFailCell = _resultInfo[1];
            LastAttempts = _resultInfo[2];

            var status = (WFCStatus)_resultInfo[0];
            if (status != WFCStatus.Success) {
                Array.Copy(_initialSemanticNodes, SemanticNodes, _cellCount);
                return status;
            }

            // The kernel resolved each cell to one route. Report its terrain and its leaf.
            for (int cell = 0; cell < _cellCount; cell++) {
                int route = _resultRoutes[cell];
                Layout[cell]        = _graph.RouteTerrainIds[route];
                SemanticNodes[cell] = _graph.RouteNodes[_graph.RouteNodeOffsets[route + 1] - 1];
            }
            return status;
        }

        /// <summary>
        /// Copies the resolved path from the cell's starting node to its leaf.
        /// Returns an empty array until a solve has succeeded.
        /// </summary>
        public int[] GetSemanticPath(int cell) {
            if (_isDisposed) throw new ObjectDisposedException(nameof(WFCSolver));
            if (cell < 0 || cell >= _cellCount) throw new ArgumentOutOfRangeException(nameof(cell));
            if (_resultRoutes[cell] < 0) return Array.Empty<int>();

            int route = _resultRoutes[cell];
            int start = _graph.RouteNodeOffsets[route];
            int end   = _graph.RouteNodeOffsets[route + 1];
            while (_graph.RouteNodes[start] != _initialSemanticNodes[cell]) start++;

            var path = new int[end - start];
            Array.Copy(_graph.RouteNodes, start, path, 0, path.Length);
            return path;
        }

        /// <summary>Restricts each cell to the routes through its starting node (the root allows every route).</summary>
        void ApplyStartingNodes(ReadOnlySpan<int> initialSemanticNodes) {
            for (int cell = 0; cell < _cellCount; cell++) {
                int node = initialSemanticNodes.IsEmpty ? -1 : initialSemanticNodes[cell];
                if (node < 0) node = _graph.RootId;
                _initialSemanticNodes[cell] = node;
                if (node == _graph.RootId) continue;

                ulong[] through = RoutesThrough(node);
                for (int w = 0; w < _wordsPerDomain; w++) _initialDomains[cell * _wordsPerDomain + w] &= through[w];
            }
        }

        ulong[] RoutesThrough(int node) {
            if (_routesThroughNode.TryGetValue(node, out ulong[] mask)) return mask;

            mask = new ulong[_wordsPerDomain];
            for (int route = 0; route < _tileCount; route++) {
                for (int i = _graph.RouteNodeOffsets[route]; i < _graph.RouteNodeOffsets[route + 1]; i++) {
                    if (_graph.RouteNodes[i] != node) continue;
                    mask[route >> 6] |= 1UL << (route & 63);
                    break;
                }
            }

            _routesThroughNode.Add(node, mask);
            return mask;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ResetInitialDomains() {
            int   remainder     = _tileCount & 63;
            ulong finalWordMask = remainder == 0 ? ulong.MaxValue : (1UL << remainder) - 1UL;

            for (int cell = 0; cell < _cellCount; cell++) {
                int offset = cell * _wordsPerDomain;
                for (int w = 0; w < _wordsPerDomain - 1; w++)
                    _initialDomains[offset + w] = ulong.MaxValue;

                _initialDomains[offset + _wordsPerDomain - 1] = finalWordMask;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void MaskInitialDomains() {
            int remainder = _tileCount & 63;
            if (remainder == 0) return;

            ulong finalWordMask = (1UL << remainder) - 1UL;
            for (int cell = 0; cell < _cellCount; cell++)
                _initialDomains[cell * _wordsPerDomain + _wordsPerDomain - 1] &= finalWordMask;
        }

        /// <summary>
        /// Removes tiles from boundary cells when their mandatory neighbours would extend beyond the grid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ApplyBoundary() {
            ulong[] mustExist = _rules.mustExist;
            bool hasConstraints = mustExist.Any(word => word != 0);
            if (!hasConstraints) return;

            for (int y = 0; y < _height; y++) {
                for (int x = 0; x < _width; x++) {
                    if (!IsBoundaryCell(x, y)) continue;

                    int cellOffset = (y * _width + x) * _wordsPerDomain;
                    for (int w = 0; w < _wordsPerDomain; w++) {
                        ulong allowed = ulong.MaxValue;

                        for (int d = 0; d < Dirs.Count; d++) {
                            Direction dir = (Direction)d;
                            int       nx  = x + Dirs.Dx(dir);
                            int       ny  = y + Dirs.Dy(dir);

                            if (IsOutOfBounds(nx, ny, _width, _height)) {
                                allowed &= ~mustExist[d * _wordsPerDomain + w];
                            }
                        }

                        _initialDomains[cellOffset + w] &= allowed;
                    }
                }
            }
            return;
            bool IsBoundaryCell(int x, int y) => x == 0 || y == 0 || x == _width - 1 || y == _height - 1;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsOutOfBounds(int x, int y, int w, int h) => x < 0 || y < 0 || x >= w || y >= h;

        /// <summary> Releases all pinned buffers owned by solver. </summary>
        public void Dispose() {
            if (_isDisposed) return;
            _isDisposed = true;
            foreach (GCHandle h in _pinnedHandles.Where(h => h.IsAllocated)) h.Free();
            _pinnedHandles.Clear();
            GC.SuppressFinalize(this);
        }

        ~WFCSolver() {
            if (_isDisposed) return;
            foreach (GCHandle h in _pinnedHandles.Where(h => h.IsAllocated)) h.Free();
        }
    }
}