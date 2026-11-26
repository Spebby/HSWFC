using System;
using System.Collections.Generic;
using UnityEngine;
using WFC.Core;


namespace WFC.Terrain {
    /// <summary>
    /// Utility class for compiling <see cref="SemanticGraphAsset"/> into <see cref="CompiledTerrain"/>.
    /// </summary>
    public static class TerrainCompiler {
        public static CompiledTerrain Compile(SemanticGraphAsset graph, bool logWarnings = true) {
            if (!graph) throw new ArgumentNullException(nameof(graph));

            List<TerrainDef> defs = graph.CollectTerrains();
            int              n    = defs.Count;
            if (n == 0) throw new InvalidOperationException("The semantic graph needs at least one leaf with a terrain.");
            int wordsPerDomain = (n + 63) / 64;

            CompiledTerrain c = new() {
                count          = n, defs = defs.ToArray(),
                baseVariants   = new Sprite[n][], overlayRank = new int[n],
                overlayTargets = new ulong[n * wordsPerDomain],
                overlayByMask  = new Sprite[n][][]
            };

            // Every terrain pair may touch. Where the graph declares a direction its declarations replace this, and
            // where it declares nothing the direction is unconstrained.
            WFCRulesBuilder b = new(n);
            for (int a = 0; a < n; a++) {
                for (int other = a; other < n; other++) b.AllowCardinal(a, other);
            }

            #region Render & Overlay Targets
            for (int t = 0; t < n; t++) {
                TerrainDef d = defs[t];
                c.baseVariants[t] = d.baseVariants;
                if (d.baseVariants == null || d.baseVariants.Length == 0) Warn($"{d.name} has no base sprites.");
                if (d.overlay.mode == OverlayMode.None) continue;

                // each tile target mask occupies WPD consecutive words.
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
            #endregion

            // overlay draw order: lower priority first
            int[] order                          = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;

            Array.Sort(order, (p, q) => defs[p].priority.CompareTo(defs[q].priority));
            for (int r = 0; r < n; r++) c.overlayRank[order[r]] = r;

            // enforce visual availability in rules
            for (int t = 0; t < n; t++) {
                TerrainDef d = defs[t];
                if (d.overlay.mode == OverlayMode.None || !(d.overlay.requireSides || d.overlay.requireCorners)) continue;

                int targetOffset = t * wordsPerDomain;
                if (!AnySet(c.overlayTargets, targetOffset, wordsPerDomain)) continue;

                bool[] renderable = d.overlay.Renderable();
                int    forbidden  = RenderableTables.CountForbidden(renderable);
                if (forbidden == 0) continue;

                b.AddWindow(t, new ReadOnlySpan<ulong>(c.overlayTargets, targetOffset, wordsPerDomain), renderable);
                Warn($"{d.name}: {forbidden} of 255 neighbour layouts need a mandatory overlay sprite that does not exist; the generator will avoid them.");
            }

            c.rules         = b.Build();
            c.semanticGraph = graph.Compile(defs);
            string problem = c.rules.Validate();
            if (problem != null) Warn(problem);
            return c;

            void Warn(string msg) {
                if (logWarnings) Debug.LogWarning($"[TerrainCompiler] {msg}", graph);
            }
        }

        static bool AnySet(ulong[] mask, int offset, int length) {
            for (int i = 0; i < length; i++) {
                if (mask[offset + i] != 0) return true;
            }

            return false;
        }
    }
}
