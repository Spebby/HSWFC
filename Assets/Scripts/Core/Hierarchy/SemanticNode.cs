using System.Collections.Generic;

namespace WFC.Core
{
    public class SemanticNode
    {
        public string Name;
        public int Id;

        public int TerrainId;
        public List<SemanticEdge> Children = new List<SemanticEdge>();
        public SemanticNode(string name, int id, int terrainId = -1)
        {
            Name = name;
            Id = id;
            TerrainId = terrainId;
        }
    }
}