using System;


namespace WFC.Core {
    /// <summary>
    /// Immutable, solver-ready rules. Built by WFCRulesBuilder.
    /// </summary>
    /// <remarks>
    /// Support[(d * Count + t) * WordsPerDomain + w] contains the tiles
    /// allowed in direction d from tile t, stored as a multiword bitset.
    /// MustExist[d * WordsPerDomain + w] contains tiles that require an
    /// in-grid neighbour in direction d.
    /// Window arrays are flattened by window index. A window's marker is a set of tiles, stored like
    /// a domain, so a marker terrain can be several tiles once routes are expanded.
    /// </remarks>
    public sealed class WFCRules {
        /// <summary>64-bit words per tile domain.</summary>
        public readonly int wordsPerDomain;

        /// <summary>64-bit words per window feasibility table: one bit per (must, may) pair.</summary>
        public const int FEASIBLE_WORDS = 1024;

        public readonly int count;
        public readonly float[] weight;
        public readonly ulong[] support;
        // Only the explicit directional rules, with no terrain whitelist. Null when there are none. A semantic graph
        // that defines a direction's adjacency replaces the whitelist there but must still honour these masks.
        internal readonly ulong[] directionalSupport;
        public readonly ulong[] mustExist;
        public readonly int windowCount;
        public readonly ulong[] windowTargets;
        public readonly ulong[] windowMarkers;
        public readonly ulong[] windowFeasible;

        internal WFCRules(int count, int wordsPerDomain, float[] weight, ulong[] support, ulong[] directionalSupport, ulong[] mustExist, ulong[] windowTargets, ulong[] windowMarkers, ulong[] windowFeasible) {
            this.count          = count;
            this.wordsPerDomain = wordsPerDomain;
            this.weight         = weight;
            this.support        = support;
            this.directionalSupport = directionalSupport;
            this.mustExist      = mustExist;
            windowCount         = windowMarkers.Length / wordsPerDomain;
            this.windowTargets  = windowTargets;
            this.windowMarkers  = windowMarkers;
            this.windowFeasible = windowFeasible;
        }

        /// <summary>Full mask for the first 64 tiles. Use a multiword mask for the complete domain.</summary>
        public ulong FullMask => count >= 64 ? ulong.MaxValue : (1UL << count) - 1UL;

        /// <summary>Null when consistent. Checks that adjacency rules are symmetric across opposite directions.</summary>
        public string Validate() {
            for (int d = 0; d < Dirs.Count; d++) {
                int opposite = (int)Dirs.Opposite((Direction)d);
                for (int a = 0; a < count; a++) {
                    int abOffset = (d * count + a) * wordsPerDomain;
                    for (int b = 0; b < count; b++) {
                        int  baOffset = (opposite * count + b) * wordsPerDomain;
                        bool ab       = Has(support, abOffset, b);
                        bool ba       = Has(support, baOffset, a);

                        if (ab != ba) {
                            return $"Support is not symmetric: tile {a} -> {b} in direction {(Direction)d} disagrees with the opposite direction.";
                        }
                    }
                }
            }

            return null;
        }

        static bool Has(ulong[] mask, int offset, int tile) => (mask[offset + (tile >> 6)] & (1UL << (tile & 63))) != 0;
    }

    /// <summary>Collects logical rules and compiles them into a WFCRules.</summary>
    public sealed class WFCRulesBuilder {
        readonly float[] _weight;
        readonly ulong[] _cardinal, _diagonal;
        readonly System.Collections.Generic.List<DirectionalRule> _rules = new();
        readonly System.Collections.Generic.List<Window> _windows = new();

        struct DirectionalRule {
            public int tile;
            public Direction dir;
            public ulong[] allowed;
            public bool mustExist;
        }

        struct Window {
            public int marker;
            public ulong[] targets, feasible;
        }

        /// <summary>
        /// Off (default): diagonal neighbours are unconstrained and the diagonal lists are ignored.
        /// On: a diagonal neighbour must be the tile itself or listed via AllowDiagonal. Never derived from the cardinal lists.
        /// </summary>
        public bool diagonalRules;

        public WFCRulesBuilder(int tileCount) {
            if (tileCount < 1) throw new ArgumentException("tileCount must be positive", nameof(tileCount));

            TileCount      = tileCount;
            WordsPerDomain = checked((tileCount + 63) / 64);
            _weight        = new float[tileCount];
            _cardinal      = new ulong[checked(tileCount * WordsPerDomain)];
            _diagonal      = new ulong[checked(tileCount * WordsPerDomain)];

            for (int i = 0; i < tileCount; i++) _weight[i] = 1f;
        }

        public int TileCount { get; }
        public int WordsPerDomain { get; }

