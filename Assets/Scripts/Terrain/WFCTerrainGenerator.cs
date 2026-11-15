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

        [Header("Display")] public View view = View.Final;
        public bool overlays = true;
        public bool autoFrameCamera = true;
        public bool regenerateOnValidate = true;

        /// <summary>Tile index per cell (y * width + x) of the last successful generation, otherwise null.</summary>
        public byte[] Layout { get; private set; }

        public WFCStatus LastStatus { get; private set; }

        CompiledTerrain _ct;
        WFCSolver _solver;
        Transform _root;
        Sprite _pixel;
        bool _dirty;
        float[] _regionNoise; // saved for visualisation purposes. Not actually used by anything.

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

        void OnDestroy() => _solver?.Dispose();


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
                _ct = terrainSet.Compile();
            } catch (Exception e) {
                Debug.LogError($"{nameof(WFCTerrainGenerator)}: {e.Message}", this);
                return;
            }

            // If solver already exists, dispose of it
            _solver?.Dispose();
            _solver = new WFCSolver(_ct.rules, width, height, useBurst ? new BurstWFCExecutor() : new ManagedIWFCExecutor());

            ulong s = (uint)seed;
            _regionNoise = BiomeWeights.Noise(width, height, Mathf.Max(0.5f, biomeScale), s ^ BIOME_SALT);
            // ^ save for visualiser. Not actually used for anything otherwise.

            float[] cw = regionalBias > 0f
                ? BiomeWeights.Build(width, height, s ^ BIOME_SALT, _ct.weight, _ct.biomeCenter, regionalBias, biomeScale)
                : null;

            WFCStatus status = _solver.Solve(s, default, cw, maxAttempts, enforceVisualConstraints);
            if (status != WFCStatus.Success && enforceVisualConstraints && _ct.rules.windowCount > 0) {
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

            Layout = (byte[])_solver.Layout.Clone();
            Render();
            if (autoFrameCamera) FrameCamera();
        }

        public int TerrainAt(int x, int y) => Layout == null || x < 0 || y < 0 || x >= width || y >= height ? -1 : Layout[y * width + x];

        // ------------------------------------------------------------------ rendering
        void ClearMap() {
            if (_root) {
                if (Application.isPlaying) Destroy(_root.gameObject);
                else DestroyImmediate(_root.gameObject);
            }

            _root = new GameObject("Map").transform;
            _root.SetParent(transform, false);
        }

        void Render() {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int t = Layout[y * width + x];
                    switch (view) {
                        case View.Logic:
                            PutFlat(x, y, _ct.defs[t].debugColor, 0);
                            continue;
                        case View.Regions: {
                            float noise = _regionNoise[y * width + x];
                            PutFlat(x, y, new Color(noise, noise, noise), 0);
                            continue;
                        }
                        case View.Final:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    Sprite sp = Pick(_ct.baseVariants[t], x, y, 1);
                    if (sp) PutBox(sp, x, y, 0);
                    else PutFlat(x, y, _ct.defs[t].debugColor, 0);
                }
            }

            if (view == View.Final && overlays) RenderOverlays();
        }

        // The mask of overlay tile o around a cell has bit d set when the neighbour in direction d is o. Edges16 stacks several
        // pieces for one mask, Blob47 gives one.
        void RenderOverlays() {
            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int t = Layout[y * width + x];
                    for (int o = 0; o < _ct.count; o++) {
                        if (((_ct.overlayTargets[o] >> t) & 1UL) == 0) continue;
                        int mask = 0;
                        for (int d = 0; d < 8; d++) {
                            if (TerrainAt(x + Dirs.Dx(d), y + Dirs.Dy(d)) == o)
                                mask |= 1 << d;
                        }

                        if (mask == 0) continue;
                        foreach (Sprite sp in _ct.overlayByMask[o][mask]) PutBox(sp, x, y, 1 + _ct.overlayRank[o]);
                    }
                }
            }
        }

        // Sprite pivot placed so the sprite fills its cell like a tile.
        void PutBox(Sprite sp, int x, int y, int order) {
            GameObject go = new("s");
            go.transform.SetParent(_root, false);
            SpriteRenderer r = go.AddComponent<SpriteRenderer>();
            r.sprite       = sp;
            r.sortingOrder = order;
            float   ppu  = sp.pixelsPerUnit;
            Vector2 size = sp.rect.size / ppu, pivot = sp.pivot / ppu;
            go.transform.localPosition = new Vector3(x * cellSize + (pivot.x - size.x * 0.5f) * (cellSize / size.x),
                                                     y * cellSize + (pivot.y - size.y * 0.5f) * (cellSize / size.y),
                                                     0f);
            go.transform.localScale = new Vector3(cellSize / size.x, cellSize / size.y, 1f);
        }

        void PutFlat(int x, int y, Color color, int order) {
            if (!_pixel) {
                Texture2D tex = new(1, 1) { filterMode = FilterMode.Point };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _pixel = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }

            GameObject go = new("f");
            go.transform.SetParent(_root, false);
            SpriteRenderer r = go.AddComponent<SpriteRenderer>();
            r.sprite                   = _pixel;
            r.color                    = color;
            r.sortingOrder             = order;
            go.transform.localPosition = new Vector3(x * cellSize, y * cellSize, 0f);
            go.transform.localScale    = new Vector3(cellSize, cellSize, 1f);
        }

        void FrameCamera() {
            if (!autoFrameCamera) return;

            Camera cam = Camera.main;
            if (!cam) return;

            Vector3 centre = transform.position +
                             new Vector3((width - 1) * 0.5f * cellSize, (height - 1) * 0.5f * cellSize, 0f);

            cam.orthographic       = true;
            cam.transform.position = new Vector3(centre.x, centre.y, -10f);
            cam.orthographicSize = Mathf.Max(
                height * cellSize * 0.5f + cellSize,
                (width * cellSize * 0.5f + cellSize) / Mathf.Max(0.1f, cam.aspect)
            );
        }

        static Sprite Pick(Sprite[] arr, int x, int y, int salt) {
            if (arr == null || arr.Length == 0) return null;
            return arr[(int)(hash_coord(x, y, salt) % (uint)arr.Length)];
        }

        /// <remarks>
        /// This exists, rather than using a built-in func to have deterministic pseudo-random vals w/o maintaining random state.
        /// Ensures that sprite selection remains consistent for a given tile coordinate and salt, independent of generation order.
        /// This code was generated by an LLM. Appears to be based on Dwarf Fortress' TWBT coord_hash function.
        /// </remarks>
        static uint hash_coord(int a, int b, int c) {
            unchecked {
                uint h = (uint)(a * 374761393 + b * 668265263 + c * 1013904223);
                h = (h ^ (h >> 13)) * 1274126177u;
                return h ^ (h >> 16);
            }
        }
    }
}
