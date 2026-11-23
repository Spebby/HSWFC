namespace WFC.Core
{
    /// <summary>
    /// Permits a neighbor in a direction relative to a semantic node.
    /// The compiler adds the opposite-direction declaration automatically.
    /// </summary>
    public readonly struct SemanticAdjacency
    {
        public readonly int SourceId;
        public readonly Direction Direction;
        public readonly int NeighborId;

        public SemanticAdjacency(int sourceId, Direction direction, int neighborId)
        {
            SourceId = sourceId;
            Direction = direction;
            NeighborId = neighborId;
        }
    }
}
