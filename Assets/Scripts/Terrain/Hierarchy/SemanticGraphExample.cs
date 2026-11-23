using UnityEngine;
using WFC.Core;

public class SemanticGraphExample : MonoBehaviour
{
    private void Start()
    {
        SemanticGraph graph = new SemanticGraph(0);

        // Semantic groups.
        graph.Nodes.Add(new SemanticNode("Root", 0));
        graph.Nodes.Add(new SemanticNode("Land", 1));
        graph.Nodes.Add(new SemanticNode("Shore", 2));

        // Leaves: graph node ID followed by concrete terrain ID.
        graph.Nodes.Add(new SemanticNode("Grass", 3, 0));
        graph.Nodes.Add(new SemanticNode("Dirt", 4, 1));
        graph.Nodes.Add(new SemanticNode("Water", 5, 2));

        // Root can become Land or Shore.
        graph.Nodes[0].Children.Add(new SemanticEdge(1, 1.0f));
        graph.Nodes[0].Children.Add(new SemanticEdge(2, 1.0f));

        // Land can become Grass or Dirt.
        graph.Nodes[1].Children.Add(new SemanticEdge(3, 1.0f));
        graph.Nodes[1].Children.Add(new SemanticEdge(4, 1.0f));

        // Shore can become Dirt or Water.
        graph.Nodes[2].Children.Add(new SemanticEdge(4, 1.0f));
        graph.Nodes[2].Children.Add(new SemanticEdge(5, 1.0f));

        // Compile also performs the graph validation checks.
        CompiledSemanticGraph compiled =
            SemanticGraphCompiler.Compile(graph, 3);

        Debug.Log(
            "Child offsets: " +
            string.Join(", ", compiled.ChildOffsets));

        Debug.Log(
            "Child IDs: " +
            string.Join(", ", compiled.ChildIds));

        Debug.Log(
            "Child weights: " +
            string.Join(", ", compiled.ChildWeights));

        Debug.Log(
            "Leaf terrain IDs: " +
            string.Join(", ", compiled.LeafTerrainIds));

        Debug.Log(
            "Descendant masks: " +
            string.Join(", ", compiled.DescendantMasks));

        // Three concrete terrain types, matching the compiled graph.
        WFCRules rules = new WFCRulesBuilder(3).Build();

        // Construct a grid with two cells and give it the graph.
        using (WFCSolver solver = new WFCSolver(
            rules, 2, 1, semanticGraph: compiled))
        {
            WFCStatus status = solver.Solve(7UL);

            Debug.Log("Solve status: " + status);

            if (status == WFCStatus.Success)
            {
                Debug.Log(
                    "Final semantic leaves: " +
                    string.Join(", ", solver.SemanticNodes));

                Debug.Log(
                    "Final terrain IDs: " +
                    string.Join(", ", solver.Layout));

                for (int cell = 0; cell < solver.Layout.Length; cell++)
                {
                    Debug.Log($"Cell {cell} path: {string.Join(" -> ", solver.GetSemanticPath(cell))}");
                }
            }
        }
    }
}
