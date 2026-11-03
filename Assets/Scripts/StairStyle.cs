// StairStyle.cs
// Authored stair art + placement constraints, referenced from a CliffStyle (so stairs are only
// generated over cliffs whose raised terrain uses that style). Stairs are purely visual here.
//
// Edges: stairs go on the SOUTH face of a plateau, or on its WEST / EAST side. Never on the north edge.
//
// South stair  : replaces a stretch of the cliff face with stair pieces, assembled exactly like a face
//                (horizontal role left/middle/right/narrow x vertical role single/top/mid/bottom).
// Side stair   : the plateau's side steps down in blocks. For a drop of N levels there are N-1 treads
//                over the neighbouring lower cells; tread j sits at elevation (upper - j) and is drawn as
//                a top tile plus a front face reaching the ground. Rendering only; logical cells stay put.
using System.Collections.Generic;
using UnityEngine;

/// <summary>Wall-facing end pieces of a south feature for particular wall styles (so the neutral core art can be shared).</summary>
[System.Serializable]
public class FeatureAdapter
{
    [Tooltip("Wall styles these end pieces fit.")]
    public List<CliffStyle> forStyles = new List<CliffStyle>();
    public FaceRow left = new FaceRow();
    public FaceRow right = new FaceRow();
}

[CreateAssetMenu(menuName = "Terrain/Stair Style", fileName = "StairStyle")]
public class StairStyle : ScriptableObject
{
    [Header("Where (north is never offered)")]
    public bool allowSouth = true;
    public bool allowWest = true, allowEast = true;

    [Header("Cliff interaction")]
    [Tooltip("On: the stair replaces the cliff beneath it. The face columns are not drawn there, the plateau rim and side outline are cut across the opening, and the neighbouring faces get end caps (use for stair art that is a complete opening). Off: the cliff is drawn as normal and the stair is drawn on top of it (use for transparent-edged stair overlays that rely on the cliff art underneath).")]
    public bool replacesCliff = true;

    [Header("Feature requirements (waterfalls and similar; leave empty for plain stairs)")]
    [Tooltip("Terrains allowed on the top cells. Empty = any (subject to allowedTerrains).")]
    public List<TerrainDef> requiredUpperTerrains = new List<TerrainDef>();
    [Tooltip("Terrains required at the foot (the landing cells). Empty = any.")]
    public List<TerrainDef> requiredLowerTerrains = new List<TerrainDef>();
    [Min(0), Tooltip("Cells behind the top edge that must also be a required upper terrain on the same level (e.g. a pond behind a waterfall).")]
    public int sourceDepth = 0;

    [Header("Seams (where two wall styles meet)")]
    [Tooltip("Off: the feature and the columns beside it must all be one wall style. On: it may straddle a seam if it is wide enough.")]
    public bool allowSeamStraddle = false;
    [Min(2), Tooltip("Width needed to straddle a seam. A width-1 feature never sits in a seam zone.")]
    public int minWidthInSeamZone = 2;
    [Tooltip("Optional wall-style specific end pieces. If any are listed, a straddling feature needs an adapter for each wall style it touches; otherwise left/right below are used.")]
    public List<FeatureAdapter> adapters = new List<FeatureAdapter>();

    [Header("Constraints")]
    [Tooltip("Stair width in cells (columns for south stairs, rows for side stairs). Min / max, picked per stair.")]
    public Vector2Int width = new Vector2Int(2, 2);
    [Tooltip("Allowed elevation drop. Side stairs need at least 2 (they need a tread).")]
    public Vector2Int drop = new Vector2Int(1, 4);
    [Min(0), Tooltip("Cells of plain cliff kept on each side of the stair along the edge.")]
    public int edgeClearance = 1;
    [Min(1), Tooltip("Flat lower-surface cells required past the stair's foot (in the direction of descent).")]
    public int landingDepth = 1;
    [Tooltip("Terrains allowed on every cell the stair touches (top, steps, landing). Empty = any.")]
    public List<TerrainDef> allowedTerrains = new List<TerrainDef>();
    [Range(0f, 1f), Tooltip("Chance that a suitable cliff run / side run gets stairs at all.")]
    public float chancePerRun = 0.5f;
    [Min(1)] public int maxPerRun = 1;
    [Min(0), Tooltip("Minimum gap in cells between two stairs along an edge.")]
    public int spacing = 2;
    [Min(0), Tooltip("Cap on stairs per map for this style. 0 = unlimited.")]
    public int maxTotal = 0;
    [Min(1)] public int attempts = 12;

    [Header("South stair art (same roles as CliffStyle faces)")]
    public FaceRow left = new FaceRow();
    public FaceRow middle = new FaceRow();
    public FaceRow right = new FaceRow();
    [Tooltip("Used when the stair is one column wide.")]
    public FaceRow narrow = new FaceRow();
    [Tooltip("Optional decals on the upper cells at the stair top / lower cells at its foot.")]
    public Sprite[] upperLanding, lowerLanding;

    [Header("Side stair art (authored for WEST; east mirrors it unless east art is given)")]
    [Tooltip("Top surface of a tread (one cell).")]
    public Sprite[] treadTop;
    [Tooltip("Front face under a tread; stacks from the tread down to the ground (single/top/mid/bottom).")]
    public FaceRow treadFront = new FaceRow();
    public Sprite[] treadTopEast;
    public FaceRow treadFrontEast = new FaceRow();
    public bool mirrorEastFromWest = true;
}
