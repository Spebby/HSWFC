namespace WFC.Core {
    /// <summary>
    /// Validated graph data prepared for the solver. Treat arrays as read-only while solving.
    /// A route is one root-to-leaf path. Routes are the solver's tiles: each is a terrain seen in the
    /// context of the nodes above it, so a shared leaf reached through two parents is two routes.
    /// </summary>
    public class CompiledSemanticGraph {
        public int RootId;
        public int NodeCount;
        public int TerrainCount;

        // Direct outgoing edges for node n occupy [ChildOffsets[n], ChildOffsets[n + 1]).
        public int[] ChildOffsets;
        public int[] ChildIds;
        public float[] ChildWeights;

        // Non-leaf nodes have terrain ID -1.
        public int[] LeafTerrainIds;

        // Routes are emitted in depth-first order, children in edge order.
        public int[] RouteTerrainIds;

        // Route r's nodes occupy [RouteNodeOffsets[r], RouteNodeOffsets[r + 1]) of RouteNodes.
        public int[] RouteNodeOffsets;

        public int[] RouteNodes;

        // One incoming edge index per route node, or -1 for the root.
        public int[] RouteEdgeIndices;

        // Declared adjacency, validated and de-duplicated, with each opposite-direction declaration added.
        public SemanticAdjacency[] Adjacencies = System.Array.Empty<SemanticAdjacency>();

        // Preferred noise value per node and per route (NaN: no preference). A route takes the centre of the
        // deepest node on it that has one.
        public float[] NodeBiomeCenters;
        public float[] RouteBiomeCenters;

        public int RouteCount => RouteTerrainIds.Length;

        /// <summary>True when any node has a biome centre, so regional weights would change the result.</summary>
        public bool HasBiome { get; internal set; }
    }
}