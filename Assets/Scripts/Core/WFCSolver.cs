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
        public readonly WFCRules rules;
        public readonly int width, height;
        public readonly IWFCExecutor executor;

        readonly int _tileCounts, _cellCount;
        readonly List<GCHandle> _pinnedHandles = new();
        readonly ulong[] _initialDomains;
        readonly int[] _resultInfo = new int[3];
        float[] _cellWeightsBuffer;
        WFCKernelArgs _args;
        bool _isDisposed;

        /// <summary>Tile index per cell, index = y * Width + x. Overwritten by every successful Solve.</summary>
        public byte[] Layout { get; }

        /// <summary>Number of search attempts used by the last Solve.</summary>
        public int LastAttempts { get; private set; }

        /// <summary>Cell where the last failure occurred, or -1 if none.</summary>
        public int LastFailCell { get; private set; }

        /// <summary>
        /// Creates solver for the rules and grid dimensions. Allocates and pins the kernel's working buffers.
        /// </summary>
        public WFCSolver(WFCRules rules, int width, int height, IWFCExecutor executor = null) {
            if (width < 1 || height < 1) throw new ArgumentException("grid must be at least 1x1");
            this.rules    = rules ?? throw new ArgumentNullException(nameof(rules));
            this.width    = width;
            this.height   = height;
            _tileCounts   = rules.count;
            _cellCount    = width * height;
            this.executor = executor ?? new ManagedIWFCExecutor();

            Layout = new byte[_cellCount];
            _initialDomains = new ulong[_cellCount];
            _args = new WFCKernelArgs {
                width               = width, height = height, tileCount = _tileCounts, windowCount = rules.windowCount,
                weights             = Pin(rules.weight),
                supportMasks        = Pin(rules.support),
                windowTargets       = Pin(rules.windowCount == 0 ? new ulong[1] : rules.windowTargets),
                windowMarker        = Pin(rules.windowCount == 0 ? new byte[1] : rules.windowMarker),
                windowFeasibleMasks = Pin(rules.windowFeasible),
                initialDomains      = Pin(_initialDomains),
                result              = Pin(Layout),
                resultInfo          = Pin(_resultInfo),
                domain              = Pin(new ulong[_cellCount]),
                baseDomains         = Pin(new ulong[_cellCount]),
                heapKeys            = Pin(new float[_cellCount]),
                heap                = Pin(new int[_cellCount]),
                heapPos             = Pin(new int[_cellCount]),
                queue               = Pin(new int[2 * _cellCount + 1]),
                changedCells        = Pin(new int[_cellCount]),
                queuedFlags         = Pin(new byte[2 * _cellCount]),
                changedFlags        = Pin(new byte[_cellCount]),
                weightLogWeights    = Pin(new float[_tileCounts])
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
        /// Initial domains restrict which tiles may occupy each cell; cell weights
        /// bias tile selection independently per cell. Boundary constraints remove
        /// tiles whose MustExist neighbours would fall outside the grid.
        /// Window constraints can be disabled with useWindows.
        /// </summary>
        public WFCStatus Solve(ulong seed, ReadOnlySpan<ulong> initialDomains = default, ReadOnlySpan<float> cellWeights = default, int maxAttempts = 100, bool useWindows = true) {
            const string INIT_DOMAIN_IS_EMPTY       = "initialDomains needs one entry per cell";
            const string CELL_WEIGHT_COUNT_MISMATCH = "cellWeights needs Count entries per cell";
            const float  MIN_TILE_WEIGHT            = 1e-4f;

            if (_isDisposed) throw new ObjectDisposedException(nameof(WFCSolver));
            if (!initialDomains.IsEmpty && initialDomains.Length != _cellCount) throw new ArgumentException(INIT_DOMAIN_IS_EMPTY);
            if (!cellWeights.IsEmpty && cellWeights.Length != _cellCount * _tileCounts) throw new ArgumentException(CELL_WEIGHT_COUNT_MISMATCH);
            // Shouldn't happen under normal execution unless it's being called incorrectly

            ulong fullDomain                                        = rules.FullMask;
            for (int i = 0; i < _cellCount; i++) _initialDomains[i] = initialDomains.IsEmpty ? fullDomain : initialDomains[i];
            ApplyBoundary();

            _args.hasPerCellWeights = 0;
            if (!cellWeights.IsEmpty) {
                int weightCount = _cellCount * _tileCounts;
                if (_cellWeightsBuffer == null) {
                    _cellWeightsBuffer = new float[weightCount];
                    GCHandle h = GCHandle.Alloc(_cellWeightsBuffer, GCHandleType.Pinned);
                    _pinnedHandles.Add(h);
                    _args.cellWeights = (float*)h.AddrOfPinnedObject();
                    _args.weightLogWeights = Pin(new float[weightCount]);
                }

                for (int i = 0; i < weightCount; i++) _cellWeightsBuffer[i] = Math.Max(MIN_TILE_WEIGHT, cellWeights[i]);
                _args.hasPerCellWeights = 1;
            }

            _args.seed        = seed;
            _args.maxAttempts = maxAttempts;
            _args.windowCount = useWindows ? rules.windowCount : 0;
            _resultInfo[0]       = (int)WFCStatus.Failed;
            _resultInfo[1]       = -1;
            _resultInfo[2]       =  0;

            executor.Execute(ref _args);

            LastFailCell = _resultInfo[1];
            LastAttempts = _resultInfo[2];
            return (WFCStatus)_resultInfo[0];
        }

        /// <summary>
        /// Removes tiles from boundary cells when their mandatory neighbours would extend beyond the grid.
        /// </summary>
        void ApplyBoundary() {
            ulong[] mustExistMask = rules.mustExist;
            bool    hasBoundaryConstraints  = false;
            for (int d = 0; d < 8; d++) {
                if (mustExistMask[d] != 0) hasBoundaryConstraints = true;
            }

            if (!hasBoundaryConstraints) return;
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    if (!IsBoundaryCell(x, y)) continue;
                    ulong allowedTiles = ulong.MaxValue;
                    for (int d = 0; d < 8; d++) {
                        int nx = x + Dirs.Dx(d);
                        int ny = y + Dirs.Dy(d);
                        if (IsOutOfBounds(nx, ny, width, height)) allowedTiles &= ~mustExistMask[d];
                    }

                    _initialDomains[y * width + x] &= allowedTiles;
                }
            }
            return;

            bool IsBoundaryCell(int x, int y) => x == 0 || y == 0 || x == width - 1 || y == height - 1;
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