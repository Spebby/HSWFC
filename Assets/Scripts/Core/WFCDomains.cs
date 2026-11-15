using System;


namespace WFC.Core {
    /// <summary>
    /// Helpers for building the optional initial domain array. Pin a cell to a tile, restrict it to a set, or a rectangle.
    /// This acts as the hook for anything placed before solving (reserved areas, fixed tiles, borders).
    /// </summary>
    public static class WFCDomains {
        public static ulong[] Full(int tileCount, int width, int height) {
            ulong[] d                               = new ulong[width * height];
            ulong   full                            = tileCount == 64 ? ulong.MaxValue : (1UL << tileCount) - 1;
            for (int i = 0; i < d.Length; i++) d[i] = full;
            return d;
        }

        public static void Pin(Span<ulong> domains, int width, int x, int y, int tile) {
            domains[y * width + x] = 1UL << tile;
        }

        // used for restricting a set of tiles that can appear in one or more cells, but unused for now
        public static void Restrict(Span<ulong> domains, int width, int x, int y, ulong allowedMask) {
            domains[y * width + x] &= allowedMask;
        }

        public static void RestrictRect(Span<ulong> domains, int width, int x0, int y0, int w, int h, ulong allowedMask) {
            for (int y = y0; y < y0 + h; y++) {
                for (int x = x0; x < x0 + w; x++) {
                    domains[y * width + x] &= allowedMask;
                }
            }
        }
    }
}