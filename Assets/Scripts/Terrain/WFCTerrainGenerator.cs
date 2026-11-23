using System;
using System.Collections.Generic;
using UnityEngine;
using WFC.Burst;
using WFC.Core;


namespace WFC.Terrain {
    /// <summary>
    /// Thin driver that wraps a compiled TerrainSet, solves for it, then renders it.
    /// </summary>
    public class WFCTerrainGenerator : MonoBehaviour {
        [Serializable]
        public struct SemanticPlacement {
            public Vector2Int cell;
            [Min(0)] public int nodeId;
        }

        [Serializable]
        public struct SemanticRegion {
            public Vector2Int origin;
            public Vector2Int size;
            [Min(0)] public int nodeId;
        }

        public enum View {
            Final,
            Logic,
            Regions,
            SemanticPaths
        }

        [Header("Data")] public TerrainSet terrainSet;
        public float cellSize = 1f;
        [Tooltip("Optional initial meta-tiles or leaves, using node indices from the TerrainSet's graph.")]
        public List<SemanticPlacement> semanticPlacements = new();
        [Tooltip("Optional rectangular starting zones, using node indices from the TerrainSet's graph.")]
        public List<SemanticRegion> semanticRegions = new();

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
        bool _dirty;
        bool _semanticViewDirty;
        int _semanticStep, _lastSemanticStep;
        int[][] _semanticPaths;
        readonly SortedSet<int> _visibleSemanticNodes = new();
        int _layoutWidth, _layoutHeight;
        internal float[] regionNoise; // saved for visualisation purposes. Not actually used by anything.

        void Start() {
            Generate();
        }

        void OnValidate() {
            if (regenerateOnValidate) _dirty = true;
        }

        void Update() {
            if (!Application.isPlaying) return;
            if (!_dirty) {
                if (_semanticViewDirty && Layout != null) {
                    _semanticViewDirty = false;
                    Render();
                }
                return;
            }

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
            int[] initialSemanticNodes;
            try {
                CompiledTerrain compiled = terrainSet.Compile();
                initialSemanticNodes = BuildInitialSemanticNodes(compiled.semanticGraph);
                var solver = new WFCSolver(compiled.rules, width, height, regionalBias > 0f,
                    useBurst ? new BurstWFCExecutor() : new ManagedIWFCExecutor(), compiled.semanticGraph);

                _solver?.Dispose();
                _solver = solver;
                ct = compiled;
            } catch (Exception e) {
                Debug.LogError($"{nameof(WFCTerrainGenerator)}: {e.Message}", this);
                return;
            }

            ulong s = (uint)seed;
            regionNoise = BiomeWeights.Noise(width, height, Mathf.Max(0.5f, biomeScale), s ^ BIOME_SALT);
            // ^ save for visualiser. Not actually used for anything otherwise.

            float[] cw = regionalBias > 0f
                ? BiomeWeights.Build(width, height, s ^ BIOME_SALT, ct.weight, ct.biomeCenter, regionalBias, biomeScale)
                : null;

            WFCStatus status = _solver.Solve(s, default, cw, maxAttempts, enforceVisualConstraints, initialSemanticNodes);
            if (status != WFCStatus.Success && enforceVisualConstraints && ct.rules.windowCount > 0) {
                Debug.LogWarning($"{nameof(WFCTerrainGenerator)}: no layout satisfied the mandatory overlay sprites ({status}). Retrying without them", this);
                status = _solver.Solve(s, default, cw, maxAttempts, false, initialSemanticNodes);
            }

            LastStatus = status;
            _semanticPaths = null;
            _semanticStep = 0;
            _semanticViewDirty = false;
            ClearMap();
            if (status != WFCStatus.Success) {
                Layout = null;
                Debug.LogWarning($"{nameof(WFCTerrainGenerator)}: generation {status}{(_solver.LastFailCell >= 0 ? $" near cell {_solver.LastFailCell % width},{_solver.LastFailCell / width}" : "")}. Check adjacency and directional rules.", this);
                return;
            }

            Layout = (int[])_solver.Layout.Clone();
            _layoutWidth = width;
            _layoutHeight = height;
            Render();
            if (autoFrameCamera) FrameCamera();
        }

        public int TerrainAt(int x, int y) => Layout == null ||
            WFCSolver.IsOutOfBounds(x, y, _layoutWidth, _layoutHeight) ? -1 : Layout[y * _layoutWidth + x];

        /// <summary>Returns the semantic path at a cell of the generated map, or an empty array if unavailable.</summary>
        public int[] GetSemanticPath(int x, int y) {
            if (Layout == null || ct?.semanticGraph == null || WFCSolver.IsOutOfBounds(x, y, _layoutWidth, _layoutHeight))
                return Array.Empty<int>();

            return _solver.GetSemanticPath(y * _layoutWidth + x);
        }

