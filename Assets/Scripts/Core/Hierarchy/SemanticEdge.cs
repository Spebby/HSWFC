namespace WFC.Core
{
    public class SemanticEdge
    {
        public int ChildId;
        public float Weight;
        public SemanticEdge(int childId, float weight)
        {
            ChildId = childId;
            Weight = weight;
        }
    }
}