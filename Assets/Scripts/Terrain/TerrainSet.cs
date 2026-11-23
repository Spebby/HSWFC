using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WFC.Core;


namespace WFC.Terrain {
    public class CompiledTerrain {
        public int count;
        public TerrainDef[] defs;
        public WFCRules rules;
        public CompiledSemanticGraph semanticGraph;

        // Logic inputs for BiomeWeights.
        public float[] weight, biomeCenter;

        // Render data.
        public Sprite[][] baseVariants;
        public int[] overlayRank;          // Draw order: lower priority first.
        public ulong[] overlayTargets;     // [tile * wordsPerDomain] -> tiles it can overlay.
        public Sprite[][][] overlayByMask; // [overlay tile][raw 8-bit mask] -> sprites.
    }

    /// <summary>
    /// Compiles <see cref="TerrainDef"/> into <see cref="WFCRules"/>, and creates lookup tables for sprite placement
    /// after solving.
    /// </summary>
    [CreateAssetMenu(menuName = "Terrain/Terrain Set", fileName = "TerrainSet")]
    public class TerrainSet : ScriptableObject {
        [SerializeField] public List<TerrainDef> terrains = new();
        [Tooltip("Optional semantic hierarchy for generation. Leaves reference terrains in this set.")]
        public SemanticGraphAsset semanticGraph;

        [Tooltip("Off: diagonal neighbours are unconstrained. On: every terrain may only touch itself or its diagonalNeighbor entries diagonally. Never derived from canNeighbor.")]
        public bool useDiagonalRules;

        [ContextMenu("Validate")]
        void ValidateMenu() {
            Compile();
        }

        // ReSharper disable once HeapView.ClosureAllocation
        public CompiledTerrain Compile(bool logWarnings = true) {
            List<TerrainDef> defs = terrains.FindAll(t => t);
            int              n    = defs.Count;
            if (n == 0) throw new InvalidOperationException("TerrainSet needs at least one terrain.");

            // Domains are stored as one or more 64-bit words per tile.
            int wordsPerDomain = (n + 63) / 64;

            Dictionary<TerrainDef, int> idx          = new();
            for (int i = 0; i < n; i++) idx[defs[i]] = i;

            CompiledTerrain c = new() {
                count          = n, defs = defs.ToArray(), weight = new float[n], biomeCenter = new float[n],
                baseVariants   = new Sprite[n][], overlayRank = new int[n],
                overlayTargets = new ulong[n * wordsPerDomain],
                overlayByMask  = new Sprite[n][][]
            };
            WFCRulesBuilder b = new(n) { diagonalRules = useDiagonalRules };

            // ---- logic: weights, adjacency, semantic rules
            for (int t = 0; t < n; t++) {
                TerrainDef d = defs[t];
                c.weight[t]      = Mathf.Max(0.0001f, d.weight);
                c.biomeCenter[t] = d.biomeCenter;
                b.SetWeight(t, d.weight);

                // I'm fine w/ LINQ here because this is not hot path
                foreach (TerrainDef other in d.canNeighbor.Where(other => other)) {
                    if (idx.TryGetValue(other, out int bi)) b.AllowCardinal(t, bi);
                    else Warn($"{d.name} lists {other.name} which is not in the set.");
                }

                if (useDiagonalRules) {
                    foreach (TerrainDef other in d.canNeighbor.Where(other => other)) {
                        if (idx.TryGetValue(other, out int bi)) b.AllowDiagonal(t, bi);
                        else Warn($"{d.name} lists {other.name} (diagonal) which is not in the set.");
                    }
                }

                foreach (DirectionalRule r in d.rules) {
                    if (r == null) continue;
                    ulong[] mask = MaskOf(r.allowed, d.name);
                    if (!Any(mask)) {
                        Warn($"{d.name} has a {r.direction} rule with no valid allowed terrains; it is ignored.");
                        continue;
                    }

                    b.Restrict(t, r.direction, mask, r.mustExist);
                }
            }

            // ---- render data and overlay targets
            for (int t = 0; t < n; t++) {
                TerrainDef d = defs[t];
                c.baseVariants[t] = d.baseVariants;
                if (d.baseVariants == null || d.baseVariants.Length == 0) Warn($"{d.name} has no base sprites.");
                if (d.overlay.mode == OverlayMode.None) continue;

                // Each tile's target mask occupies wordsPerDomain consecutive words.
                int targetOffset = t * wordsPerDomain;
                for (int u = 0; u < n; u++) {
                    if (u != t && d.priority > defs[u].priority && !d.noOverlayOnto.Contains(defs[u])) {
                        c.overlayTargets[targetOffset + (u >> 6)] |= 1UL << (u & 63);
                    }
                }

                c.overlayByMask[t] = new Sprite[256][];
                for (int m = 0; m < 256; m++) c.overlayByMask[t][m] = d.overlay.Resolve(m);

                if (d.overlay.mode != OverlayMode.Blob47 || d.overlay.blob) continue;
                Warn($"{d.name} uses Blob47 overlays but has no Blob47Set.");
            }

            // overlay draw order: lower priority first
            int[] order                          = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;

            // ReSharper disable once HeapView.ImplicitCapture
            Array.Sort(order, (p, q) => defs[p].priority.CompareTo(defs[q].priority));
            for (int r = 0; r < n; r++) c.overlayRank[order[r]] = r;

            // ---- visual availability: mandatory overlay components become neighbourhood constraints
            for (int t = 0; t < n; t++) {
                TerrainDef d = defs[t];
                if (d.overlay.mode == OverlayMode.None ||
                    !(d.overlay.requireSides || d.overlay.requireCorners)) continue;

                int targetOffset = t * wordsPerDomain;
                if (!Any2(c.overlayTargets, targetOffset, wordsPerDomain)) continue;

                bool[] renderable = d.overlay.Renderable();
                int    forbidden  = RenderableTables.CountForbidden(renderable);
                if (forbidden == 0) continue;

                b.AddWindow(t, new ReadOnlySpan<ulong>(c.overlayTargets, targetOffset, wordsPerDomain), renderable);
                Warn($"{d.name}: {forbidden} of 255 neighbour layouts need a mandatory overlay sprite that does not exist; the generator will avoid them.");
            }

            c.rules = b.Build();
            if (semanticGraph) c.semanticGraph = semanticGraph.Compile(c.defs);
            string problem = c.rules.Validate();
            if (problem != null) Warn(problem);
            return c;

            void Warn(string msg) {
                if (logWarnings) Debug.LogWarning($"[TerrainSet] {msg}", this);
            }

            ulong[] MaskOf(List<TerrainDef> list, string owner) {
                ulong[] mask = new ulong[wordsPerDomain];
                foreach (TerrainDef td in list.Where(td => td)) {
                    if (idx.TryGetValue(td, out int bi)) mask[bi >> 6] |= 1UL << (bi & 63);
                    else Warn($"{owner} lists {td.name} which is not in the set.");
                }

                return mask;
            }

            bool Any(ulong[] mask) => mask.Any(word => word != 0);

            bool Any2(ulong[] mask, int offset, int length) {
                for (int i = 0; i < length; i++) {
                    if (mask[offset + i] != 0) return true;
                }

                return false;
            }
        }
    }
}
