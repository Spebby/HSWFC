// CliffStyle.cs
// Art for the cliff formed under a raised terrain. Reusable: grass, dirt and sand can share
// a rock style while water points at a waterfall style (see TerrainDef.cliffStyle).
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Vertical stack roles for one horizontal role. Single = 1 level tall; Top/Mid/Bottom stack for 2+.</summary>
[Serializable]
public class FaceRow { public Sprite[] single, top, mid, bottom; }

[CreateAssetMenu(menuName = "Terrain/Cliff Style", fileName = "CliffStyle")]
public class CliffStyle : ScriptableObject
{
    [Header("Face walls (horizontal role x vertical role)")]
    [Tooltip("Face with another face of the same cliff on both sides. Also the fallback for every empty slot below.")]
    public FaceRow middle = new FaceRow();
    [Tooltip("No face to its left: the wall starts here.")]
    public FaceRow leftEnd = new FaceRow();
    [Tooltip("No face to its right: the wall ends here.")]
    public FaceRow rightEnd = new FaceRow();
    [Tooltip("One column wide.")]
    public FaceRow narrow = new FaceRow();
    [Tooltip("Optional. Left end of a run where the plateau continues south beside it (concave / inner corner). Empty = leftEnd.")]
    public FaceRow innerLeft = new FaceRow();
    [Tooltip("Optional. Right-hand counterpart of innerLeft. Empty = rightEnd.")]
    public FaceRow innerRight = new FaceRow();

    [Header("Plateau rim (drawn on the raised cell; mask bit = that neighbour is LOWER)")]
    public OverlayArt rim = new OverlayArt();

    [Header("Wall material (how this style behaves along a long wall)")]
    [Tooltip("Styles in the same non-empty family may sit next to each other along one wall (a seam). Empty = only compatible with itself and the styles listed below.")]
    public string family;
    public List<CliffStyle> compatibleWith = new List<CliffStyle>();
    [Tooltip("Breaks ties, and decides which neighbour absorbs a span that is too short.")]
    public int dominance;
    [Tooltip("Minimum span width in columns for this style. -1 = use the generator's minSpan. 1 = never smoothed away.")]
    public int minSpan = -1;

    [Header("Features offered on this wall (stairs, waterfalls, ...)")]
    public StairStyle stairs;
    public List<StairStyle> moreFeatures = new List<StairStyle>();

    [Header("Optional thin outline on the raised cell's west/east side")]
    public Sprite sideLeft, sideRight;

    void OnValidate() { if (rim == null) rim = new OverlayArt(); rim.Fit(); }
}
