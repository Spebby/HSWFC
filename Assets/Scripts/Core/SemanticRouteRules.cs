using System;
using System.Collections.Generic;

namespace WFC.Core {
    /// <summary>
    /// Compiles adjacency on complete root-to-leaf routes. Two routes ending at
    /// the same terrain can have different supports because their ancestors differ.
    /// </summary>
    internal static class SemanticRouteRules {
        const long MaxSupportBytes = 64L * 1024 * 1024;

        public static ulong[] Build(CompiledSemanticGraph graph, WFCRules rules, int routeWords) {
            int routeCount = graph.RouteTerrainIds.Length;
            long tableWords = (long)Dirs.Count * routeCount * routeWords;
            if (tableWords > MaxSupportBytes / sizeof(ulong))
                throw new ArgumentException("Semantic route compatibility exceeds 64 MiB. Simplify the graph.", nameof(graph));

            var support = new ulong[(int)tableWords];
            var routesByTerrain = new ulong[checked(rules.count * routeWords)];
            var routesByNode = new Dictionary<int, ulong[]>();
            foreach (SemanticAdjacency rule in graph.Adjacencies) {
                if (!routesByNode.ContainsKey(rule.NeighborId))
                    routesByNode.Add(rule.NeighborId, new ulong[routeWords]);
            }
            for (int route = 0; route < routeCount; route++) {
                int word = route / 64;
                ulong bit = 1UL << (route % 64);
                routesByTerrain[graph.RouteTerrainIds[route] * routeWords + word] |= bit;
                for (int entry = graph.RouteNodeOffsets[route]; entry < graph.RouteNodeOffsets[route + 1]; entry++) {
                    if (routesByNode.TryGetValue(graph.RouteNodes[entry], out ulong[] nodeRoutes))
                        nodeRoutes[word] |= bit;
                }
            }

            for (int direction = 0; direction < Dirs.Count; direction++) {
                var targets = new Dictionary<int, List<int>>();
                foreach (SemanticAdjacency rule in graph.Adjacencies) {
                    if ((int)rule.Direction != direction) continue;
                    if (!targets.TryGetValue(rule.SourceId, out List<int> list)) {
                        list = new List<int>();
                        targets.Add(rule.SourceId, list);
                    }
                    list.Add(rule.NeighborId);
                }

                bool hasSemanticRules = targets.Count != 0;
                ulong[] terrainRules = hasSemanticRules ? rules.directionalSupport : rules.support;
                ulong[] terrainSupport = terrainRules == null ? null : ExpandTerrainSupport(
                    rules, direction, terrainRules, routesByTerrain, routeWords);

                for (int route = 0; route < routeCount; route++) {
                    int output = (direction * routeCount + route) * routeWords;
                    int terrain = graph.RouteTerrainIds[route] * routeWords;
                    if (!hasSemanticRules) {
                        Array.Copy(terrainSupport, terrain, support, output, routeWords);
                        continue;
                    }

                    // A rule is inherited by all refinements of its endpoints on
                    // these routes. A shared leaf never grants another route's rules.
                    for (int entry = graph.RouteNodeOffsets[route]; entry < graph.RouteNodeOffsets[route + 1]; entry++) {
                        if (!targets.TryGetValue(graph.RouteNodes[entry], out List<int> list)) continue;
                        foreach (int node in list) {
                            ulong[] incoming = routesByNode[node];
                            for (int word = 0; word < routeWords; word++)
                                support[output + word] |= incoming[word];
                        }
                    }

                    if (terrainSupport != null) {
                        for (int word = 0; word < routeWords; word++)
                            support[output + word] &= terrainSupport[terrain + word];
                    }
                }
            }
            return support;
        }

        static ulong[] ExpandTerrainSupport(
            WFCRules rules, int direction, ulong[] allowed, ulong[] routesByTerrain, int routeWords) {
            var expanded = new ulong[routesByTerrain.Length];
            for (int terrain = 0; terrain < rules.count; terrain++) {
                int input = (direction * rules.count + terrain) * rules.wordsPerDomain;
                int output = terrain * routeWords;
                for (int neighbor = 0; neighbor < rules.count; neighbor++) {
                    if ((allowed[input + neighbor / 64] & (1UL << (neighbor % 64))) == 0) continue;
                    for (int word = 0; word < routeWords; word++)
                        expanded[output + word] |= routesByTerrain[neighbor * routeWords + word];
                }
            }
            return expanded;
        }
    }
}
