// TerrainDef.cs
// One asset per terrain type (Create > Terrain > Terrain Def): art plus every constraint that
// applies to that terrain. A TerrainSet collects them.
using System;
using System.Collections.Generic;
using UnityEngine;

public enum OverlayMode
{
    None,     // never drawn as a sprite
    Edges16,  // 15 edge sprites (N/E/S/W combos) + 4 lone-corner sprites
    Blob47    // one Blob47Set asset; each sprite already combines edges AND corners
}

[Serializable]
public class PropEntry
{
    [Tooltip("Tall-object sprites (trees, rocks). Pivot should be bottom-centre.")]
    public Sprite[] variants;
    [Range(0f, 1f), Tooltip("Chance per cell. Entries are rolled in list order, first success wins.")]
    public float density = 0.05f;
    public Vector2 offset;
}

/// <summary>
/// Bit layout of the 8-bit neighbour mask. A bit is set when the neighbour in that direction
/// matches the rule being drawn (overlay terrain present / neighbour lower), i.e. "paint towards that side".
///       NW(128) N(1)  NE(2)
///        W(64)   .    E(4)
///       SW(32)  S(16) SE(8)
/// </summary>
public static class OverlayMasks
{
    public const int N = 1, NE = 2, E = 4, SE = 8, S = 16, SW = 32, W = 64, NW = 128;
    public static readonly int[] CornerBit = { NE, SE, SW, NW };
    public static readonly int[] CornerEdges = { N | E, E | S, S | W, W | N };
    static readonly string[] Names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
    static readonly Sprite[] NoSprites = new Sprite[0];

    public static int EdgeIndex(int m) { return (m & N) | ((m >> 1) & 2) | ((m >> 2) & 4) | ((m >> 3) & 8); } // N1 E2 S4 W8

    // A diagonal only matters when both bounding edges are clear. Gives exactly 47 distinct masks.
    public static int Reduce(int m)
    {
        int r = m & (N | E | S | W);
        for (int c = 0; c < 4; c++)
            if ((m & CornerBit[c]) != 0 && (m & CornerEdges[c]) == 0) r |= CornerBit[c];
        return r;
    }

    static int[] blobIndex; static List<int> canonical;
    static void Build()
    {
        if (blobIndex != null) return;
        var seen = new bool[256];
        for (int m = 0; m < 256; m++) seen[Reduce(m)] = true;
        blobIndex = new int[256]; canonical = new List<int>();
        for (int m = 0; m < 256; m++) if (seen[m]) { blobIndex[m] = canonical.Count; canonical.Add(m); }
    }
    public static int BlobIndex(int rawMask) { Build(); return blobIndex[Reduce(rawMask)]; }

    public static Sprite[] Resolve(OverlayMode mode, Sprite[] edges, Sprite[] corners, Sprite[] blob, int m)
    {
        switch (mode)
        {
            case OverlayMode.Blob47:
                {
                    int i = BlobIndex(m);
                    return i != 0 && blob != null && i < blob.Length && blob[i] != null ? new[] { blob[i] } : NoSprites;
                }
            case OverlayMode.Edges16:
                {
                    var list = new List<Sprite>(3);
                    int e = EdgeIndex(m);
                    if (e != 0 && edges != null && e < edges.Length && edges[e] != null) list.Add(edges[e]);
                    for (int c = 0; c < 4; c++)
                        if ((m & CornerBit[c]) != 0 && (m & CornerEdges[c]) == 0 && corners != null && c < corners.Length && corners[c] != null)
                            list.Add(corners[c]);
                    return list.ToArray();
                }
            default: return NoSprites;
        }
    }

    public static string DescribeBlob47()
    {
        Build();
        var sb = new System.Text.StringBuilder("Blob47 slots (index: sides the sprite paints towards)\n");
        for (int i = 0; i < canonical.Count; i++)
        {
            var parts = new List<string>();
            for (int b = 0; b < 8; b++) if ((canonical[i] >> b & 1) != 0) parts.Add(Names[b]);
            sb.AppendLine(i + ": " + (parts.Count == 0 ? "(none, leave empty)" : string.Join("+", parts)));
        }
        return sb.ToString();
    }
}

/// <summary>A reusable "edge sprite set": used for terrain overlays and for cliff rims.</summary>
[Serializable]
public class OverlayArt
{
    public OverlayMode mode = OverlayMode.None;
    [Tooltip("Edges16: index = N1 | E2 | S4 | W8. Index 0 unused.")]
    public Sprite[] edges = new Sprite[16];
    [Tooltip("Edges16 lone corners in order NE, SE, SW, NW.")]
    public Sprite[] corners = new Sprite[4];
    [Tooltip("Blob47: a single Blob47Set asset.")]
    public Blob47Set blob;

