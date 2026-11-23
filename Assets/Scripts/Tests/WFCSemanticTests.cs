using NUnit.Framework;
using WFC.Core;

namespace WFC.Tests {
    public class WFCSemanticTests {
        static SemanticGraph SharedLeafGraph(float landWeight = 1f, float shoreWeight = 1f) {
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Land", 1));
            graph.Nodes.Add(new SemanticNode("Shore", 2));
            graph.Nodes.Add(new SemanticNode("Dirt", 3, 0));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, landWeight));
            graph.Nodes[0].Children.Add(new SemanticEdge(2, shoreWeight));
            graph.Nodes[1].Children.Add(new SemanticEdge(3, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(3, 1f));
            return graph;
        }

        [Test]
        public void WeightedBranchesStillCollapseWhenTheConcreteTerrainIsAlreadyFixed() {
            SemanticGraph graph = SharedLeafGraph(landWeight: 0f);
            CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(graph, 1);
            using WFCSolver solver = new(new WFCRulesBuilder(1).Build(), 1, 1, semanticGraph: compiled);

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7));
            Assert.AreEqual(0, solver.Layout[0]);
            Assert.AreEqual(3, solver.SemanticNodes[0]);
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, solver.GetSemanticPath(0));
        }

        [Test]
        public void SemanticAdjacencyDistinguishesSharedLeafRoutesAndSolveCanRecover() {
            SemanticGraph graph = SharedLeafGraph();
            graph.Adjacencies.Add(new SemanticAdjacency(1, Direction.E, 1));
            graph.Adjacencies.Add(new SemanticAdjacency(2, Direction.E, 2));
            CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(graph, 1);
            using WFCSolver solver = new(new WFCRulesBuilder(1).Build(), 2, 1, semanticGraph: compiled);

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, initialSemanticNodes: new[] { 1, -1 }));
            CollectionAssert.AreEqual(new[] { 1, 3 }, solver.GetSemanticPath(0));
            CollectionAssert.AreEqual(new[] { 0, 1, 3 }, solver.GetSemanticPath(1));

            // Both cells could still become Dirt. Only their semantic paths conflict.
            Assert.AreEqual(WFCStatus.Unsatisfiable,
                solver.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
            Assert.IsEmpty(solver.GetSemanticPath(0));
            Assert.IsEmpty(solver.GetSemanticPath(1));

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, initialSemanticNodes: new[] { -1, 2 }));
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, solver.GetSemanticPath(0));
            CollectionAssert.AreEqual(new[] { 2, 3 }, solver.GetSemanticPath(1));
            CollectionAssert.AreEqual(new[] { 0, 0 }, solver.Layout);

            // Starting at the shared leaf leaves either ancestor route available for adjacency.
            foreach (int neighborNode in new[] { 1, 2 }) {
                Assert.AreEqual(WFCStatus.Success,
                    solver.Solve(7, initialSemanticNodes: new[] { 3, neighborNode }));
                CollectionAssert.AreEqual(new[] { 3 }, solver.GetSemanticPath(0));
                CollectionAssert.AreEqual(new[] { neighborNode, 3 }, solver.GetSemanticPath(1));
            }
        }

        static CompiledSemanticGraph PartialRefinementGraph() {
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Land", 1));
            graph.Nodes.Add(new SemanticNode("Shore", 2));
            graph.Nodes.Add(new SemanticNode("Grass", 3, 0));
            graph.Nodes.Add(new SemanticNode("Dirt", 4, 1));
            graph.Nodes.Add(new SemanticNode("Water", 5, 2));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, 1f));
            graph.Nodes[0].Children.Add(new SemanticEdge(2, 1f));
            graph.Nodes[1].Children.Add(new SemanticEdge(3, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(4, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(5, 10f));
            graph.Adjacencies.Add(new SemanticAdjacency(1, Direction.E, 4));
            return SemanticGraphCompiler.Compile(graph, 3);
        }

        [Test]
        public void MetaToLeafAdjacencyPrunesOneRefinementAndKeepsTheParent() {
            CompiledSemanticGraph graph = PartialRefinementGraph();
            // The semantic rule authorizes Grass/Dirt even without a flat AllowCardinal rule.
            using WFCSolver solver = new(new WFCRulesBuilder(3).Build(), 2, 1, semanticGraph: graph);

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
            CollectionAssert.AreEqual(new[] { 0, 1 }, solver.Layout);
            CollectionAssert.AreEqual(new[] { 1, 3 }, solver.GetSemanticPath(0));
            CollectionAssert.AreEqual(new[] { 2, 4 }, solver.GetSemanticPath(1));
        }

        [Test]
        public void ConcreteDirectionalRestrictionStillAppliesToSemanticAllowance() {
            WFCRulesBuilder builder = new(3);
            builder.Restrict(0, Direction.E, 1UL << 2);
            using WFCSolver solver = new(builder.Build(), 2, 1, semanticGraph: PartialRefinementGraph());

            Assert.AreEqual(WFCStatus.Unsatisfiable,
                solver.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
        }

        [Test]
        public void ConcreteWindowConstraintsStillApplyToSemanticAllowances() {
            WFCRulesBuilder builder = new(3);
            bool[] renderable = new bool[256];
            // Tile 0 may not have the marker tile 1 to its east.
            for (int mask = 0; mask < renderable.Length; mask++) {
                renderable[mask] = (mask & (1 << (int)Direction.E)) == 0;
            }
            builder.AddWindow(1, 1UL, renderable);
            using WFCSolver solver = new(builder.Build(), 2, 1, semanticGraph: PartialRefinementGraph());

            int[] startNodes = { 1, 2 };
            Assert.AreEqual(WFCStatus.Unsatisfiable, solver.Solve(7, initialSemanticNodes: startNodes));
            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, useWindows: false, initialSemanticNodes: startNodes));
            CollectionAssert.AreEqual(new[] { 0, 1 }, solver.Layout);
        }

        [Test]
        public void SemanticTileIdsBeyondTheFirstDomainWordArePreserved() {
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Low terrain ID", 1, 0));
            graph.Nodes.Add(new SemanticNode("High terrain ID", 2, 64));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, 0f));
            graph.Nodes[0].Children.Add(new SemanticEdge(2, 1f));
            CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(graph, 65);
            using WFCSolver solver = new(new WFCRulesBuilder(65).Build(), 1, 1, semanticGraph: compiled);

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7));
            Assert.AreEqual(64, solver.Layout[0]);
            CollectionAssert.AreEqual(new[] { 0, 2 }, solver.GetSemanticPath(0));
        }

        [Test]
        public void RetryDiscardsPathsFromAContradictoryBranch() {
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Alternating", 1));
            graph.Nodes.Add(new SemanticNode("Uniform", 2));
            graph.Nodes.Add(new SemanticNode("First", 3, 0));
            graph.Nodes.Add(new SemanticNode("Second", 4, 0));
            graph.Nodes.Add(new SemanticNode("Only", 5, 0));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, 1f));
            graph.Nodes[0].Children.Add(new SemanticEdge(2, 1f));
            graph.Nodes[1].Children.Add(new SemanticEdge(3, 1f));
            graph.Nodes[1].Children.Add(new SemanticEdge(4, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(5, 1f));

            // The alternating branch requires opposite colors across a triangle.
            // Initial propagation can support both colors locally. Committing a color exposes the contradiction.
            foreach (Direction direction in new[] { Direction.E, Direction.N, Direction.NE }) {
                graph.Adjacencies.Add(new SemanticAdjacency(3, direction, 4));
                graph.Adjacencies.Add(new SemanticAdjacency(4, direction, 3));
                graph.Adjacencies.Add(new SemanticAdjacency(2, direction, 2));
            }

            CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(graph, 1);
            using WFCSolver solver = new(new WFCRulesBuilder(1).Build(), 2, 2, semanticGraph: compiled);

            // Find a deterministic seed that explores the invalid branch before finding the uniform solution.
            for (ulong seed = 0; seed < 32; seed++) {
                Assert.AreEqual(WFCStatus.Success, solver.Solve(seed, maxAttempts: 64));
                if (solver.LastAttempts == 1) continue;

                for (int cell = 0; cell < solver.Layout.Length; cell++) {
                    CollectionAssert.AreEqual(new[] { 0, 2, 5 }, solver.GetSemanticPath(cell));
                }
                return;
            }

            Assert.Fail("Expected at least one seed to exercise a failed search attempt.");
        }
    }
}
