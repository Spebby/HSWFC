using System.Runtime.CompilerServices;


namespace WFC.Core {
    public enum Direction {
        N = 0,
        NE = 1,
        E = 2,
        SE = 3,
        S = 4,
        SW = 5,
        W = 6,
        NW = 7
    }

    /// <summary>
    /// Convenience functions for neighbour directions. Order matches bit order of <see cref="OverlayMasks"/>
    /// </summary>
    /// <remarks>
    /// +y is north. A direction index d corresponds to mask bit (1 << d)
    /// </remarks>
    public static class Dirs {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Opposite(int d) => (d + 4) & 7;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCardinal(int d) => (d & 1) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Dx(int d) {
            return d switch {
                1 or 2 or 3 => 1,
                5 or 6 or 7 => -1,
                _           => 0
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Dy(int d) {
            return d switch {
                0 or 1 or 7 => 1,
                3 or 4 or 5 => -1,
                _           => 0
            };
        }
    }
}
