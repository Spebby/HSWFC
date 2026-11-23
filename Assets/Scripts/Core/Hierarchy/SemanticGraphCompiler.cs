using System;
using System.Collections.Generic;

namespace WFC.Core
{
    public static class SemanticGraphCompiler
    {
        // Route counts can grow exponentially with shared descendants. Check sizes
        // before expanding paths so an oversized authored graph fails predictably.
        public const int MaxRouteCount = 16_384;
        public const int MaxRouteNodeCount = 1_048_576;

        public static ulong[] BuildLeafMasks(SemanticGraph graph, int terrainCount)
        {
            int[] order = Validate(graph, terrainCount, out _);
            return BuildLeafMasks(graph, (terrainCount - 1) / 64 + 1, order);
        }

        public static CompiledSemanticGraph Compile(SemanticGraph graph, int terrainCount)
        {
            int[] order = Validate(graph, terrainCount, out int edgeCount);
            int nodeCount = graph.Nodes.Count;
            int wordsPerNode = (terrainCount - 1) / 64 + 1;
            var compiled = new CompiledSemanticGraph
            {
                RootId = graph.RootId,
                NodeCount = nodeCount,
                TerrainCount = terrainCount,
                WordsPerNode = wordsPerNode,
                ChildOffsets = new int[nodeCount + 1],
                ChildIds = new int[edgeCount],
                ChildWeights = new float[edgeCount],
                LeafTerrainIds = new int[nodeCount],
                DescendantMasks = BuildLeafMasks(graph, wordsPerNode, order),
                Adjacencies = CompileAdjacencies(graph)
            };

            int edgeIndex = 0;
            for (int nodeId = 0; nodeId < nodeCount; nodeId++)
            {
                SemanticNode node = graph.Nodes[nodeId];
                compiled.ChildOffsets[nodeId] = edgeIndex;
                compiled.LeafTerrainIds[nodeId] = node.TerrainId;

                foreach (SemanticEdge edge in node.Children)
                {
                    compiled.ChildIds[edgeIndex] = edge.ChildId;
                    compiled.ChildWeights[edgeIndex] = edge.Weight;
                    edgeIndex++;
                }
            }

            compiled.ChildOffsets[nodeCount] = edgeIndex;
            BuildRoutes(compiled, order);
            return compiled;
        }

        private static int[] Validate(SemanticGraph graph, int terrainCount, out int edgeCount)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (terrainCount < 1) throw new ArgumentOutOfRangeException(nameof(terrainCount));
            if (!graph.HasValidReferences())
                throw new ArgumentException("The graph contains invalid node or edge references.", nameof(graph));
            if (!graph.TryGetTopologicalOrder(out int[] order))
                throw new ArgumentException("The graph contains a cycle.", nameof(graph));
            if (!graph.AllNodesReachableFromRoot())
                throw new ArgumentException("Every node must be reachable from the root.", nameof(graph));

            edgeCount = 0;
            for (int nodeId = 0; nodeId < graph.Nodes.Count; nodeId++)
            {
                SemanticNode node = graph.Nodes[nodeId];
                if (node.Id != nodeId)
                    throw new ArgumentException("Node IDs must match their positions in Nodes.", nameof(graph));

                if (node.Children.Count == 0)
                {
                    if (node.TerrainId < 0 || node.TerrainId >= terrainCount)
                        throw new ArgumentException(
                            $"Leaf {nodeId} has invalid terrain ID {node.TerrainId}.", nameof(graph));
                    continue;
                }

                if (node.TerrainId != -1)
                    throw new ArgumentException($"Non-leaf node {nodeId} must have terrain ID -1.", nameof(graph));

                edgeCount = checked(edgeCount + node.Children.Count);
                var seenChildren = new HashSet<int>();
                foreach (SemanticEdge edge in node.Children)
                {
                    if (!seenChildren.Add(edge.ChildId))
                        throw new ArgumentException(
                            $"Node {nodeId} lists child {edge.ChildId} twice.", nameof(graph));
                    if (edge.Weight < 0 || float.IsNaN(edge.Weight) || float.IsInfinity(edge.Weight))
                        throw new ArgumentException(
                            $"Edge {nodeId} -> {edge.ChildId} has an invalid weight.", nameof(graph));
                }
            }

            return order;
        }

        private static ulong[] BuildLeafMasks(SemanticGraph graph, int wordsPerNode, int[] order)
        {
            var masks = new ulong[checked(graph.Nodes.Count * wordsPerNode)];

            // Children are visited before their parents, so shared descendants are
            // evaluated once and each parent can union its direct children's masks.
            for (int i = order.Length - 1; i >= 0; i--)
            {
                int nodeId = order[i];
                SemanticNode node = graph.Nodes[nodeId];
                int offset = nodeId * wordsPerNode;

                if (node.Children.Count == 0)
                {
                    int terrainId = node.TerrainId;
                    masks[offset + terrainId / 64] = 1UL << (terrainId % 64);
                    continue;
                }

                foreach (SemanticEdge edge in node.Children)
                {
                    int childOffset = edge.ChildId * wordsPerNode;
                    for (int word = 0; word < wordsPerNode; word++)
                        masks[offset + word] |= masks[childOffset + word];
                }
            }

            return masks;
        }

