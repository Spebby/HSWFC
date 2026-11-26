using System.Collections.Generic;
using System.Linq;


namespace WFC.Core {
    /// <summary>A rooted DAG whose node IDs match their indices in <see cref="Nodes"/>.</summary>
    public class SemanticGraph {
        public int RootId;
        public readonly List<SemanticNode> Nodes = new();
        public readonly List<SemanticAdjacency> Adjacencies = new();

        public SemanticGraph(int rootId) => RootId = rootId;

        public bool IsLeaf(int nodeId) => Nodes[nodeId].children.Count == 0;

        /// <summary>Permits routes through <paramref name="neighborId"/> beside routes through <paramref name="sourceId"/> in all eight directions.</summary>
        public void AllowNeighbors(int sourceId, int neighborId) {
            for (int d = 0; d < Dirs.Count; d++)
                Adjacencies.Add(new SemanticAdjacency(sourceId, (Direction)d, neighborId));
        }

        public HashSet<int> GetLeafIds(int nodeId) {
            HashSet<int> leaves = new();
            foreach (int reachableId in GetReachableIds(nodeId).Where(reachableId => IsLeaf(reachableId))) {
                leaves.Add(reachableId);
            }

            return leaves;
        }

        public bool HasValidReferences() {
            if (Nodes == null || RootId < 0 || RootId >= Nodes.Count) return false;
            return Nodes.All(node => !node.children.Any(edge => edge is not { childId: >= 0 } || edge.childId >= Nodes.Count));
        }

        public bool HasCycle() => !TryGetTopologicalOrder(out _);

        public bool AllNodesReachableFromRoot() =>
            GetReachableIds(RootId).Count == Nodes.Count;

        HashSet<int> GetReachableIds(int startNode) {
            HashSet<int> reachable = new() { startNode };
            Stack<int>   pending   = new();
            pending.Push(startNode);

            while (pending.Count > 0) {
                foreach (SemanticEdge edge in
                         Nodes[pending.Pop()].children.Where(edge => reachable.Add(edge.childId))) {
                    pending.Push(edge.childId);
                }
            }

            return reachable;
        }

        // Kahn's algorithm emits parents before children. A cycle leaves some nodes unprocessed.
        internal bool TryGetTopologicalOrder(out int[] order) {
            int nodeCount = Nodes.Count;
            order = new int[nodeCount];
            int[] incoming = new int[nodeCount];

            foreach (SemanticEdge edge in Nodes.SelectMany(node => node.children)) {
                incoming[edge.childId]++;
            }

            // Reuse the output array as a queue of nodes with no remaining parents.
            int written = 0;
            for (int nodeId = 0; nodeId < nodeCount; nodeId++) {
                if (incoming[nodeId] == 0) order[written++] = nodeId;
            }

            for (int read = 0; read < written; read++) {
                foreach (SemanticEdge edge in Nodes[order[read]].children.Where(edge => --incoming[edge.childId] == 0)) {
                    order[written++] = edge.childId;
                }
            }

            return written == nodeCount;
        }
    }
}