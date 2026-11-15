using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WFC.Core;


namespace WFC.Terrain {
    public enum OverlayMode {
        None,    // never drawn as an overlay
        Edges16, // 15 side sprites (N/E/S/W combinations) + 4 lone-corner sprites
        Blob47   // one Blob47Set asset; each sprite already combines sides AND corners
    }

    /// <summary>Overlay sprite set: drawn on a lower-priority neighbour wherever this terrain borders it.</summary>
    [Serializable]
    public class OverlayArt {
        public OverlayMode mode = OverlayMode.None;

        [Tooltip("Edges16: index = N1 | E2 | S4 | W8. Index 0 unused.")]
        public Sprite[] edges = new Sprite[16];

        [Tooltip("Edges16 lone corners in order NE, SE, SW, NW.")]
        public Sprite[] corners = new Sprite[4];

        [Tooltip("Blob47: a single Blob47Set asset.")]
        public Blob47Set blob;

        [Header("Mandatory components")]
        [Tooltip("On: the generator never creates a neighbourhood that needs a side sprite this set does not have. Off: a missing side sprite is simply not drawn.")]
        public bool requireSides;

        [Tooltip("On: the generator never creates a neighbourhood that needs a corner sprite this set does not have. Off: a missing corner sprite is simply not drawn.")]
        public bool requireCorners;

        public void Fit() {
            if (edges is not { Length  : 16 }) Array.Resize(ref edges, 16);
            if (corners is not { Length: 4 }) Array.Resize(ref corners, 4);
        }

        public Sprite[] Resolve(int m) => OverlayResolver.Resolve(mode, edges, corners, blob ? blob.sprites : null, m);

        public bool AnyCorner() => corners != null && corners.Any(c => c);

        /// <summary>Which raw neighbour masks can be drawn, given the sprites that exist and the require flags.</summary>
        public bool[] Renderable() {
            switch (mode) {
                case OverlayMode.Edges16: {
                    int sides = 0, cor = 0;
                    for (int e = 1; e < 16 && edges != null && e < edges.Length; e++) {
                        if (!edges[e]) continue;
                        sides |= 1 << e;
                    }

                    for (int c = 0; c < 4 && corners != null && c < corners.Length; c++) {
                        if (!corners[c]) continue;
                        cor |= 1 << c;
                    }

                    return RenderableTables.Edge16(sides, cor, requireSides, requireCorners);
                }
                case OverlayMode.Blob47: {
                    ulong slots = 0;
                    var   sp    = blob ? blob.sprites : null;
                    for (int i = 1; i < OverlayMasks.BLOB_COUNT && sp != null && i < sp.Length; i++) {
                        if (!sp[i]) continue;
                        slots |= 1UL << i;
                    }

                    return RenderableTables.Blob47(slots, requireSides, requireCorners);
                }
                case OverlayMode.None:
                default: {
                    bool[] all                           = new bool[256];
                    for (int i = 0; i < 256; i++) all[i] = true;
                    return all;
                }
            }
        }
    }

    /// <summary>
    /// Semantic rule: the neighbour of this terrain in one direction must be one of the listed terrains, e.g. a door whose
    /// south neighbour must be road. With mustExist the terrain is never placed where that neighbour would be off the map.
    /// </summary>
    [Serializable]
    public class DirectionalRule {
        public Direction direction = Direction.S;

        [Tooltip("The only terrains allowed in that direction. Must not be empty.")]
        public List<TerrainDef> allowed = new();

        [Tooltip("The terrain needs a neighbour in that direction, so it cannot sit on the map border there.")]
        public bool mustExist = true;
    }

    /// <summary>
    /// Carries both logical rules and visual data for a given tile type. <see cref="TerrainSet"/> compiles the logical
    /// half into <see cref="WFCRules"/> and the visual half into render data, so the solver is not bothered with engine
    /// concerns.
    /// </summary>
    [CreateAssetMenu(menuName = "Terrain/Terrain Def", fileName = "Terrain_")]
    public class TerrainDef : ScriptableObject {
        [Header("Base tile (opaque, fills the cell)")]
        public Sprite[] baseVariants;

        public Color debugColor = Color.magenta;

        [Header("Logic: weights")] public float weight = 1f;

        [Range(0f, 1f)] [Tooltip("Where on the low-frequency biome noise this terrain prefers to appear.")]
        public float biomeCenter = 0.5f;

        [Header("Logic: adjacency")]
        [Tooltip("Terrains allowed to touch this one across cardinals (and if enabled) diagonals. A terrain may always touch itself.")]
        public List<TerrainDef> canNeighbor = new();

        [Tooltip("Directional rules, e.g. a door that needs road to its south.")]
        public List<DirectionalRule> rules = new();

        [Header("Overlay (drawn over lower-priority neighbours)")]
        public int priority;

        public OverlayArt overlay = new();
        public List<TerrainDef> noOverlayOnto = new();

        void OnValidate() {
            overlay ??= new OverlayArt();
            overlay.Fit();
        }
    }
}