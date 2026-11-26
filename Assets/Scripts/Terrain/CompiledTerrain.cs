using UnityEngine;
using WFC.Core;

namespace WFC.Terrain {
    /// <summary>
    /// A semantic graph compiled for generation: the solver's rules and hierarchy, plus the lookup tables used to
    /// place sprites once a layout is solved. Terrain IDs are indices into <see cref="defs"/>.
    /// </summary>
    public class CompiledTerrain {
        public int count;
        public TerrainDef[] defs;

        // Terrain-level rules: the overlay windows. Adjacency comes from the graph.
        public WFCRules rules;
        public CompiledSemanticGraph semanticGraph;

        // Render data.
        public Sprite[][] baseVariants;
        public int[] overlayRank;          // Draw order: lower priority first.
        public ulong[] overlayTargets;     // [tile * wordsPerDomain] -> tiles it can overlay.
        public Sprite[][][] overlayByMask; // [overlay tile][raw 8-bit mask] -> sprites.
    }
}