        void PrepareSemanticView() {
            _visibleSemanticNodes.Clear();
            if (view != View.SemanticPaths || ct.semanticGraph == null) return;

            if (_semanticPaths == null) {
                _semanticPaths = new int[Layout.Length][];
                _lastSemanticStep = 0;
                for (int cell = 0; cell < _semanticPaths.Length; cell++) {
                    int[] path = _solver.GetSemanticPath(cell);
                    _semanticPaths[cell] = path;
                    _lastSemanticStep = Math.Max(_lastSemanticStep, path.Length - 1);
                }
            }
        }

        internal Color SemanticColorAt(int cell) {
            if (_semanticPaths == null) return ct.defs[Layout[cell]].debugColor;

            int[] path = _semanticPaths[cell];
            int node = path[Math.Min(_semanticStep, path.Length - 1)];
            _visibleSemanticNodes.Add(node);
            return SemanticNodeColor(node);
        }

        Color SemanticNodeColor(int node) {
            var graph = terrainSet ? terrainSet.semanticGraph : null;
            if (graph && node < graph.nodes.Count) {
                SemanticGraphAsset.Node entry = graph.nodes[node];
                if (entry != null && entry.overridePreviewColor) {
                    Color color = entry.previewColor;
                    color.a = 1f;
                    return color;
                }
            }

            if (node == ct.semanticGraph.RootId) return new Color(0.36f, 0.39f, 0.43f);
            int terrain = ct.semanticGraph.LeafTerrainIds[node];
            return terrain >= 0
                ? ct.defs[terrain].debugColor
                : Color.HSVToRGB(Mathf.Repeat(node * 0.618034f, 1f), 0.68f, 0.84f);
        }

        void OnGUI() {
            if (view != View.SemanticPaths || Layout == null || _semanticPaths == null) return;

            int legendRows = Math.Min(_visibleSemanticNodes.Count, 9);
            GUILayout.BeginArea(new Rect(12, 12, 245, 55 + 23 * legendRows), GUI.skin.box);
            GUILayout.Label($"Collapse paths · step {_semanticStep} of {_lastSemanticStep}");
            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && _semanticStep > 0;
            if (GUILayout.Button("Previous")) SetSemanticStep(_semanticStep - 1);
            GUI.enabled = wasEnabled && _semanticStep < _lastSemanticStep;
            if (GUILayout.Button("Next")) SetSemanticStep(_semanticStep + 1);
            GUI.enabled = wasEnabled;
            GUILayout.EndHorizontal();

            int shown = 0;
            foreach (int node in _visibleSemanticNodes) {
                if (shown++ == 8) {
                    GUILayout.Label($"and {_visibleSemanticNodes.Count - 8} more");
                    break;
                }
                var graph = terrainSet ? terrainSet.semanticGraph : null;
                string label = graph && node < graph.nodes.Count ? graph.nodes[node]?.name : null;
                GUILayout.BeginHorizontal();
                Rect swatch = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
                if (Event.current.type == EventType.Repaint) {
                    Color previous = GUI.color;
                    GUI.color = SemanticNodeColor(node);
                    GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                    GUI.color = previous;
                }
                GUILayout.Label(string.IsNullOrEmpty(label) ? $"Node {node}" : label);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndArea();
        }

        void SetSemanticStep(int step) {
            _semanticStep = step;
            _semanticViewDirty = true;
        }

        int[] BuildInitialSemanticNodes(CompiledSemanticGraph graph) {
            if (semanticPlacements.Count == 0 && semanticRegions.Count == 0) return null;
            if (graph == null) throw new InvalidOperationException("Semantic placements and regions require a semantic graph on the TerrainSet.");

            int[] nodes = new int[checked(width * height)];
            Array.Fill(nodes, -1); // Unspecified cells begin at the root.
            foreach (SemanticRegion region in semanticRegions) {
                Vector2Int origin = region.origin;
                Vector2Int size = region.size;
                if (size.x <= 0 || size.y <= 0 || origin.x < 0 || origin.y < 0 ||
                    (long)origin.x + size.x > width || (long)origin.y + size.y > height)
                    throw new InvalidOperationException($"Semantic region at {origin} with size {size} must fit within the grid.");

                for (int y = origin.y; y < origin.y + size.y; y++) {
                    for (int x = origin.x; x < origin.x + size.x; x++)
                        SetStartingNode(new Vector2Int(x, y), region.nodeId);
                }
            }

            foreach (SemanticPlacement placement in semanticPlacements) {
                Vector2Int cell = placement.cell;
                if (WFCSolver.IsOutOfBounds(cell.x, cell.y, width, height))
                    throw new InvalidOperationException($"Semantic placement {cell} is outside the grid.");
                SetStartingNode(cell, placement.nodeId);
            }

            return nodes;

            void SetStartingNode(Vector2Int cell, int nodeId) {
                if (nodeId < 0 || nodeId >= graph.NodeCount)
                    throw new InvalidOperationException($"Semantic cell {cell} refers to unknown node {nodeId}.");

                int index = cell.y * width + cell.x;
                if (nodes[index] != -1 && nodes[index] != nodeId)
                    throw new InvalidOperationException($"Semantic cell {cell} has two different starting nodes.");
                nodes[index] = nodeId;
            }
        }

        #region Rendering
        void Render() {
            PrepareSemanticView();
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
