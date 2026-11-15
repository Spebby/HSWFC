// OverlayMasks.cs
// Bit layout of the 8-bit neighbour mask and the Blob47 reduction. Pure integer logic, no engine types.
// Global namespace so tileset tooling can use it unqualified.
//
// A bit is set when the neighbour in that direction matches the rule being drawn ("paint towards that side").
//       NW(128) N(1)  NE(2)
//        W(64)   .    E(4)
//       SW(32)  S(16) SE(8)
using System.Collections.Generic;
using System.Text;


namespace WFC.Core {
    public static class OverlayMasks {
        public const int N = 1, NE = 2, E = 4, SE = 8, S = 16, SW = 32, W = 64, NW = 128;
        public const int BLOB_COUNT = 47;
        public const int DIAGONAL_BITS = NE | SE | SW | NW;
        public static readonly int[] CornerBit = { NE, SE, SW, NW };
        public static readonly int[] CornerEdges = { N | E, E | S, S | W, W | N };

        static readonly string[] NAMES = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        static readonly int[] REDUCE_TABLE = new int[256];
        static readonly int[] BLOB_INDEX = new int[256];
        static readonly int[] CANONICAL;

        static OverlayMasks() {
            bool[] seen = new bool[256];
            for (int m = 0; m < 256; m++) {
                REDUCE_TABLE[m]       = ComputeReduce(m);
                seen[REDUCE_TABLE[m]] = true;
            }

            List<int> list = new();
            for (int m = 0; m < 256; m++) {
                if (!seen[m]) continue;
                BLOB_INDEX[m] = list.Count;
                list.Add(m);
            }

            CANONICAL = list.ToArray();
        }

        static int ComputeReduce(int m) {
            int r = m & (N | E | S | W);
            for (int c = 0; c < 4; c++) {
                if ((m & CornerBit[c]) == 0 || (m & CornerEdges[c]) != 0) continue;
                r |= CornerBit[c];
            }

            return r;
        }

        /// <summary>Edge16 index of the four side bits: N1 E2 S4 W8.</summary>
        public static int EdgeIndex(int m) => (m & N) | ((m >> 1) & 2) | ((m >> 2) & 4) | ((m >> 3) & 8);

        /// <summary>Drops every diagonal whose bounding side is set. Gives exactly 47 distinct masks.</summary>
        public static int Reduce(int m) => REDUCE_TABLE[m & 255];

        /// <summary>Blob47 slot (0..46) of a raw 8-bit mask. Slot 0 is the empty mask.</summary>
        public static int BlobIndex(int rawMask) => BLOB_INDEX[REDUCE_TABLE[rawMask & 255]];

        /// <summary>
        /// Reduction used by the usual autotile sheets (Godot, GameMaker, cr31): the bit means "this neighbour is the same terrain
        /// as the tile", and a diagonal only counts when BOTH of its bounding sides are set. (Reduce above is the complement
        /// convention: the bit means "this neighbour is the overlay terrain", a diagonal counts when both sides are clear.)
        /// </summary>
        public static int StandardCanonical(int m) {
            m &= 255;
            for (int c = 0; c < 4; c++) {
                if ((m & CornerBit[c]) == 0 || (m & CornerEdges[c]) == CornerEdges[c]) continue;
                m &= ~CornerBit[c];
            }

            return m;
        }

        /// <summary>Overlay mask (neighbours that are the overlay terrain) for the sheet tile with this standard mask (neighbours that are the same terrain).</summary>
        public static int FromStandardMask(int standard) => Reduce(~standard & 255);

        /// <summary>Standard sheet mask of the tile that draws the edge art for this overlay mask.</summary>
        public static int ToStandardMask(int overlayMask) => StandardCanonical(~overlayMask & 255);

        /// <summary>The reduced 8-bit mask of Blob47 slot i.</summary>
        public static int BlobSlotMask(int i) =>
            CANONICAL[i < 0 ? 0 : i >= CANONICAL.Length ? CANONICAL.Length - 1 : i];

        /// <summary>True when the slot's mask contains a diagonal bit (31 of the 47 slots).</summary>
        public static bool IsCornerSlot(int slot) => (BlobSlotMask(slot) & DIAGONAL_BITS) != 0;

        /// <summary>Readable name of a mask, e.g. "N+E+SW", or "-" for none.</summary>
        public static string MaskName(int mask) {
            List<string> parts = new();
            for (int b = 0; b < 8; b++) {
                if (((mask >> b) & 1) == 0) continue;
                parts.Add(NAMES[b]);
            }

            return parts.Count == 0 ? "-" : string.Join("+", parts);
        }

        public static string DescribeBlob47() {
            StringBuilder sb = new("Blob47 slots (index: sides the sprite paints towards)\n");
            for (int i = 0; i < CANONICAL.Length; i++) {
                sb.AppendLine($"{i}: {(CANONICAL[i] == 0 ? "(none, leave empty)" : MaskName(CANONICAL[i]))}");
            }

            return sb.ToString();
        }
    }
}