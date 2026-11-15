namespace WFC.Core {
    /// <summary>
    /// Small deterministic PRNG.
    /// </summary>
    /// <remarks>
    /// https://rosettacode.org/wiki/Pseudo-random_numbers/Splitmix64
    /// </remarks>
    public struct SplitMix64 {
        ulong _s;
        public SplitMix64(ulong seed) => _s = seed;

        public ulong NextULong() {
            unchecked {
                _s += 0x9E3779B97F4A7C15UL;
                ulong z = _s;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Uniform float in [0, 1).</summary>
        public float NextFloat() => (NextULong() >> 40) * (1f / 16777216f);
    }
}