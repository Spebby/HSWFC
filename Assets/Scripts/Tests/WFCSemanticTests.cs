using NUnit.Framework;
using WFC.Core;

namespace WFC.Tests {
    public class WFCSemanticTests {
        // Root -> Land | Shore, both -> Dirt. One terrain, two routes.
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
        public void ZeroWeightBranchesAreNeverChosenWhileAnotherIsAvailable() {
            CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(SharedLeafGraph(landWeight: 0f), 1);
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
            Assert.AreEqual(WFCStatus.Unsatisfiable, solver.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
            Assert.IsEmpty(solver.GetSemanticPath(0));
            Assert.IsEmpty(solver.GetSemanticPath(1));

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, initialSemanticNodes: new[] { -1, 2 }));
            CollectionAssert.AreEqual(new[] { 0, 2, 3 }, solver.GetSemanticPath(0));
            CollectionAssert.AreEqual(new[] { 2, 3 }, solver.GetSemanticPath(1));
            CollectionAssert.AreEqual(new[] { 0, 0 }, solver.Layout);

            // Starting at the shared leaf leaves either ancestor route available for adjacency.
            foreach (int neighborNode in new[] { 1, 2 }) {
                Assert.AreEqual(WFCStatus.Success, solver.Solve(7, initialSemanticNodes: new[] { 3, neighborNode }));
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
            // The semantic rule authorizes Grass/Dirt even without a terrain adjacency rule.
            using WFCSolver solver = new(new WFCRulesBuilder(3).Build(), 2, 1, semanticGraph: PartialRefinementGraph());

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

            Assert.AreEqual(WFCStatus.Unsatisfiable, solver.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
        }

        [Test]
        public void ConcreteWindowConstraintsStillApplyToSemanticAllowances() {
            WFCRulesBuilder builder = new(3);
            bool[] renderable = new bool[256];
            // Tile 0 may not have the marker tile 1 to its east.
            for (int mask = 0; mask < renderable.Length; mask++) renderable[mask] = (mask & (1 << (int)Direction.E)) == 0;
            builder.AddWindow(1, 1UL, renderable);
            using WFCSolver solver = new(builder.Build(), 2, 1, semanticGraph: PartialRefinementGraph());

            int[] startNodes = { 1, 2 };
            Assert.AreEqual(WFCStatus.Unsatisfiable, solver.Solve(7, initialSemanticNodes: startNodes));
            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, useWindows: false, initialSemanticNodes: startNodes));
            CollectionAssert.AreEqual(new[] { 0, 1 }, solver.Layout);
        }

