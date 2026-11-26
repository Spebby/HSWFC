namespace WFC.Core
{
    /// <summary>
    /// Permits a neighbor in a direction relative to a semantic node.
    /// The compiler adds the opposite-direction declaration automatically.
    /// </summary>
    /// <remarks>
    /// A cell's route inherits the permissions of every node on it, so a declaration on a meta-tile covers
    /// everything below it and a leaf can add to what it inherits. Once any node declares a direction, the
    /// declarations alone define the allowed pairs for that direction and the terrain adjacency rules are
    /// not consulted there. Explicit directional terrain restrictions still apply. To keep one variant
    /// narrower than a sibling, grant the extra neighbours on the sibling instead of on their parent.
    /// </remarks>
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
