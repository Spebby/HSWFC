using static System.Math;


// This is mostly LLM generated, I figure that is fine because it is out of scope for a basic WFC impl.
namespace WFC.Core {
    /// <summary>
    /// Optional regional weighting. Each tile prefers the part of the noise range around its "biomeCenter".
    /// Produces per-cell weight array that <see cref="WFCSolver.Solve"/> accepts.
    /// </summary>
    /// <remarks>
    /// Extra for HSWFC. Arguably more powerful since metatiles could be given regional weights & children can inherit it.
    /// </remarks>
    public static class BiomeWeights {
        /// <param name="weight">Base weight per tile.</param>
        /// <param name="biomeCenter">Preferred noise value (0..1) per tile.</param>
        /// <param name="regionalBias">0 = ignore the noise, 1 = strong regional preference.</param>
        /// <param name="noiseScale">Cells per noise lattice step; larger = bigger regions.</param>
        /// <returns>Weights, cell-major: [(y * width + x) * count + tile].</returns>
        public static float[] Build(int width, int height, ulong seed, float[] weight, float[] biomeCenter,
                                    float regionalBias, float noiseScale = 7f) {
            int     n     = weight.Length;
            float[] noise = Noise(width, height, Max(0.5f, noiseScale), seed);
            float[] w     = new float[width * height * n];
            for (int i = 0; i < width * height; i++) {
                for (int t = 0; t < n; t++) {
                    float dv = noise[i] - biomeCenter[t];
                    w[i * n + t] = Max(1e-4f, weight[t] * (float)Exp(-20f * regionalBias * dv * dv));
                }
            }

            return w;
        }

        /// <summary>Smooth value noise normalised to 0..1.</summary>
        /// <remarks>LLM generated based on the commented code underneath. Wanted to keep this assembly engine-agnostic
        /// so Unity's perlin noise func wasn't a good option.</remarks>
        public static float[] Noise(int w, int h, float sc, ulong seed) {
            SplitMix64 rng                          = new(seed);
            int        gw                           = (int)Ceiling(w / sc) + 2, gh = (int)Ceiling(h / sc) + 2;
            float[]    g                            = new float[gw * gh];
            for (int i = 0; i < g.Length; i++) g[i] = rng.NextFloat();
            float[] o                               = new float[w * h];
            float   lo                              = float.MaxValue, hi = float.MinValue;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    float fx = x / sc,  fy = y / sc;
                    int   ix = (int)fx, iy = (int)fy;
                    float tx = fx - ix, ty = fy - iy;
                    tx *= tx * (3 - 2 * tx);
                    ty *= ty * (3 - 2 * ty);
                    float a = g[iy * gw + ix],
                          b = g[iy * gw + ix + 1],
                          c = g[(iy + 1) * gw + ix],
                          d = g[(iy + 1) * gw + ix + 1];
                    float v = a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
                    o[y * w + x] = v;
                    lo           = Min(lo, v);
                    hi           = Max(hi, v);
                }
            }

            float range                             = Max(1e-6f, hi - lo);
            for (int i = 0; i < o.Length; i++) o[i] = (o[i] - lo) / range;
            return o;
        }

        /*
        // LLM reference code
        public static float[] Noise(int w, int h, float sc, ulong seed) {
            SplitMix64 rng     = new(seed);
            float      offsetX = rng.NextFloat() * 10000f;
            float      offsetY = rng.NextFloat() * 10000f;

            float[] noise = new float[w * h];

            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    noise[y * w + x] = Mathf.Clamp01(Mathf.PerlinNoise(offsetX + x / sc, offsetY + y / sc));
                }
            }

            return noise;
        }
        */
    }
}