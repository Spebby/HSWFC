using System;
using System.Collections.Generic;
using UnityEngine;
using WFC.Core;


namespace WFC.Terrain {
    /// <summary>
    /// Sprite lookup for one 8-bit neighbour mask (see <see cref="OverlayMasks"/>).
    /// This is only a lookup, missing sprites are omitted. Compatible with Edge16 or Blob47.
    /// </summary>
    public static class OverlayResolver {
        static readonly Sprite[] NO_SPRITES = Array.Empty<Sprite>();

        public static Sprite[] Resolve(OverlayMode mode, Sprite[] edges, Sprite[] corners, Sprite[] blob, int m) {
            switch (mode) {
                case OverlayMode.Blob47: {
                    int  i         = OverlayMasks.BlobIndex(m);
                    bool hasSprite = blob != null && i > 0 && i < blob.Length && blob[i];
                    return hasSprite ? new[] { blob[i] } : NO_SPRITES;
                }
                case OverlayMode.Edges16: {
                    List<Sprite> list = new(3);
                    int          e    = OverlayMasks.EdgeIndex(m);

                    bool hasEdge = edges != null && e > 0 && e < edges.Length && edges[e];
                    if (hasEdge) list.Add(edges[e]);

                    for (int c = 0; c < 4; c++) {
                        bool hasCorner = corners != null && c < corners.Length && corners[c];
                        bool isExposed = (m & OverlayMasks.CornerBit[c]) != 0 &&
                                         (m & OverlayMasks.CornerEdges[c]) == 0;

                        if (hasCorner && isExposed) list.Add(corners[c]);
                    }

                    return list.ToArray();
                }
                case OverlayMode.None:
                default: return NO_SPRITES;
            }
        }
    }
}