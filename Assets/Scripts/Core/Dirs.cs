using System.Runtime.CompilerServices;


namespace WFC.Core {
    public enum Direction : byte {
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
        public static readonly int Count = 8;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Direction Opposite(Direction d) => (Direction)(((int)d + 4) & 7);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsCardinal(Direction d) => ((int)d & 1) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Dx(Direction d) =>
            d switch {
                Direction.NE or Direction.E or Direction.SE => 1,
                Direction.SW or Direction.W or Direction.NW => -1,
                _                                           => 0
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Dy(Direction d) =>
            d switch {
                Direction.N or Direction.NE or Direction.NW => 1,
                Direction.SE or Direction.S or Direction.SW => -1,
                _                                           => 0
            };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int PackWindowMasks(int mustMask, int mayMask) => (mustMask << Dirs.Count) | mayMask;
    }
}