    public void Fit()
    {
        if (edges == null || edges.Length != 16) Array.Resize(ref edges, 16);
        if (corners == null || corners.Length != 4) Array.Resize(ref corners, 4);
    }
    public Sprite[] Resolve(int m) { return OverlayMasks.Resolve(mode, edges, corners, blob != null ? blob.sprites : null, m); }
}

/// <summary>Picks the wall style when this terrain is the raised edge. Most specific match wins, then priority.</summary>
[Serializable]
public class CliffRule
{
    public CliffStyle style;
    [Tooltip("Terrains that must be at the foot of the wall. Empty = any (an unspecific rule).")]
    public List<TerrainDef> whenBelow = new List<TerrainDef>();
    public int priority;
}

public enum FootMode { Overlay, Replace }

/// <summary>
/// Used when THIS terrain is at the foot of a cliff. Adds art where the wall meets it. All parts are optional:
/// a missing sprite simply draws nothing, the wall's own art stays.
/// </summary>
[Serializable]
public class FootRule
{
    [Tooltip("Wall styles above this terrain that the rule applies to. Empty = any.")]
    public List<CliffStyle> whenAboveStyle = new List<CliffStyle>();
    [Tooltip("Raised terrains the rule applies to. Empty = any.")]
    public List<TerrainDef> whenAboveTerrain = new List<TerrainDef>();
    [Tooltip("Art for the bottom of the wall (its middle row: single when the cliff is 1 high, otherwise bottom).")]
    public CliffStyle faceLayer;
    [Tooltip("Overlay: drawn over the wall's own bottom piece. Replace: used instead of it.")]
    public FootMode mode = FootMode.Overlay;
    [Tooltip("Drawn on this terrain's own cell at the foot. Mask bit = that neighbour is HIGHER.")]
    public OverlayArt groundOverlay = new OverlayArt();
    public int priority;
}

[CreateAssetMenu(menuName = "Terrain/Terrain Def", fileName = "Terrain_")]
public class TerrainDef : ScriptableObject
{
    [Header("Base tile (opaque, fills the cell)")]
    public Sprite[] baseVariants;
    public Color debugColor = Color.magenta;

    [Header("WFC: same-level adjacency")]
    public float weight = 1f;
    [Range(0f, 1f), Tooltip("Where on the low-frequency biome noise this terrain prefers to appear.")]
    public float biomeCenter = 0.5f;
    [Tooltip("Terrains allowed to touch this one on the same level. Symmetric. A terrain may always touch itself.")]
    public List<TerrainDef> canNeighbor = new List<TerrainDef>();

    [Header("WFC: height constraints (heights are solved first, so these are exact)")]
    public int minLevel = 0;
    public int maxLevel = 99;
    [Tooltip("Off = never placed on a plateau edge: a cell with a lower neighbour (see the generator's rimIncludesCorners), including the bottom row of a raised area.")]
    public bool allowOnRim = true;
    [Tooltip("On = only the terrains below may sit on the LOWER side of a cliff whose top edge is this terrain. Off = anything may. Example: water lists water (a waterfall landing in a pond).")]
    public bool restrictBelowCliff;
    public List<TerrainDef> allowedBelowCliff = new List<TerrainDef>();

    [Header("Cliff look when this terrain is the raised edge")]
    [Tooltip("Default wall style (also supplies the plateau rim). Empty = no cliff art unless a rule below matches.")]
    public CliffStyle cliffStyle;
    [Tooltip("More specific wall styles, e.g. a different style when the cell below is water.")]
    public List<CliffRule> cliffRules = new List<CliffRule>();
    [Tooltip("Art added where a cliff meets THIS terrain at its foot (waterlines, foam, ...).")]
    public List<FootRule> footRules = new List<FootRule>();

    [Header("Overlay (drawn over lower-priority neighbours)")]
    public int priority;
    public OverlayArt overlay = new OverlayArt();
    public List<TerrainDef> noOverlayOnto = new List<TerrainDef>();

    [Header("Props (tall objects, y-sorted)")]
    public List<PropEntry> props = new List<PropEntry>();

    void OnValidate()
    {
        if (overlay == null) overlay = new OverlayArt(); overlay.Fit();
        foreach (var f in footRules) { if (f.groundOverlay == null) f.groundOverlay = new OverlayArt(); f.groundOverlay.Fit(); }
    }
}
