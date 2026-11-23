using static WFC.Core.Dirs;
using static WFC.Core.WFCSolver;

namespace WFC.Core {
    unsafe partial struct WFCState {
        readonly struct SemanticBuffers {
            public readonly int Count, Words;
            public readonly int* ChildOffsets, LeafTerrains, Starts, CurrentNodes;
            public readonly int* Offsets, Nodes, Edges, Terrains, Results;
            public readonly float* ChildWeights;
            public readonly ulong* Support, Routes, BaseRoutes, Scratch;
            public readonly byte* Choices;

            public SemanticBuffers(ref WFCKernelArgs args) {
                Count = args.semanticRouteCount;
                Words = args.semanticRouteWords;
                ChildOffsets = args.semanticChildOffsets;
                LeafTerrains = args.semanticLeafTerrainIds;
                Starts = args.initialSemanticNodes;
                CurrentNodes = args.semanticNodes;
                Offsets = args.semanticRouteOffsets;
                Nodes = args.semanticRouteNodes;
                Edges = args.semanticRouteEdges;
                Terrains = args.semanticRouteTerrains;
                Results = args.resultSemanticRoutes;
                ChildWeights = args.semanticChildWeights;
                Support = args.semanticRouteSupport;
                Routes = args.semanticRoutes;
                BaseRoutes = args.baseSemanticRoutes;
                Scratch = args.semanticRouteScratch;
                Choices = args.semanticChoiceScratch;
            }
        }

        bool HasSemantics => _semantic.Count > 0;
        Domain SemanticRoutes(int cell) => new(_semantic.Routes, cell, _semantic.Words);

        // A node occurs at most once on a route through an acyclic graph.
        int RouteEntry(int route, int node) {
            for (int index = _semantic.Offsets[route]; index < _semantic.Offsets[route + 1]; index++) {
                if (_semantic.Nodes[index] == node) return index;
            }
            return -1;
        }

        bool InitializeSemanticCell(int cell) {
            int start = _semantic.Starts[cell];
            _semantic.CurrentNodes[cell] = start;
            Domain routes = SemanticRoutes(cell);
            Domain terrain = D(cell);
            routes.ClearAll();
            for (int route = 0; route < _semantic.Count; route++) {
                if (terrain.Has(_semantic.Terrains[route]) && RouteEntry(route, start) >= 0)
                    routes.Set(route);
            }
            ProjectSemanticTerrains(cell, routes);
            return !routes.IsEmpty;
        }

        void ResetSemanticNodes() {
            for (int cell = 0; cell < _cellCount; cell++)
                _semantic.CurrentNodes[cell] = _semantic.Starts[cell];
        }

        void RestoreSemanticAttempt() {
            ResetSemanticNodes();
            Domain.CopyRange(_semantic.Routes, _semantic.BaseRoutes, _cellCount, _semantic.Words);
        }

        bool IsCellResolved(int cell) {
            if (!HasSemantics) return D(cell).IsSingle;
            return _semantic.LeafTerrains[_semantic.CurrentNodes[cell]] >= 0 && SemanticRoutes(cell).IsSingle;
        }

        bool RefineSemanticCell(int cell) {
            int node = _semantic.CurrentNodes[cell];
            Domain routes = SemanticRoutes(cell);
            if (routes.IsEmpty) {
                _failedCell = cell;
                return false;
            }

            if (_semantic.LeafTerrains[node] >= 0) {
                // A leaf explicitly placed by the caller may have multiple ancestor
                // routes. Resolve that context before returning a complete layout.
                int remaining = (int)((double)_rng.NextFloat() * routes.Count());
                int route = routes.FirstSet();
                while (remaining-- > 0) route = routes.NextSet(route + 1);
                routes.SetOnly(route);
                return true;
            }

            int first = _semantic.ChildOffsets[node];
            int end = _semantic.ChildOffsets[node + 1];
            for (int edge = first; edge < end; edge++) _semantic.Choices[edge] = 0;

            for (int route = routes.NextSet(0); route >= 0; route = routes.NextSet(route + 1)) {
                int next = RouteEntry(route, node) + 1;
                _semantic.Choices[_semantic.Edges[next]] = 1;
            }

            int selected = PickSemanticEdge(first, end);
            for (int route = routes.NextSet(0); route >= 0; route = routes.NextSet(route + 1)) {
                int next = RouteEntry(route, node) + 1;
                if (_semantic.Edges[next] == selected) _semantic.CurrentNodes[cell] = _semantic.Nodes[next];
                else routes.Clear(route);
            }
            return true;
        }

        int PickSemanticEdge(int first, int end) {
            int count = 0;
            int last = -1;
            double total = 0.0;
            for (int edge = first; edge < end; edge++) {
                if (_semantic.Choices[edge] == 0) continue;
                count++;
                last = edge;
                total += _semantic.ChildWeights[edge];
            }
            if (count == 1) return last;

            // Zero-weight edges are fallback choices when propagation has removed
            // every positive-weight alternative.
            bool uniform = total == 0.0;
            double remaining = (double)_rng.NextFloat() * (uniform ? count : total);
            for (int edge = first; edge < end; edge++) {
                if (_semantic.Choices[edge] == 0) continue;
                double weight = uniform ? 1.0 : _semantic.ChildWeights[edge];
                if (weight == 0.0) continue;
                remaining -= weight;
                last = edge;
                if (remaining < 0.0) return edge;
            }
            return last;
        }

        bool ProjectSemanticTerrains(int cell, Domain routes) {
            Domain projection = new(_scratch, _wordsPerDomain);
            projection.ClearAll();
            for (int route = routes.NextSet(0); route >= 0; route = routes.NextSet(route + 1))
                projection.Set(_semantic.Terrains[route]);

            return D(cell).AndWith(projection);
        }

        bool PropagateSemanticAdjacency(int cell) {
            Domain source = SemanticRoutes(cell);
            Domain terrain = D(cell);
            // Windows operate on terrains. Feed their removals back into routes
            // before propagating, then project any route removals onto terrains.
            for (int route = source.NextSet(0); route >= 0; route = source.NextSet(route + 1)) {
                if (!terrain.Has(_semantic.Terrains[route])) source.Clear(route);
            }
            if (source.IsEmpty) {
                _failedCell = cell;
                return false;
            }
            if (ProjectSemanticTerrains(cell, source)) OnDomainChanged(cell);

            Domain supported = new(_semantic.Scratch, _semantic.Words);
            int x = cell % _w, y = cell / _w;
            for (int d = 0; d < Count; d++) {
                Direction direction = (Direction)d;
                int nx = x + Dx(direction), ny = y + Dy(direction);
                if (IsOutOfBounds(nx, ny, _w, _h)) continue;

                supported.ClearAll();
                for (int route = source.NextSet(0); route >= 0; route = source.NextSet(route + 1)) {
                    Domain mask = new(_semantic.Support, d * _semantic.Count + route, _semantic.Words);
                    for (int word = 0; word < _semantic.Words; word++)
                        supported.OrWord(word, mask.Word(word));
                }

                int neighbor = ny * _w + nx;
                Domain routes = SemanticRoutes(neighbor);
                if (!routes.AndWith(supported)) continue;
                if (routes.IsEmpty) {
                    _failedCell = neighbor;
                    return false;
                }
                OnDomainChanged(neighbor);
            }
            return true;
        }
    }
}
