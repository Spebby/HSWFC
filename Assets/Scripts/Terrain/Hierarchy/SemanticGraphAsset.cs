using System;
using System.Collections.Generic;
using UnityEngine;
using WFC.Core;

namespace WFC.Terrain {
    /// <summary>Inspector authoring for a semantic DAG using TerrainDef references at its leaves.</summary>
    [CreateAssetMenu(menuName = "Terrain/Semantic Graph", fileName = "SemanticGraph")]
    public sealed class SemanticGraphAsset : ScriptableObject {
        [Serializable]
        public sealed class Child {
            [Min(0)] public int nodeId;
            [Min(0f)] public float weight = 1f;
        }

        [Serializable]
        public sealed class Node {
            public string name;
            [Tooltip("Assign a terrain to a leaf. Leave this empty for a meta-tile with children.")]
            public TerrainDef terrain;
            [Tooltip("Use the preview color below in Semantic Paths.")]
            public bool overridePreviewColor;
            public Color previewColor = Color.white;
            public List<Child> children = new();
        }

        [Serializable]
        public struct Adjacency {
            [Min(0)] public int sourceId;
            public Direction direction;
            [Min(0)] public int neighborId;
        }

        [Min(0)] public int rootId;
        [Tooltip("A node's ID is its index in this list. Update references if you reorder nodes.")]
        public List<Node> nodes = new();
        [Tooltip("Allowed semantic adjacencies. The opposite-direction rule is inferred automatically.")]
        public List<Adjacency> adjacencies = new();

        /// <summary>Resolve leaf assets to the concrete terrain IDs of the current TerrainSet.</summary>
        public CompiledSemanticGraph Compile(IReadOnlyList<TerrainDef> terrains) {
            if (terrains == null) throw new ArgumentNullException(nameof(terrains));

            var terrainIds = new Dictionary<TerrainDef, int>();
            for (int i = 0; i < terrains.Count; i++) {
                TerrainDef terrain = terrains[i];
                if (!terrain || !terrainIds.TryAdd(terrain, i))
                    throw new ArgumentException("A semantic graph needs a TerrainSet with unique, non-null terrains.", nameof(terrains));
            }

            var graph = new SemanticGraph(rootId);
            for (int nodeId = 0; nodeId < nodes.Count; nodeId++) {
                Node entry = nodes[nodeId] ?? throw new InvalidOperationException($"Semantic node {nodeId} is missing.");
                int terrainId = -1;
                if (entry.children.Count == 0) {
                    if (!entry.terrain || !terrainIds.TryGetValue(entry.terrain, out terrainId))
                        throw new InvalidOperationException($"Semantic leaf {nodeId} ({entry.name}) must reference a terrain in this TerrainSet.");
                } else if (entry.terrain) {
                    throw new InvalidOperationException($"Semantic node {nodeId} ({entry.name}) has children and cannot also reference a terrain.");
                }

                var node = new SemanticNode(entry.name, nodeId, terrainId);
                foreach (Child child in entry.children) {
                    if (child == null) throw new InvalidOperationException($"Semantic node {nodeId} has a missing child entry.");
                    node.Children.Add(new SemanticEdge(child.nodeId, child.weight));
                }
                graph.Nodes.Add(node);
            }

            foreach (Adjacency adjacency in adjacencies) {
                graph.Adjacencies.Add(new SemanticAdjacency(adjacency.sourceId, adjacency.direction, adjacency.neighborId));
            }

            return SemanticGraphCompiler.Compile(graph, terrains.Count);
        }
    }
}
