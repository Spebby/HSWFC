using System.Collections.Generic;


namespace WFC.Core {
    public struct SemanticEdge {
        public readonly int childId;
        public readonly float weight;

        public SemanticEdge(int childId, float weight) {
            this.childId = childId;
            this.weight  = weight;
        }
    }

    public class SemanticNode {
        public readonly int id;
        public readonly int terrainId;

        /// <summary>
        /// Where on the regional noise (0..1) this node prefers to appear, or NaN for no preference. A route uses the
        /// center of the deepest node on it that has one.
        /// </summary>
        public float biomeCenter = float.NaN;

        public readonly List<SemanticEdge> children = new();

        public SemanticNode(int id, int terrainId = -1) {
            this.id        = id;
            this.terrainId = terrainId;
        }
    }
}