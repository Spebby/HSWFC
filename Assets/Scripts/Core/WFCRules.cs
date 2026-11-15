//   Support[d * Count + t]   tiles allowed in direction d from tile t (8 directions, bit order of Dirs)
//   MustExist[d]             tiles that may only be placed where the neighbour in direction d is inside the grid
//   Window*                  neighbourhood constraints (see WFCRulesBuilder.AddWindow)
using System;


namespace WFC.Core {
    /// <summary>
    /// Immutable, solver-ready rules. Build by WFCRulesBuilder.
    /// </summary>
    /// <remarks>
    /// Support[d * Count + t]
    /// </remarks>
    public sealed class WFCRules {
        public const int MAX_TILES = 64;

        /// <summary>64-bit words per window feasibility table: one bit per (must, may) pair of 8-bit masks.</summary>
        public const int FEASIBLE_WORDS = 1024;

        public readonly int count;
        public readonly float[] weight;
        public readonly ulong[] support;
        public readonly ulong[] mustExist;
        public readonly int windowCount;
        public readonly ulong[] windowTargets;
        public readonly byte[] windowMarker;
        public readonly ulong[] windowFeasible;

        internal WFCRules(int count, float[] weight, ulong[] support, ulong[] mustExist,
                          ulong[] windowTargets, byte[] windowMarker, ulong[] windowFeasible) {
            this.count          = count;
            this.weight         = weight;
            this.support        = support;
            this.mustExist      = mustExist;
            windowCount         = windowTargets.Length;
            this.windowTargets  = windowTargets;
            this.windowMarker   = windowMarker;
            this.windowFeasible = windowFeasible;
        }

        public ulong FullMask => count == 64 ? ulong.MaxValue : (1UL << count) - 1;

        /// <summary>Null when consistent. Checks that b is allowed in direction d from a exactly when a is allowed in the opposite direction from b.</summary>
        public string Validate() {
            for (int d = 0; d < 8; d++) {
                int opposite = Dirs.Opposite(d);
                for (int a = 0; a < count; a++) {
                    for (int b = 0; b < count; b++) {
                        bool ab = IsAllowed(a, d, b);
                        bool ba = IsAllowed(b, opposite, a);

                        if (ab != ba) {
                            return $"Support is not symmetric: tile {a} -> {b} in direction {(Direction)d} disagrees with the opposite direction.";
                        }
                    }
                }
            }
            return null;

            bool IsAllowed(int tile, int direction, int neighbour) => ((support[direction * count + tile] >> neighbour) & 1UL) != 0UL;
        }
    }

    /// <summary>Collects logical rules and compiles them into a WFCRules.</summary>
    public sealed class WFCRulesBuilder {
        readonly float[] _weight;
        readonly ulong[] _cardinal, _diagonal;
        readonly System.Collections.Generic.List<DirectionalRule> _rules = new();
        readonly System.Collections.Generic.List<Window> _windows = new();

        struct DirectionalRule {
            public int tile, dir;
            public ulong allowed;
            public bool mustExist;
        }

        struct Window {
            public int marker;
            public ulong targets;
            public ulong[] feasible;
        }

        /// <summary>
        /// Off (default): diagonal neighbours are unconstrained and the diagonal lists are ignored.
        /// On: a diagonal neighbour must be the tile itself or listed via AllowDiagonal. Never derived from the cardinal lists.
        /// </summary>
        public bool diagonalRules;

        public WFCRulesBuilder(int tileTileCount) {
            if (tileTileCount is < 1 or > WFCRules.MAX_TILES) throw new ArgumentException("tileCount must be 1..64");
            TileCount        = tileTileCount;
            _weight   = new float[TileCount];
            _cardinal = new ulong[TileCount];
            _diagonal = new ulong[TileCount];
            for (int i = 0; i < TileCount; i++) _weight[i] = 1f;
        }

        public int TileCount { get; }

        ulong Full => TileCount == 64 ? ulong.MaxValue : (1UL << TileCount) - 1;
        static ulong Bit(int t) => 1UL << t;

        void Check(int t) {
            if (t < 0 || t >= TileCount) throw new ArgumentOutOfRangeException(nameof(TileCount));
        }

        public WFCRulesBuilder SetWeight(int tile, float w) {
            Check(tile);
            _weight[tile] = Math.Max(1e-4f, w);
            return this;
        }

        /// <summary>a and b may touch across N, E, S and W (symmetric). A tile may always touch itself.</summary>
        public WFCRulesBuilder AllowCardinal(int a, int b) {
            Check(a);
            Check(b);
            _cardinal[a] |= Bit(b);
            _cardinal[b] |= Bit(a);
            return this;
        }

        /// <summary>a and b may touch across NE, SE, SW and NW (symmetric). Only used when DiagonalRules is on.</summary>
        public WFCRulesBuilder AllowDiagonal(int a, int b) {
            Check(a);
            Check(b);
            _diagonal[a] |= Bit(b);
            _diagonal[b] |= Bit(a);
            return this;
        }

