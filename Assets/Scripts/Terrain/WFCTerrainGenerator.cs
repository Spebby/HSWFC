using System;
using UnityEngine;
using WFC.Burst;
using WFC.Core;


namespace WFC.Terrain {
    /// <summary>
    /// Thin driver that wraps a compiled TerrainSet, solves for it, then renders it.
    /// </summary>
    public class WFCTerrainGenerator : MonoBehaviour {
        public enum View {
            Final,
            Logic,
            Regions
        }

        [Header("Data")] public TerrainSet terrainSet;
        public float cellSize = 1f;

        [Header("Generation")] public int seed = 7;
        public int width = 64, height = 64;

        [Range(0f, 1f)] [Tooltip("How strongly the biome noise steers tile choice. 0 = off.")]
        public float regionalBias = 0.7f;

        [Min(1f)] public float biomeScale = 7f;
        [Min(1)] public int maxAttempts = 100;

        [Tooltip("Run the solver as a Burst job.")]
        public bool useBurst = true;

        [Tooltip("Keep mandatory overlay sprites renderable. If the solve fails with them, it is retried without.")]
        public bool enforceVisualConstraints = true;

        [Header("Rendering")]
        public TerrainRenderStrategy renderStrategy = TerrainRenderStrategy.ChunkMesh;
        [Tooltip("Chunk size, if set to -1, it will be a quarter of map size."), Min(-1)] public int meshChunkSize = -1;

        [Header("Display")] public View view = View.Final;
        public bool overlays = true;
        public bool autoFrameCamera = true;
        public bool regenerateOnValidate = true;

        /// <summary>Tile index per cell (y * width + x) of the last successful generation, otherwise null.</summary>
        public int[] Layout { get; private set; }

        public WFCStatus LastStatus { get; private set; }

        internal CompiledTerrain ct;
        WFCSolver _solver;
        ITerrainRenderer _renderer;
        Transform _root;
        Sprite _pixel;
        bool _dirty;
        internal float[] regionNoise; // saved for visualisation purposes. Not actually used by anything.

        void Start() {
            Generate();
        }

        void OnValidate() {
            if (regenerateOnValidate) _dirty = true;
        }

        void Update() {
            if (!_dirty || !Application.isPlaying) return;

            _dirty = false;
            Generate();
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.R)) { seed = UnityEngine.Random.Range(0, int.MaxValue); Generate(); }
#endif
        }

        void OnDestroy() {
            _solver?.Dispose();
            _renderer?.Dispose();
        }


        [ContextMenu("Generate")]
        public void Generate() {
            const ulong BIOME_SALT = 0xB5297A4D3F84D5B5UL;
            if (!terrainSet) {
                Debug.LogWarning($"{nameof(WFCTerrainGenerator)}: assign a TerrainSet");
                return;
            }

            width  = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            try {
                ct = terrainSet.Compile();
            } catch (Exception e) {
                Debug.LogError($"{nameof(WFCTerrainGenerator)}: {e.Message}", this);
                return;
            }

            // If solver already exists, dispose of it
            _solver?.Dispose();
            _solver = new WFCSolver(ct.rules, width, height, regionalBias > 0f, useBurst ? new BurstWFCExecutor() : new ManagedIWFCExecutor());

            ulong s = (uint)seed;
            regionNoise = BiomeWeights.Noise(width, height, Mathf.Max(0.5f, biomeScale), s ^ BIOME_SALT);
            // ^ save for visualiser. Not actually used for anything otherwise.

            float[] cw = regionalBias > 0f
                ? BiomeWeights.Build(width, height, s ^ BIOME_SALT, ct.weight, ct.biomeCenter, regionalBias, biomeScale)
                : null;

            WFCStatus status = _solver.Solve(s, default, cw, maxAttempts, enforceVisualConstraints);
            if (status != WFCStatus.Success && enforceVisualConstraints && ct.rules.windowCount > 0) {
                Debug.LogWarning($"{nameof(WFCTerrainGenerator)}: no layout satisfied the mandatory overlay sprites ({status}). Retrying without them", this);
                status = _solver.Solve(s, default, cw, maxAttempts, false);
            }

            LastStatus = status;
            ClearMap();
            if (status != WFCStatus.Success) {
                Layout = null;
                Debug.LogWarning($"{nameof(WFCTerrainGenerator)}: generation {status}{(_solver.LastFailCell >= 0 ? $" near cell {_solver.LastFailCell % width},{_solver.LastFailCell / width}" : "")}. Check adjacency and directional rules.", this);
                return;
            }

            Layout = (int[])_solver.Layout.Clone();
            Render();
            if (autoFrameCamera) FrameCamera();
        }

        public int TerrainAt(int x, int y) => Layout == null || WFCSolver.IsOutOfBounds(x, y, width, height) ? -1 : Layout[y * width + x];

        #region Rendering
        void Render() {
            _renderer?.Dispose();
            _renderer = CreateRenderer(renderStrategy);
            _renderer.Render(new TerrainRenderContext(this, _root));
        }

        ITerrainRenderer CreateRenderer(TerrainRenderStrategy strat) =>
            strat switch {
                TerrainRenderStrategy.ChunkMesh => new ChunkMeshTerrainRenderer(ResolveChunkSize(width, height, meshChunkSize)),
                TerrainRenderStrategy.Tilemap   => new TilemapTerrainRenderer(),
                _                               => throw new ArgumentOutOfRangeException(nameof(strat), strat, null)
            };

        static int ResolveChunkSize(int width, int height, int configuredSize) {
            if (configuredSize > 0) {
                return configuredSize;
            }

            const int TARGET_SHORT_AXIS = 2;

            int shortSide = Mathf.Min(width, height);
            return Mathf.Max(1, Mathf.CeilToInt((float)shortSide / TARGET_SHORT_AXIS));
        }

        void ClearMap() {
            _renderer?.Dispose();
            _renderer = null;

            if (_root) {
                if (Application.isPlaying) Destroy(_root.gameObject);
                else DestroyImmediate(_root.gameObject);
            }

            _root = new GameObject("Map").transform;
            _root.SetParent(transform, false);
        }

        void FrameCamera() {
            if (!autoFrameCamera) return;

            Camera cam = Camera.main;
            if (!cam) return;

            Vector3 centre = transform.position + new Vector3((width - 1) * 0.5f * cellSize, (height - 1) * 0.5f * cellSize, 0f);

            cam.orthographic       = true;
            cam.transform.position = new Vector3(centre.x, centre.y, -10f);
            cam.orthographicSize = Mathf.Max(
                height * cellSize * 0.5f + cellSize,
                (width * cellSize * 0.5f + cellSize) / Mathf.Max(0.1f, cam.aspect)
            );
        }
        #endregion
    }
}