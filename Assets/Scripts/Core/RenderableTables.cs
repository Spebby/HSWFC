namespace WFC.Core {
    /// <summary>
    /// Builds lookup tables describing which neighbour masks can be rendered with a given set of overlay components.
    /// </summary>
    public static class RenderableTables {
        /// <summary>
        /// Determines renderability for Edge16 overlays.
        /// </summary>
        /// <param name="sidesAvailable">
        /// Bit e set indicates that the side sprite at Edge16 index e exists. Bit 0 is ignored.
        /// </param>
        /// <param name="cornersAvailable">
        /// Bit k set indicates that the lone-corner sprite at index k exists. Corner order is clockwise NE..NW.
        /// </param>
        /// <param name="requireSides">
        /// If true, every non-empty side configuration must have its corresponding sprite.
        /// </param>
        /// <param name="requireCorners">
        /// If true, every exposed diagonal corner must have its corresponding sprite.
        /// </param>
        /// <returns>
        /// A 256-entry lookup table indexed by raw neighbour mask. True indicates that the mask can be rendered.
        /// </returns>
        public static bool[] Edge16(int sidesAvailable, int cornersAvailable, bool requireSides, bool requireCorners) {
            bool[] r = new bool[256];

            for (int i = 0; i < 256; i++) {
                int  edgeIndex    = OverlayMasks.EdgeIndex(i);
                bool hasEdge      = edgeIndex == 0 || ((sidesAvailable >> edgeIndex) & 1) != 0;
                bool isRenderable = !requireSides || hasEdge;
                for (int k = 0; k < 4 && isRenderable; k++) {
                    if (IsMissingExposedCorner(i, k)) isRenderable = false;
                }

                r[i] = isRenderable;
            }

            return r;
            bool IsMissingExposedCorner(int mask, int corner) {
                bool isCornerSet = (mask & OverlayMasks.CornerBit[corner]) != 0;
                bool isExposed   = (mask & OverlayMasks.CornerEdges[corner]) == 0;
                bool hasSprite   = ((cornersAvailable >> corner) & 1) != 0;
                return isCornerSet && isExposed && requireCorners && !hasSprite;
            }
        }

        /// <summary>
        /// Determines renderability for Blob47 overlays.
        /// Non-empty slots w/o diagonal bits are controlled by <paramref name="requireSides"/>.
        /// Slots with diagonal bits are controlled by <paramref name="requireCorners"/>.
        /// Slot 0 is never required.
        /// </summary>
        /// <param name="slotsAvailable"> Bit i set indicates that the sprite for Blob47 slot i exists. </param>
        /// <param name="requireSides"> If true, slots without diagonal bits require an available sprite. </param>
        /// <param name="requireCorners"> If true, slots with diagonal bits require an available sprite. </param>
        /// <returns>
        /// A 256-entry lookup table indexed by raw neighbour mask. True indicates that the mask can be rendered.
        /// </returns>
        public static bool[] Blob47(ulong slotsAvailable, bool requireSides, bool requireCorners) {
            bool[] r = new bool[256];

            for (int i = 0; i < 256; i++) {
                int  slot           = OverlayMasks.BlobIndex(i);
                bool hasSprite      = ((slotsAvailable >> slot) & 1UL) != 0;
                r[i] = !RequiresSprite(slot) || hasSprite;
            }

            return r;
            bool RequiresSprite(int slot) {
                if (slot == 0) return false;
                return OverlayMasks.IsCornerSlot(slot) ? requireCorners : requireSides;
            }
        }

        /// <summary>
        /// Counts the number of non-empty neighbour masks that cannot be rendered.
        /// Mask 0 is excluded because an empty mask is always renderable.
        /// </summary>
        /// <param name="renderable">
        /// The 256-entry renderability lookup table to inspect.
        /// </param>
        /// <returns>The number of forbidden masks, from 0 to 255.</returns>
        public static int CountForbidden(bool[] renderable) {
            int forbidden = 0;
            for (int mask = 1; mask < 256; mask++) {
                if (renderable[mask]) continue;
                forbidden++;
            }

            return forbidden;
        }
    }
}