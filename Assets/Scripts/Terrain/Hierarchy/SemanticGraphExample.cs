using UnityEngine;
using WFC.Core;


public class SemanticGraphExample : MonoBehaviour {
    void Start() {
        // Terrains: 0 Grass, 1 Dirt, 2 Water.
        SemanticGraph graph = new(0);

        // Semantic groups.
        graph.Nodes.Add(new SemanticNode(0));
        graph.Nodes.Add(new SemanticNode(1));
        graph.Nodes.Add(new SemanticNode(2));

        // Leaves: graph node ID followed by concrete terrain ID.
        graph.Nodes.Add(new SemanticNode(3, 0));
        graph.Nodes.Add(new SemanticNode(4, 1));
        graph.Nodes.Add(new SemanticNode(5, 2));

        // Root can become Land or Shore.
        graph.Nodes[0].children.Add(new SemanticEdge(1, 1.0f));
        graph.Nodes[0].children.Add(new SemanticEdge(2, 1.0f));

        // Land can become Grass or Dirt.
        graph.Nodes[1].children.Add(new SemanticEdge(3, 1.0f));
        graph.Nodes[1].children.Add(new SemanticEdge(4, 1.0f));

        // Shore can become Dirt or Water. Dirt is shared, so it appears on two routes.
        graph.Nodes[2].children.Add(new SemanticEdge(4, 1.0f));
        graph.Nodes[2].children.Add(new SemanticEdge(5, 1.0f));

        // Adjacency is granted per direction, and the opposite direction is inferred. Land cells may sit beside
        // Land or Dirt, and Dirt (on either route) may sit beside Land, Shore and Water.
        graph.AllowNeighbors(1, 1);
        graph.AllowNeighbors(1, 4);
        graph.AllowNeighbors(2, 2);
        graph.AllowNeighbors(4, 2);

        // Compile also performs the graph validation checks and enumerates the routes.
        CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(graph, 3);
        Debug.Log($"Routes: {compiled.RouteCount}");

        // Terrain adjacency applies only to directions the graph does not declare.
        WFCRules rules = new WFCRulesBuilder(3).Build();

        // Construct a grid with two cells and give it the graph.
        using WFCSolver solver = new(rules, 2, 1, semanticGraph: compiled);
        WFCStatus       status = solver.Solve(7UL);

        Debug.Log($"Solve status: {status}");

        if (status != WFCStatus.Success) return;
        Debug.Log($"Final semantic leaves: {string.Join(", ", solver.SemanticNodes)}");
        Debug.Log($"Final terrain IDs: {string.Join(", ", solver.Layout)}");

        for (int cell = 0; cell < solver.Layout.Length; cell++) {
            Debug.Log($"Cell {cell} path: {string.Join(" -> ", solver.GetSemanticPath(cell))}");
        }
    }
}