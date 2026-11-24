using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace WFC.Core {
    /// <summary>
    /// Expands terrain rules into route rules, the tile alphabet the kernel solves over. A route is a
    /// terrain in the context of the nodes above it, so two routes ending at the same terrain can differ
    /// in what may neighbour them. Everything the kernel reads about tiles (support, mustExist, window
    /// targets and markers, weights) comes out indexed by route.
    /// </summary>
    internal static class RouteRules {
        const long MaxSupportBytes = 64L * 1024 * 1024;

        public static WFCRules Expand(WFCRules terrain, CompiledSemanticGraph graph) {
            int terrainCount = terrain.count, terrainWords = terrain.wordsPerDomain;
            int routeCount   = graph.RouteCount, routeWords = (routeCount + 63) / 64;
            if (graph.TerrainCount != terrainCount)
                throw new ArgumentException("The semantic graph must match the rules' terrain count.", nameof(graph));

            long supportWords = (long)Dirs.Count * routeCount * routeWords;
            if (supportWords * sizeof(ulong) > MaxSupportBytes)
                throw new ArgumentException(
                    $"Route adjacency needs {supportWords * sizeof(ulong) >> 20} MiB, above the {MaxSupportBytes >> 20} MiB limit. Simplify the graph.",
                    nameof(graph));

            ulong[] routesOfTerrain = new ulong[checked(terrainCount * routeWords)];
            for (int route = 0; route < routeCount; route++)
                routesOfTerrain[graph.RouteTerrainIds[route] * routeWords + (route >> 6)] |= 1UL << (route & 63);

            // Terrain adjacency, lifted to routes: a route supports every route of every terrain its terrain supports.
            ulong[] support  = new ulong[(int)supportWords];
            ulong[] expanded = new ulong[checked(terrainCount * routeWords)];
            for (int d = 0; d < Dirs.Count; d++) {
                Array.Clear(expanded, 0, expanded.Length);
                for (int t = 0; t < terrainCount; t++)
                    ExpandInto(terrain.support, (d * terrainCount + t) * terrainWords, terrainWords, terrainCount,
                        routesOfTerrain, routeWords, expanded, t * routeWords);

                for (int route = 0; route < routeCount; route++)
                    Array.Copy(expanded, graph.RouteTerrainIds[route] * routeWords,
                        support, (d * routeCount + route) * routeWords, routeWords);
            }

            ApplyDeclarations(terrain, graph, support, routesOfTerrain, routeCount, routeWords);

            ulong[] mustExist = new ulong[Dirs.Count * routeWords];
            for (int d = 0; d < Dirs.Count; d++)
                ExpandInto(terrain.mustExist, d * terrainWords, terrainWords, terrainCount,
                    routesOfTerrain, routeWords, mustExist, d * routeWords);

            int     windowCount = terrain.windowCount;
            ulong[] targets     = new ulong[checked(windowCount * routeWords)];
            ulong[] markers     = new ulong[checked(windowCount * routeWords)];
            for (int k = 0; k < windowCount; k++) {
                ExpandInto(terrain.windowTargets, k * terrainWords, terrainWords, terrainCount,
                    routesOfTerrain, routeWords, targets, k * routeWords);
                ExpandInto(terrain.windowMarkers, k * terrainWords, terrainWords, terrainCount,
                    routesOfTerrain, routeWords, markers, k * routeWords);
            }

            float[] weight = new float[routeCount];
            for (int route = 0; route < routeCount; route++) weight[route] = terrain.weight[graph.RouteTerrainIds[route]];

            return new WFCRules(routeCount, routeWords, weight, support, null, mustExist, targets, markers, terrain.windowFeasible);
        }

        /// <summary>
        /// Applies the graph's adjacency declarations. For a direction that has any, a route may neighbour the routes
        /// through every neighbour node declared by any node on it, and nothing else; the terrain table is not
        /// consulted there. Reciprocals are already in the declaration list, so the result is symmetric. Explicit
        /// directional terrain restrictions are applied on top. Directions without declarations keep the terrain table.
        /// </summary>
        static void ApplyDeclarations(WFCRules terrain, CompiledSemanticGraph graph, ulong[] support,
            ulong[] routesOfTerrain, int routeCount, int routeWords) {
            SemanticAdjacency[] rules = graph.Adjacencies;
            if (rules.Length == 0) return;

            int terrainCount = terrain.count, terrainWords = terrain.wordsPerDomain;

            // Routes passing through each node that a declaration names as a neighbour.
            var routesThrough = new Dictionary<int, ulong[]>();
            var neighbours    = new Dictionary<int, List<int>>[Dirs.Count];
            foreach (SemanticAdjacency rule in rules) {
                if (!routesThrough.ContainsKey(rule.NeighborId)) routesThrough.Add(rule.NeighborId, new ulong[routeWords]);

                neighbours[(int)rule.Direction] ??= new Dictionary<int, List<int>>();
                if (!neighbours[(int)rule.Direction].TryGetValue(rule.SourceId, out List<int> list)) {
                    list = new List<int>();
                    neighbours[(int)rule.Direction].Add(rule.SourceId, list);
                }

                list.Add(rule.NeighborId);
            }

            for (int route = 0; route < routeCount; route++) {
                for (int e = graph.RouteNodeOffsets[route]; e < graph.RouteNodeOffsets[route + 1]; e++) {
                    if (routesThrough.TryGetValue(graph.RouteNodes[e], out ulong[] mask))
                        mask[route >> 6] |= 1UL << (route & 63);
                }
            }

            ulong[] directional = terrain.directionalSupport == null ? null : new ulong[checked(terrainCount * routeWords)];
            for (int d = 0; d < Dirs.Count; d++) {
                if (neighbours[d] == null) continue;

                if (directional != null) {
                    Array.Clear(directional, 0, directional.Length);
                    for (int t = 0; t < terrainCount; t++)
                        ExpandInto(terrain.directionalSupport, (d * terrainCount + t) * terrainWords, terrainWords, terrainCount,
                            routesOfTerrain, routeWords, directional, t * routeWords);
                }

                for (int route = 0; route < routeCount; route++) {
                    int row = (d * routeCount + route) * routeWords;
                    Array.Clear(support, row, routeWords);

                    for (int e = graph.RouteNodeOffsets[route]; e < graph.RouteNodeOffsets[route + 1]; e++) {
                        if (!neighbours[d].TryGetValue(graph.RouteNodes[e], out List<int> list)) continue;

                        foreach (int neighbour in list) {
                            ulong[] through = routesThrough[neighbour];
                            for (int j = 0; j < routeWords; j++) support[row + j] |= through[j];
                        }
                    }

                    if (directional == null) continue;

                    int from = graph.RouteTerrainIds[route] * routeWords;
                    for (int j = 0; j < routeWords; j++) support[row + j] &= directional[from + j];
                }
            }
        }

        /// <summary>ORs the routes of every terrain set in a terrain-level mask into a route-level mask.</summary>
        static void ExpandInto(ulong[] source, int sourceOffset, int sourceWords, int terrainCount,
            ulong[] routesOfTerrain, int routeWords, ulong[] destination, int destinationOffset) {
            for (int w = 0; w < sourceWords; w++) {
                ulong bits = source[sourceOffset + w];
                while (bits != 0UL) {
                    int terrain = (w << 6) + math.tzcnt(bits);
                    bits &= bits - 1UL;
                    if (terrain >= terrainCount) continue;

                    int from = terrain * routeWords;
                    for (int j = 0; j < routeWords; j++) destination[destinationOffset + j] |= routesOfTerrain[from + j];
                }
            }
        }
    }
}
