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
        readonly WFCRules _rules;
        readonly CompiledSemanticGraph _semanticGraph;
        readonly int _width;
        readonly int _height;
        readonly IWFCExecutor _executor;

        readonly int _tileCount, _cellCount;
        readonly List<GCHandle> _pinnedHandles = new();
        readonly ulong[] _initialDomains;
        readonly int[] _initialSemanticNodes;
        readonly int[] _resultRoutes;
        readonly int[] _resultInfo = new int[3];
        readonly bool _hasPerCellWeights;
        WFCKernelArgs _args;
        bool _isDisposed;
        readonly int _wordsPerDomain;

        /// <summary>Tile index per cell, index = y * Width + x. Overwritten by every successful Solve.</summary>
        public int[] Layout { get; }

        /// <summary>Final semantic node per cell after success, or the starting node before or after a failed solve.</summary>
        public int[] SemanticNodes { get; }
        /// <summary>Number of search attempts used by the last Solve.</summary>
        public int LastAttempts { get; private set; }

        /// <summary>Cell where the last failure occurred, or -1 if none.</summary>
        public int LastFailCell { get; private set; }

        /// <summary>
        /// Creates solver for the rules and grid dimensions. Allocates and pins the kernel's working buffers.
        /// </summary>
        public WFCSolver(WFCRules rules, int width, int height, bool hasPerCellWeights = false, IWFCExecutor executor = null, CompiledSemanticGraph semanticGraph = null) {
            if (width < 1 || height < 1) throw new ArgumentException("grid must be at least 1x1");
            _rules    = rules ?? throw new ArgumentNullException(nameof(rules));
            _width    = width;
            _height   = height;
            _tileCount = rules.count;
            _cellCount    = checked(width * height);
            _executor = executor ?? new ManagedIWFCExecutor();

            // NOTE: there's a fair few pins here, if there's a throw then it'll leak, so may be good to be more defensive
            _wordsPerDomain  = (_tileCount + 63) / 64;
            int domainWordCount = checked(_cellCount * _wordsPerDomain);

            _semanticGraph = semanticGraph;

            if (_semanticGraph != null)
            {
                if (_semanticGraph.TerrainCount != _tileCount ||
                    _semanticGraph.WordsPerNode != _wordsPerDomain)
                {
                    throw new ArgumentException(
                        "The semantic graph must match the solver's terrain count.");
                }

                if (_semanticGraph.RootId < 0 ||
                    _semanticGraph.RootId >= _semanticGraph.NodeCount)
                {
                    throw new ArgumentException(
                        "The semantic graph has an invalid root.");
                }
            }

            SemanticNodes = new int[_cellCount];

            int startingNode = _semanticGraph == null
                ? -1
                : _semanticGraph.RootId;

            Array.Fill(SemanticNodes, startingNode);

            int routeWords = 0;
            int routeDomainWords = 0;
            ulong[] routeSupport = null;
            if (_semanticGraph != null) {
                int routeCount = _semanticGraph.RouteTerrainIds.Length;
                routeWords = (routeCount - 1) / 64 + 1;
                routeDomainWords = checked(_cellCount * routeWords);
                routeSupport = SemanticRouteRules.Build(_semanticGraph, rules, routeWords);
                _initialSemanticNodes = new int[_cellCount];
                Array.Fill(_initialSemanticNodes, startingNode);
                _resultRoutes = new int[_cellCount];
                Array.Fill(_resultRoutes, -1);
            }

            Layout          = new int[_cellCount];
            _initialDomains = new ulong[domainWordCount];

            ResetInitialDomains();
            _hasPerCellWeights = hasPerCellWeights;

            int weightCount = hasPerCellWeights
                ? checked(_cellCount * _tileCount)
                : _tileCount;

            _args = new WFCKernelArgs {
                width               = width, height = height, tileCount = _tileCount, windowCount = rules.windowCount,
                weights             = Pin(rules.weight),
                supportMasks        = Pin(rules.support),
                windowTargets       = Pin(rules.windowCount == 0 ? new ulong[_wordsPerDomain] : rules.windowTargets),
                windowMarker        = Pin(rules.windowCount == 0 ? new int[1] : rules.windowMarker),
                windowFeasibleMasks = Pin(rules.windowFeasible),
                initialDomains      = Pin(_initialDomains),
                result              = Pin(Layout),
                resultInfo          = Pin(_resultInfo),
                wordsPerDomain      = _wordsPerDomain,
                domain              = Pin(new ulong[domainWordCount]),
                baseDomains         = Pin(new ulong[domainWordCount]),
                scratch             = Pin(new ulong[domainWordCount]),
                heapKeys            = Pin(new float[domainWordCount]),
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

            if (_semanticGraph != null)
            {
                _args.semanticChildOffsets = Pin(_semanticGraph.ChildOffsets);
                _args.semanticChildWeights = Pin(_semanticGraph.ChildWeights);
                _args.semanticLeafTerrainIds = Pin(_semanticGraph.LeafTerrainIds);
                _args.initialSemanticNodes = Pin(_initialSemanticNodes);
                _args.semanticNodes = Pin(SemanticNodes);
                _args.semanticRouteCount = _semanticGraph.RouteTerrainIds.Length;
                _args.semanticRouteWords = routeWords;
                _args.semanticRouteOffsets = Pin(_semanticGraph.RouteNodeOffsets);
                _args.semanticRouteNodes = Pin(_semanticGraph.RouteNodes);
                _args.semanticRouteEdges = Pin(_semanticGraph.RouteEdgeIndices);
                _args.semanticRouteTerrains = Pin(_semanticGraph.RouteTerrainIds);
                _args.semanticRouteSupport = Pin(routeSupport);
                _args.semanticRoutes = Pin(new ulong[routeDomainWords]);
                _args.baseSemanticRoutes = Pin(new ulong[routeDomainWords]);
                _args.semanticRouteScratch = Pin(new ulong[routeWords]);
                _args.semanticChoiceScratch = Pin(new byte[_semanticGraph.ChildWeights.Length]);
                _args.resultSemanticRoutes = Pin(_resultRoutes);
            }
        }

        /// <summary> Pins array and returns a pointer to its memory. Retained until solver is disposed. </summary>
        T* Pin<T>(T[] arr) where T : unmanaged {
            GCHandle h = GCHandle.Alloc(arr, GCHandleType.Pinned);
            _pinnedHandles.Add(h);
            return (T*)h.AddrOfPinnedObject();
        }

        /// <summary>
        /// Solves the grid using the supplied seed and optional constraints.
        /// Initial domains restrict which tiles may occupy each cell; cell weights
        /// bias tile selection independently per cell. Boundary constraints remove
        /// tiles whose MustExist neighbours would fall outside the grid.
        /// Window constraints can be disabled with useWindows.
        /// With a graph, initialSemanticNodes optionally specifies one starting node
        /// per cell. Use -1 for the default root. Concrete restrictions still apply.
        /// </summary>
        public WFCStatus Solve(ulong seed, ReadOnlySpan<ulong> initialDomains = default, ReadOnlySpan<float> cellWeights = default, int maxAttempts = 100, bool useWindows = true, ReadOnlySpan<int> initialSemanticNodes = default) {
            const string INIT_DOMAIN_LENGTH_MISMATCH = "initialDomains needs one entry per cell per domain word";
            const string CELL_WEIGHT_COUNT_MISMATCH  = "cellWeights needs Count entries per cell";
            const string SOLVER_CELL_WEIGHT_ERROR    = "This solver was not configured for per-cell weights";
            const float  MIN_TILE_WEIGHT             = 1e-4f;

            if (_isDisposed) {
                throw new ObjectDisposedException(nameof(WFCSolver));
            }

            if (!initialDomains.IsEmpty && initialDomains.Length != _initialDomains.Length) {
                throw new ArgumentException(INIT_DOMAIN_LENGTH_MISMATCH, nameof(initialDomains));
            }
            if (!initialSemanticNodes.IsEmpty) {
                if (_semanticGraph == null || initialSemanticNodes.Length != _cellCount)
                    throw new ArgumentException("Provide one starting node per cell and a semantic graph.", nameof(initialSemanticNodes));

                foreach (int node in initialSemanticNodes) {
                    if (node < -1 || node >= _semanticGraph.NodeCount)
                        throw new ArgumentOutOfRangeException(nameof(initialSemanticNodes), "Starting node IDs must exist in the semantic graph.");
                }
            }

            // config per cell weights
            if (_hasPerCellWeights) {
                if (cellWeights.Length != checked(_cellCount * _tileCount)) {
                    throw new ArgumentException(CELL_WEIGHT_COUNT_MISMATCH, nameof(cellWeights));
                }

                float* weights = _args.cellWeights;
                for (int i = 0; i < cellWeights.Length; i++) {
                    weights[i] = Math.Max(MIN_TILE_WEIGHT, cellWeights[i]);
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

            ApplyBoundary();

            if (_semanticGraph != null) {
                for (int cell = 0; cell < _cellCount; cell++) {
                    int node = initialSemanticNodes.IsEmpty ? -1 : initialSemanticNodes[cell];
                    _initialSemanticNodes[cell] = node < 0 ? _semanticGraph.RootId : node;
                }
                Array.Fill(_resultRoutes, -1);
            }

            // config kernel args
            _args.seed        = seed;
            _args.maxAttempts = maxAttempts;
            _args.windowCount = useWindows ? _rules.windowCount : 0;

            _resultInfo[0] = (int)WFCStatus.Failed;
            _resultInfo[1] = -1;
            _resultInfo[2] = 0;

            // execute
            _executor.Execute(ref _args);
            LastFailCell = _resultInfo[1];
            LastAttempts = _resultInfo[2];
            return (WFCStatus)_resultInfo[0];
        }

        /// <summary>
        /// Copies the resolved path from the cell's starting node to its leaf.
        /// Returns an empty array until a semantic solve has succeeded.
        /// </summary>
        public int[] GetSemanticPath(int cell) {
            if (_isDisposed) throw new ObjectDisposedException(nameof(WFCSolver));
            if (cell < 0 || cell >= _cellCount) throw new ArgumentOutOfRangeException(nameof(cell));
            if (_resultRoutes == null || _resultRoutes[cell] < 0) return Array.Empty<int>();

            int route = _resultRoutes[cell];
            int start = _semanticGraph.RouteNodeOffsets[route];
            int end = _semanticGraph.RouteNodeOffsets[route + 1];
            while (_semanticGraph.RouteNodes[start] != _initialSemanticNodes[cell]) start++;
            var path = new int[end - start];
            Array.Copy(_semanticGraph.RouteNodes, start, path, 0, path.Length);
            return path;
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