        [Test]
        public void TerrainAdjacencyAppliesOnlyToDirectionsTheGraphDoesNotDeclare() {
            // The graph declares East and West. North uses the terrain table, which here allows only self-adjacency.
            using WFCSolver solver = new(new WFCRulesBuilder(3).Build(), 1, 2, semanticGraph: PartialRefinementGraph());

            Assert.AreEqual(WFCStatus.Unsatisfiable, solver.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
            using WFCSolver permissive = new(new WFCRulesBuilder(3).AllowCardinal(0, 1).Build(), 1, 2, semanticGraph: PartialRefinementGraph());
            Assert.AreEqual(WFCStatus.Success, permissive.Solve(7, initialSemanticNodes: new[] { 1, 2 }));
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
        public void RoutesBeyondTheFirstDomainWordSolve() {
            // 70 leaves under one meta-tile: 70 routes, two domain words.
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Group", 1));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, 1f));
            for (int t = 0; t < 70; t++) {
                graph.Nodes.Add(new SemanticNode($"Leaf {t}", t + 2, t));
                graph.Nodes[1].Children.Add(new SemanticEdge(t + 2, t == 69 ? 1f : 0f));
            }

            CompiledSemanticGraph compiled = SemanticGraphCompiler.Compile(graph, 70);
            using WFCSolver       solver   = new(new WFCRulesBuilder(70).Build(), 3, 3, semanticGraph: compiled);

            Assert.AreEqual(WFCStatus.Success, solver.Solve(3));
            foreach (int terrain in solver.Layout) Assert.AreEqual(69, terrain);
            CollectionAssert.AreEqual(new[] { 0, 1, 71 }, solver.GetSemanticPath(4));
        }

        // Terrains: Grass 0, Tree 1, Water 2.
        // Root -> Forest | Water. Forest -> Sparse | Deep. Sparse and Deep both lead to Grass and Tree, with
        // different weights, so they share leaves and differ only in weights and in what may neighbour them.
        static SemanticGraph ForestGraph() {
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Forest", 1));
            graph.Nodes.Add(new SemanticNode("Sparse", 2));
            graph.Nodes.Add(new SemanticNode("Deep", 3));
            graph.Nodes.Add(new SemanticNode("Grass", 4, 0));
            graph.Nodes.Add(new SemanticNode("Tree", 5, 1));
            graph.Nodes.Add(new SemanticNode("Water", 6, 2));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, 1f));
            graph.Nodes[0].Children.Add(new SemanticEdge(6, 1f));
            graph.Nodes[1].Children.Add(new SemanticEdge(2, 1f));
            graph.Nodes[1].Children.Add(new SemanticEdge(3, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(4, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(5, 1f));
            graph.Nodes[3].Children.Add(new SemanticEdge(4, 0.1f));
            graph.Nodes[3].Children.Add(new SemanticEdge(5, 1f));

            // Sparse forest may border forest and water. Deep forest only borders forest. The extra grant sits on
            // Sparse rather than on Forest, which Deep would inherit.
            graph.AllowNeighbors(2, 1);
            graph.AllowNeighbors(2, 6);
            graph.AllowNeighbors(3, 1);
            graph.AllowNeighbors(6, 6);
            return graph;
        }

        static WFCSolver ForestSolver(SemanticGraph graph, int width, int height) =>
            new(new WFCRulesBuilder(3).Build(), width, height, semanticGraph: SemanticGraphCompiler.Compile(graph, 3));

        static bool Passes(int[] path, int node) => System.Array.IndexOf(path, node) >= 0;

        // Paths begin at a cell's starting node, so a cell that started at Deep or Sparse has no Forest entry.
        static bool IsForest(int[] path) => Passes(path, 1) || Passes(path, 2) || Passes(path, 3);

        [Test]
        public void VariantsThatShareLeavesCanDifferInWhatMayNeighbourThem() {
            using WFCSolver solver = ForestSolver(ForestGraph(), 8, 8);

            int[] start = new int[64];
            System.Array.Fill(start, -1);
            start[3 * 8 + 3] = 3;

            int deepCells = 0, waterBesideForest = 0;
            for (ulong seed = 1; seed <= 6; seed++) {
                Assert.AreEqual(WFCStatus.Success, solver.Solve(seed, initialSemanticNodes: start));
                for (int y = 0; y < 8; y++) {
                    for (int x = 0; x < 8; x++) {
                        bool deep = Passes(solver.GetSemanticPath(y * 8 + x), 3);
                        if (deep) deepCells++;

                        for (int dy = -1; dy <= 1; dy++) {
                            for (int dx = -1; dx <= 1; dx++) {
                                int nx = x + dx, ny = y + dy;
                                if ((dx == 0 && dy == 0) || WFCSolver.IsOutOfBounds(nx, ny, 8, 8)) continue;

                                int[] neighbour = solver.GetSemanticPath(ny * 8 + nx);
                                if (deep) Assert.IsTrue(IsForest(neighbour), $"Deep cell ({x},{y}) has a non-forest neighbour at ({nx},{ny})");
                                if (IsForest(solver.GetSemanticPath(y * 8 + x)) && solver.Layout[ny * 8 + nx] == 2) waterBesideForest++;
                            }
                        }
                    }
                }
            }

            Assert.Greater(deepCells, 0);
            // The rule is not vacuous: sparse forest does border water.
            Assert.Greater(waterBesideForest, 0);
        }

        [Test]
        public void VariantsThatShareLeavesDifferInWeights() {
            using WFCSolver solver = ForestSolver(ForestGraph(), 20, 20);

            Assert.Greater(GrassShare(solver, 2), 0.35);
            Assert.Less(GrassShare(solver, 3), 0.2);
        }

        static double GrassShare(WFCSolver solver, int variantNode) {
            int[] start = new int[400];
            System.Array.Fill(start, variantNode);
            Assert.AreEqual(WFCStatus.Success, solver.Solve(11, initialSemanticNodes: start));

            int grass = 0;
            foreach (int terrain in solver.Layout) {
                if (terrain == 0) grass++;
            }

            return grass / 400.0;
        }

        [Test]
        public void ALeafAddsItsOwnGrantsToWhatItInheritsFromItsAncestors() {
            SemanticGraph graph = ForestGraph();
            graph.Adjacencies.Clear();
            graph.Adjacencies.Add(new SemanticAdjacency(1, Direction.E, 1)); // forest beside forest
            graph.Adjacencies.Add(new SemanticAdjacency(5, Direction.E, 6)); // trees beside water, on top of that
            using WFCSolver solver = ForestSolver(graph, 2, 1);

            Assert.AreEqual(WFCStatus.Success, solver.Solve(7, initialSemanticNodes: new[] { 5, 6 }));
            CollectionAssert.AreEqual(new[] { 1, 2 }, solver.Layout);

            // Grass inherits only the forest grant.
            Assert.AreEqual(WFCStatus.Unsatisfiable, solver.Solve(7, initialSemanticNodes: new[] { 4, 6 }));
        }

        // Terrains: Grass 0, Dirt 1, Water 2. This is the demo scene's graph and terrain rules.
        static CompiledSemanticGraph DemoGraph() {
            SemanticGraph graph = new(0);
            graph.Nodes.Add(new SemanticNode("Root", 0));
            graph.Nodes.Add(new SemanticNode("Land", 1));
            graph.Nodes.Add(new SemanticNode("Shore", 2));
            graph.Nodes.Add(new SemanticNode("Grass", 3, 0));
            graph.Nodes.Add(new SemanticNode("Dirt", 4, 1));
            graph.Nodes.Add(new SemanticNode("Water", 5, 2));
            graph.Nodes[0].Children.Add(new SemanticEdge(1, 1f));
            graph.Nodes[0].Children.Add(new SemanticEdge(2, 1f));
            graph.Nodes[1].Children.Add(new SemanticEdge(3, 4f));
            graph.Nodes[1].Children.Add(new SemanticEdge(4, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(4, 1f));
            graph.Nodes[2].Children.Add(new SemanticEdge(5, 4f));

            // Authored for North and East only. The compiler adds South and West.
            foreach (Direction direction in new[] { Direction.N, Direction.E }) {
                graph.Adjacencies.Add(new SemanticAdjacency(1, direction, 1));
                graph.Adjacencies.Add(new SemanticAdjacency(2, direction, 2));
                graph.Adjacencies.Add(new SemanticAdjacency(4, direction, 4));
                graph.Adjacencies.Add(new SemanticAdjacency(3, direction, 4));
                graph.Adjacencies.Add(new SemanticAdjacency(4, direction, 3));
                graph.Adjacencies.Add(new SemanticAdjacency(5, direction, 4));
                graph.Adjacencies.Add(new SemanticAdjacency(4, direction, 5));
            }

            return SemanticGraphCompiler.Compile(graph, 3);
        }

        [Test]
        public void DemoSceneSolves() {
            // Terrain rules: Grass touches Dirt, Dirt touches Water, with diagonals on.
            WFCRules rules = new WFCRulesBuilder(3) { diagonalRules = true }
                .AllowCardinal(0, 1).AllowCardinal(1, 2).AllowDiagonal(0, 1).AllowDiagonal(1, 2).Build();

            int[] start = new int[32 * 32];
            System.Array.Fill(start, -1);
            void Region(int x, int y, int w, int h, int node) {
                for (int j = y; j < y + h; j++)
                    for (int i = x; i < x + w; i++) start[j * 32 + i] = node;
            }

            Region(0, 0, 17, 8, 1);
            Region(0, 8, 19, 8, 1);
            Region(0, 16, 16, 8, 1);
            Region(0, 24, 14, 8, 1);
            Region(22, 0, 10, 8, 5);
            Region(24, 8, 8, 8, 5);
            Region(21, 16, 11, 8, 5);
            Region(19, 24, 13, 8, 5);
            start[16 * 32 + 18] = 2;

            using WFCSolver solver = new(rules, 32, 32, semanticGraph: DemoGraph());
            for (ulong seed = 1; seed <= 8; seed++) {
                Assert.AreEqual(WFCStatus.Success, solver.Solve(seed, initialSemanticNodes: start), $"seed {seed}");

                // Grass and Water never touch directly. Dirt bridges them.
                for (int y = 0; y < 32; y++) {
                    for (int x = 0; x < 32; x++) {
                        if (solver.Layout[y * 32 + x] != 0) continue;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++) {
                                int nx = x + dx, ny = y + dy;
                                if (WFCSolver.IsOutOfBounds(nx, ny, 32, 32)) continue;
                                Assert.IsTrue(solver.Layout[ny * 32 + nx] != 2, $"Grass at ({x},{y}) touches water at ({nx},{ny})");
                            }
                    }
                }
            }
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