        /// <summary>
        /// Directional rule: the neighbour of <paramref name="tile"/> in <paramref name="dir"/> must be in <paramref name="allowed"/>.
        /// The opposite direction is restricted accordingly, so the rule is symmetric. With mustExist the tile cannot be
        /// placed where that neighbour would be outside the grid.
        /// </summary>
        public WFCRulesBuilder Restrict(int tile, Direction dir, ulong allowed, bool mustExist = false) {
            Check(tile);
            _rules.Add(new DirectionalRule
                          { tile = tile, dir = (int)dir, allowed = allowed & Full, mustExist = mustExist });
            return this;
        }

        /// <summary>
        /// Neighbourhood constraint for visual availability. For every cell whose tile is in <paramref name="targets"/>,
        /// the 8-bit mask "neighbour is the marker tile" must be renderable. Out-of-grid neighbours count as not marker.
        /// Mask 0 must be renderable.
        /// </summary>
        public WFCRulesBuilder AddWindow(int marker, ulong targets, bool[] renderable) {
            Check(marker);
            if (renderable is not { Length: 256 })
                throw new ArgumentException("renderable needs 256 entries");
            if (!renderable[0]) throw new ArgumentException("mask 0 must be renderable");
            targets &= Full;
            if (targets == 0) return this;
            _windows.Add(new Window { marker = marker, targets = targets, feasible = BuildFeasible(renderable) });
            return this;
        }

        /// <summary>
        /// Builds a lookup table indicating which combinations of required and possible
        /// marker neighbours admit at least one renderable configuration.
        /// </summary>
        /// <param name="renderable">
        /// A 256-entry lookup table indicating whether each 8-neighbour marker mask
        /// can be rendered.
        /// </param>
        /// <returns>
        /// A bitset indexed by <c>(mustMask &lt;&lt; 8) | mayMask</c>. A set bit means
        /// that at least one renderable mask exists between the two bounds.
        /// </returns>
        public static ulong[] BuildFeasible(bool[] renderable) {
            ulong[] f = new ulong[WFCRules.FEASIBLE_WORDS];
            for (int mayMask = 0; mayMask < 256; mayMask++) {
                for (int mustMask = 0; mustMask < 256; mustMask++) {
                    // every required marker neighbour must also be a possible marker neighbour
                    if ((mustMask & ~mayMask) != 0) continue;

                    // bits in free may independently be either present or absent
                    int  free = mayMask & ~mustMask;
                    bool ok   = false;

                    // enumerate every mask satisfying mustMask ⊆ mask ⊆ mayMask
                    for (int sub = free;; sub = (sub - 1) & free) {
                        if (renderable[mustMask | sub]) {
                            ok = true;
                            break;
                        }

                        if (sub == 0) break;
                    }

                    if (!ok) continue;
                    // pack the (must, may) pair into a single 16-bit lookup
                    int i = (mustMask << 8) | mayMask;
                    f[i >> 6] |= 1UL << (i & 63);
                }
            }

            return f;
        }

        /// <summary>
        /// Builds the solver's adjacency and window constraint tables from the configured rules.
        /// </summary>
        /// <returns></returns>
        public WFCRules Build() {
            ulong   full    = Full;

            // init support masks for every tile & dir
            ulong[] support = new ulong[8 * TileCount];
            for (int t = 0; t < TileCount; t++) {
                for (int d = 0; d < 8; d++) {
                    ulong s;
                    if (Dirs.IsCardinal(d)) s = _cardinal[t] | Bit(t);
                    else s                    = diagonalRules ? _diagonal[t] | Bit(t) : full;
                    support[d * TileCount + t] = s;
                }
            }

            // apply explicit dir rules to the support masks & prune invalid rules
            ulong[] must = new ulong[8];
            foreach (DirectionalRule r in _rules) {
                support[r.dir * TileCount + r.tile] &= r.allowed;
                int opp = Dirs.Opposite(r.dir);
                for (int x = 0; x < TileCount; x++) {
                    if (((r.allowed >> x) & 1) != 0) continue;
                    support[opp * TileCount + x] &= ~Bit(r.tile);
                }

                // record tiles that must occur in specified dir at boundaries
                if (r.mustExist) must[r.dir] |= Bit(r.tile);
            }

            // flatten window constraints into contiguous arrays for the unmanaged kernel
            int     wc       = _windows.Count;
            ulong[] targets  = new ulong[wc];
            byte[]  markers  = new byte[wc];
            ulong[] feasible = new ulong[Math.Max(1, wc) * WFCRules.FEASIBLE_WORDS];
            for (int k = 0; k < wc; k++) {
                targets[k] = _windows[k].targets;
                markers[k] = (byte)_windows[k].marker;
                Array.Copy(
                    _windows[k].feasible,
                    0,
                    feasible,
                    k * WFCRules.FEASIBLE_WORDS,
                    WFCRules.FEASIBLE_WORDS);
            }

            return new WFCRules(
                TileCount,
                (float[])_weight.Clone(),
                support,
                must,
                targets,
                markers,
                feasible);
        }
    }
}