        void Check(int tile) {
            if (tile < 0 || tile >= TileCount) throw new ArgumentOutOfRangeException(nameof(tile));
        }

        static bool Has(ulong[] mask, int tile) => (mask[tile >> 6] & (1UL << (tile & 63))) != 0;

        void SetDomainBit(ulong[] domains, int tile, int bit) {
            int offset = tile * WordsPerDomain;
            domains[offset + (bit >> 6)] |= 1UL << (bit & 63);
        }

        ulong[] FullWords() {
            ulong[] full = new ulong[WordsPerDomain];
            Array.Fill(full, ulong.MaxValue);
            MaskFinalWord(full);
            return full;
        }

        void MaskFinalWord(ulong[] mask) {
            int remainder = TileCount & 63;
            if (remainder == 0) return;
            mask[WordsPerDomain - 1] &= (1UL << remainder) - 1UL;
        }

        static bool Any(ulong[] mask) {
            foreach (ulong word in mask) {
                if (word != 0) return true;
            }

            return false;
        }

        public WFCRulesBuilder SetWeight(int tile, float weight) {
            Check(tile);
            _weight[tile] = Math.Max(1e-4f, weight);
            return this;
        }

        /// <summary>a and b may touch across N, E, S and W (symmetric). A tile may always touch itself.</summary>
        public WFCRulesBuilder AllowCardinal(int a, int b) {
            Check(a);
            Check(b);
            SetDomainBit(_cardinal, a, b);
            SetDomainBit(_cardinal, b, a);
            return this;
        }

        /// <summary>a and b may touch across NE, SE, SW and NW (symmetric). Only used when diagonalRules is on.</summary>
        public WFCRulesBuilder AllowDiagonal(int a, int b) {
            Check(a);
            Check(b);
            SetDomainBit(_diagonal, a, b);
            SetDomainBit(_diagonal, b, a);
            return this;
        }

        /// <summary>
        /// Restricts the allowed neighbours using a single 64-bit mask.
        /// Only tile indices 0..63 can be specified by this overload.
        /// </summary>
        public WFCRulesBuilder Restrict(int tile, Direction dir, ulong allowed, bool mustExist = false) {
            ulong[] mask = new ulong[WordsPerDomain];
            mask[0] = allowed;
            return Restrict(tile, dir, mask, mustExist);
        }

        /// <summary>
        /// Restricts the allowed neighbours using one word per domain word.
        /// </summary>
        public WFCRulesBuilder Restrict(int tile, Direction dir, ReadOnlySpan<ulong> allowed, bool mustExist = false) {
            Check(tile);
            if (allowed.Length != WordsPerDomain)
                throw new ArgumentException("allowed needs one entry per domain word", nameof(allowed));

            ulong[] mask = allowed.ToArray();
            MaskFinalWord(mask);
            _rules.Add(new DirectionalRule { tile = tile, dir = dir, allowed = mask, mustExist = mustExist });
            return this;
        }

        /// <summary>
        /// Adds a neighbourhood constraint using a single 64-bit target mask.
        /// Only tile indices 0..63 can be specified by this overload.
        /// </summary>
        public WFCRulesBuilder AddWindow(int marker, ulong targets, bool[] renderable) {
            ulong[] mask = new ulong[WordsPerDomain];
            mask[0] = targets;
            return AddWindow(marker, mask, renderable);
        }

        /// <summary>
        /// Adds a neighbourhood constraint using one word per domain word.
        /// </summary>
        public WFCRulesBuilder AddWindow(int marker, ReadOnlySpan<ulong> targets, bool[] renderable) {
            Check(marker);
            if (targets.Length != WordsPerDomain)
                throw new ArgumentException("targets needs one entry per domain word", nameof(targets));
            if (renderable is not { Length: 256 })
                throw new ArgumentException("renderable needs 256 entries", nameof(renderable));
            if (!renderable[0])
                throw new ArgumentException("mask 0 must be renderable", nameof(renderable));

            ulong[] mask = targets.ToArray();
            MaskFinalWord(mask);
            if (!Any(mask)) return this;

            _windows.Add(new Window {
                marker   = marker,
                targets  = mask,
                feasible = BuildFeasible(renderable)
            });
            return this;
        }

