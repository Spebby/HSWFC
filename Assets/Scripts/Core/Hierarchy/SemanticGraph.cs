using System.Collections.Generic;

namespace WFC.Core
{
    /// <summary>A rooted DAG whose node IDs match their indices in <see cref="Nodes"/>.</summary>
    public class SemanticGraph
    {
        public int RootId;
        public List<SemanticNode> Nodes = new List<SemanticNode>();
        public List<SemanticAdjacency> Adjacencies = new List<SemanticAdjacency>();

        public SemanticGraph(int rootId)
        {
            RootId = rootId;
        }

        public bool IsLeaf(int nodeId) => Nodes[nodeId].Children.Count == 0;

        public HashSet<int> GetLeafIds(int nodeId)
        {
            var leaves = new HashSet<int>();
            foreach (int reachableId in GetReachableIds(nodeId))
            {
                if (IsLeaf(reachableId)) leaves.Add(reachableId);
            }

            return leaves;
        }

        public bool HasValidReferences()
        {
            if (Nodes == null || RootId < 0 || RootId >= Nodes.Count) return false;

            foreach (SemanticNode node in Nodes)
            {
                if (node == null || node.Children == null) return false;

                foreach (SemanticEdge edge in node.Children)
                {
                    if (edge == null || edge.ChildId < 0 || edge.ChildId >= Nodes.Count) return false;
                }
            }

            return true;
        }

        public bool HasCycle() => !TryGetTopologicalOrder(out _);

        public bool AllNodesReachableFromRoot() =>
            GetReachableIds(RootId).Count == Nodes.Count;

        private HashSet<int> GetReachableIds(int startNode)
        {
            var reachable = new HashSet<int> { startNode };
            var pending = new Stack<int>();
            pending.Push(startNode);

            while (pending.Count > 0)
            {
                foreach (SemanticEdge edge in Nodes[pending.Pop()].Children)
                {
                    if (reachable.Add(edge.ChildId)) pending.Push(edge.ChildId);
                }
            }

            return reachable;
        }

        // Kahn's algorithm emits parents before children. A cycle leaves some nodes unprocessed.
        internal bool TryGetTopologicalOrder(out int[] order)
        {
            int nodeCount = Nodes.Count;
            order = new int[nodeCount];
            int[] incoming = new int[nodeCount];

            foreach (SemanticNode node in Nodes)
            {
                foreach (SemanticEdge edge in node.Children) incoming[edge.ChildId]++;
            }

            // Reuse the output array as a queue of nodes with no remaining parents.
            int written = 0;
            for (int nodeId = 0; nodeId < nodeCount; nodeId++)
            {
                if (incoming[nodeId] == 0) order[written++] = nodeId;
            }

            for (int read = 0; read < written; read++)
            {
                foreach (SemanticEdge edge in Nodes[order[read]].Children)
                {
                    if (--incoming[edge.ChildId] == 0) order[written++] = edge.ChildId;
                }
            }

            return written == nodeCount;
        }
    }
}