        private static SemanticAdjacency[] CompileAdjacencies(SemanticGraph graph)
        {
            if (graph.Adjacencies == null)
                throw new ArgumentException("The adjacency list must be initialized.", nameof(graph));

            var result = new List<SemanticAdjacency>();
            var seen = new HashSet<(int, Direction, int)>();
            foreach (SemanticAdjacency rule in graph.Adjacencies)
            {
                if (rule.SourceId < 0 || rule.SourceId >= graph.Nodes.Count ||
                    rule.NeighborId < 0 || rule.NeighborId >= graph.Nodes.Count ||
                    (int) rule.Direction >= Dirs.Count)
                {
                    throw new ArgumentException("A semantic adjacency has an invalid node ID or direction.", nameof(graph));
                }

                if (seen.Add((rule.SourceId, rule.Direction, rule.NeighborId)))
                    result.Add(rule);

                Direction opposite = Dirs.Opposite(rule.Direction);
                if (seen.Add((rule.NeighborId, opposite, rule.SourceId)))
                    result.Add(new SemanticAdjacency(rule.NeighborId, opposite, rule.SourceId));
            }

            return result.ToArray();
        }

        private static void BuildRoutes(CompiledSemanticGraph graph, int[] order)
        {
            var routeCounts = new int[graph.NodeCount];
            var routeNodeCounts = new int[graph.NodeCount];

            // Length sum includes the current node once for every route below it.
            // All nodes are reachable, so a limit exceeded by any subtree also
            // implies the limit would be exceeded by the whole graph.
            for (int i = order.Length - 1; i >= 0; i--)
            {
                int nodeId = order[i];
                int start = graph.ChildOffsets[nodeId];
                int end = graph.ChildOffsets[nodeId + 1];
                if (start == end)
                {
                    routeCounts[nodeId] = 1;
                    routeNodeCounts[nodeId] = 1;
                    continue;
                }

                long routeCount = 0;
                long routeNodeCount = 0;
                for (int edge = start; edge < end; edge++)
                {
                    int childId = graph.ChildIds[edge];
                    routeCount += routeCounts[childId];
                    routeNodeCount += routeNodeCounts[childId] + routeCounts[childId];
                    if (routeCount > MaxRouteCount || routeNodeCount > MaxRouteNodeCount)
                    {
                        throw new ArgumentException(
                            $"Semantic graph exceeds the limit of {MaxRouteCount} routes " +
                            $"or {MaxRouteNodeCount} total nodes across routes.");
                    }
                }

                routeCounts[nodeId] = (int) routeCount;
                routeNodeCounts[nodeId] = (int) routeNodeCount;
            }

            int root = graph.RootId;
            graph.RouteTerrainIds = new int[routeCounts[root]];
            graph.RouteNodeOffsets = new int[routeCounts[root] + 1];
            graph.RouteNodes = new int[routeNodeCounts[root]];
            graph.RouteEdgeIndices = new int[routeNodeCounts[root]];

            // Traverse each distinct route in child ordering. These parallel
            // arrays are a reusable DFS stack, so deep graphs avoid recursion.
            var nodes = new int[graph.NodeCount];
            var incomingEdges = new int[graph.NodeCount];
            var nextChildEdges = new int[graph.NodeCount];
            nodes[0] = root;
            incomingEdges[0] = -1;
            nextChildEdges[0] = graph.ChildOffsets[root];

            int routeIndex = 0;
            int nodeOffset = 0;
            int depth = 0;
            while (depth >= 0)
            {
                int nodeId = nodes[depth];
                int terrainId = graph.LeafTerrainIds[nodeId];
                if (terrainId >= 0)
                {
                    int length = depth + 1;
                    graph.RouteTerrainIds[routeIndex] = terrainId;
                    graph.RouteNodeOffsets[routeIndex++] = nodeOffset;
                    Array.Copy(nodes, 0, graph.RouteNodes, nodeOffset, length);
                    Array.Copy(incomingEdges, 0, graph.RouteEdgeIndices, nodeOffset, length);
                    nodeOffset += length;
                    depth--;
                    continue;
                }

                int edge = nextChildEdges[depth];
                if (edge == graph.ChildOffsets[nodeId + 1])
                {
                    depth--;
                    continue;
                }

                nextChildEdges[depth]++;
                int childId = graph.ChildIds[edge];
                depth++;
                nodes[depth] = childId;
                incomingEdges[depth] = edge;
                nextChildEdges[depth] = graph.ChildOffsets[childId];
            }

            graph.RouteNodeOffsets[routeIndex] = nodeOffset;
        }
    }
}