        /// <summary>
        /// Builds a lookup table indicating which combinations of required and possible
        /// marker neighbours admit at least one renderable configuration.
        /// </summary>
        /// <param name="renderable">A 256-entry lookup table indicating whether each 8-neighbour marker mask can be rendered.</param>
        /// <returns>
        /// A bitset indexed by <c>(mustMask &lt;&lt; 8) | mayMask</c>. A set bit means
        /// that at least one renderable mask exists between the two bounds.
        /// </returns>
        public static ulong[] BuildFeasible(bool[] renderable) {
            if (renderable is not { Length: 256 })
                throw new ArgumentException("renderable needs 256 entries", nameof(renderable));
            if (!renderable[0])
                throw new ArgumentException("mask 0 must be renderable", nameof(renderable));

            ulong[] f = new ulong[WFCRules.FEASIBLE_WORDS];
            for (int mayMask = 0; mayMask < 256; mayMask++) {
                for (int mustMask = 0; mustMask < 256; mustMask++) {
                    // Every required marker neighbour must also be possible.
                    if ((mustMask & ~mayMask) != 0) continue;

                    // Enumerate every mask satisfying mustMask ⊆ mask ⊆ mayMask.
                    int  free = mayMask & ~mustMask;
                    bool ok   = false;
                    for (int sub = free;; sub = (sub - 1) & free) {
                        if (renderable[mustMask | sub]) {
                            ok = true;
                            break;
                        }

                        if (sub == 0) break;
                    }

                    if (!ok) continue;

                    // Pack the (must, may) pair into a single 16-bit lookup.
                    int i = Dirs.PackWindowMasks(mustMask, mayMask);
                    f[i >> 6] |= 1UL << (i & 63);
                }
            }

            return f;
        }

        /// <summary>Builds the solver's adjacency and window constraint tables from the configured rules.</summary>
        public WFCRules Build() {
            ulong[] full = FullWords();

            // Initialise support masks for every tile and direction.
            ulong[] support = new ulong[checked(Dirs.Count * TileCount * WordsPerDomain)];
            for (int t = 0; t < TileCount; t++) {
                int tileOffset = t * WordsPerDomain;
                for (int d = 0; d < Dirs.Count; d++) {
                    Direction dir    = (Direction)d;
                    int       offset = (d * TileCount + t) * WordsPerDomain;

                    if (Dirs.IsCardinal(dir) || diagonalRules) {
                        ulong[] source = Dirs.IsCardinal(dir) ? _cardinal : _diagonal;
                        Array.Copy(source, tileOffset, support, offset, WordsPerDomain);
                        support[offset + (t >> 6)] |= 1UL << (t & 63);
                    } else {
                        Array.Copy(full, 0, support, offset, WordsPerDomain);
                    }
                }
            }

            // Apply explicit directional rules and their symmetric counterparts.
            ulong[] must = new ulong[checked(Dirs.Count * WordsPerDomain)];
            ulong[] directionalSupport = _rules.Count == 0 ? null : new ulong[support.Length];
            if (directionalSupport != null) {
                for (int offset = 0; offset < support.Length; offset += WordsPerDomain)
                    Array.Copy(full, 0, directionalSupport, offset, WordsPerDomain);
            }

            foreach (DirectionalRule rule in _rules) {
                int offset = ((int)rule.dir * TileCount + rule.tile) * WordsPerDomain;
                for (int w = 0; w < WordsPerDomain; w++) {
                    support[offset + w]            &= rule.allowed[w];
                    directionalSupport[offset + w] &= rule.allowed[w];
                }

                int   opposite = (int)Dirs.Opposite(rule.dir);
                ulong tileBit  = 1UL << (rule.tile & 63);
                int   tileWord = rule.tile >> 6;

                for (int x = 0; x < TileCount; x++) {
                    if (Has(rule.allowed, x)) continue;
                    int reverseOffset = (opposite * TileCount + x) * WordsPerDomain;
                    support[reverseOffset + tileWord]            &= ~tileBit;
                    directionalSupport[reverseOffset + tileWord] &= ~tileBit;
                }

                // Record tiles that require an in-grid neighbour in this direction.
                if (rule.mustExist) must[(int)rule.dir * WordsPerDomain + tileWord] |= tileBit;
            }

            // Flatten window constraints into contiguous arrays for the unmanaged kernel.
            int     wc       = _windows.Count;
            ulong[] targets  = new ulong[checked(wc * WordsPerDomain)];
            ulong[] markers  = new ulong[checked(wc * WordsPerDomain)];
            ulong[] feasible = new ulong[checked(Math.Max(1, wc) * WFCRules.FEASIBLE_WORDS)];

            for (int k = 0; k < wc; k++) {
                Window window = _windows[k];
                Array.Copy(window.targets, 0, targets, k * WordsPerDomain, WordsPerDomain);
                markers[k * WordsPerDomain + (window.marker >> 6)] |= 1UL << (window.marker & 63);
                Array.Copy(window.feasible, 0, feasible, k * WFCRules.FEASIBLE_WORDS, WFCRules.FEASIBLE_WORDS);
            }

            return new WFCRules(
                TileCount,
                WordsPerDomain,
                (float[])_weight.Clone(),
                support,
                directionalSupport,
                must,
                targets,
                markers,
                feasible);
        }
    }
}