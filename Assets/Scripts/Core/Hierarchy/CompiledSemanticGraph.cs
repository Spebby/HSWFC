using System;

namespace WFC.Core
{
    /// <summary>Validated graph data prepared for the solver. Treat arrays as read-only while solving.</summary>
    public class CompiledSemanticGraph
    {
        public int RootId;
        public int NodeCount;
        public int TerrainCount;
        public int WordsPerNode;

        // Direct outgoing edges for node n occupy [ChildOffsets[n], ChildOffsets[n + 1]).
        public int[] ChildOffsets;
        public int[] ChildIds;
        public float[] ChildWeights;

        // Non-leaf nodes have terrain ID -1. Masks are indexed by node, then word.
        public int[] LeafTerrainIds;
        public ulong[] DescendantMasks;

        // Routes are emitted in depth-first order. A shared leaf can occur on multiple routes.
        public int[] RouteTerrainIds;
        public int[] RouteNodeOffsets;
        public int[] RouteNodes;

        // One incoming edge index per route node, or -1 for the root.
        public int[] RouteEdgeIndices;

        // Explicit directional declarations, including each opposite-direction reciprocal.
        public SemanticAdjacency[] Adjacencies = Array.Empty<SemanticAdjacency>();
    }
}
