// TerrainSet.cs
// The data handed to the generator: terrains + structures. Compile() flattens them into plain
// arrays (CompiledTerrain) so the generator never touches ScriptableObjects. You can fill a
// CompiledTerrain from JSON or code instead.
using System;
using System.Collections.Generic;
using UnityEngine;

public class CompiledCliff
{
    public int id;                  // index in CompiledTerrain.allCliffs; also the "wall style id" used by runs
    public string name;
    public FaceRow middle, leftEnd, rightEnd, narrow, innerLeft, innerRight;
    public Sprite[][] rimByMask;    // [raw 8-bit "lower neighbour" mask] -> rim sprites
    public Sprite sideLeft, sideRight;
    public string family = "";
    public int dominance, minSpan = -1;
    public HashSet<CompiledCliff> compatible = new HashSet<CompiledCliff>();
    public CompiledStair[] features = new CompiledStair[0]; // stairs / waterfalls this wall can host

    /// <summary>May the two styles sit next to each other along one wall?</summary>
    public bool IsCompatible(CompiledCliff o)
    {
        return o == this || (family.Length > 0 && family == o.family) || compatible.Contains(o) || o.compatible.Contains(this);
    }
    public bool Offers(CompiledStair f) { foreach (var x in features) if (x == f) return true; return false; }
}

public class CompiledAdapter
{
    public CompiledCliff[] styles;
    public FaceRow left, right;
}

/// <summary>Stairs and other wall features (waterfalls...). One class, because they are placed and drawn the same way.</summary>
public class CompiledStair
{
    public int id; public string name;
    public bool allowSouth, allowWest, allowEast, mirrorEast, replacesCliff;
    public Vector2Int width, drop;
    public int edgeClearance, landingDepth, maxPerRun, spacing, maxTotal, attempts, sourceDepth, minWidthInSeamZone;
    public float chancePerRun;
    public ulong terrainMask, requiredUpperMask, requiredLowerMask; // 0 required mask = any
    public bool allowSeamStraddle;
    public FaceRow left, middle, right, narrow, treadFront, treadFrontEast;
    public Sprite[] upperLanding, lowerLanding, treadTop, treadTopEast;
    public CompiledAdapter[] adapters = new CompiledAdapter[0];
    public bool hasSouthArt, hasWestArt, hasEastArt; // a feature is only placed if its art exists
}

public class CompiledFoot
{
    public CompiledCliff[] aboveStyles;     // null = any wall style
    public ulong aboveTerrains;             // 0 = any raised terrain
    public CompiledCliff faceLayer;
    public FootMode mode;
    public Sprite[][] groundByMask;         // [mask of HIGHER neighbours] -> sprites on the foot cell; null = none
    public int priority;
}

public class CompiledStructure
{
    public StructureDef def;
    public ulong groundMask;       // terrains allowed under the footprint
}

/// <summary>Plain runtime data, all indexed by terrain index (position in TerrainSet.terrains).</summary>
public class CompiledTerrain
{
    public int count;
    public TerrainDef[] defs;
    public Sprite[][] baseVariants;
    public float[] weight, biomeCenter;
    public ulong[] allowed;      // same level: bit n => n may touch t
    public ulong[] belowCliff;   // t is the top edge of a cliff: bit n => n may sit on the lower side
    public ulong[] aboveCliff;   // inverse: t is on the lower side: bit n => n may be the top edge
    public int[] minLevel, maxLevel;
    public bool[] allowOnRim;
    public int[] overlayRank;
    public bool[,] overlaysOnto; // [overlay, under]
    public Sprite[][][] overlayByMask;
    public PropEntry[][] props;
    public CompiledCliff[] cliff;           // per terrain: default style (supplies rim and side outline), null = none
    public int[,] pairStyleId;              // [raised terrain, lower terrain (count = virtual ground)] -> wall style id, -1 = none
    public CompiledFoot[][] foot;           // per terrain: rules for when it is at the foot of a cliff
    public CompiledCliff[] allCliffs;       // every distinct style; index = wall style id
    public CompiledStair[] features;        // every distinct stair / waterfall style; index = feature id
    public CompiledStructure[] structures;
}

[CreateAssetMenu(menuName = "Terrain/Terrain Set", fileName = "TerrainSet")]
public class TerrainSet : ScriptableObject
{
    public List<TerrainDef> terrains = new List<TerrainDef>();
    public List<StructureDef> structures = new List<StructureDef>();

    [ContextMenu("Validate")] void ValidateMenu() { Compile(true); }

    class RuleEntry { public CompiledCliff style; public ulong below; public int prio; public bool specific; }

    public CompiledTerrain Compile(bool logWarnings = true)
    {
        var defs = terrains.FindAll(t => t != null);
        int n = defs.Count;
        if (n == 0 || n > 64) throw new InvalidOperationException("TerrainSet needs 1..64 terrains.");
        var idx = new Dictionary<TerrainDef, int>();
        for (int i = 0; i < n; i++) idx[defs[i]] = i;
        void Warn(string msg) { if (logWarnings) Debug.LogWarning("[TerrainSet] " + msg, this); }
        ulong full = n == 64 ? ulong.MaxValue : (1UL << n) - 1;
        ulong MaskOf(List<TerrainDef> list, string owner)
        {
            ulong m = 0;
            foreach (var td in list)
            {
                if (td == null) continue;
                if (idx.TryGetValue(td, out int bi)) m |= 1UL << bi; else Warn(owner + " lists " + td.name + " which is not in the set.");
            }
            return m;
        }

        var c = new CompiledTerrain
        {
            count = n, defs = defs.ToArray(),
            baseVariants = new Sprite[n][], weight = new float[n], biomeCenter = new float[n],
            allowed = new ulong[n], belowCliff = new ulong[n], aboveCliff = new ulong[n],
            minLevel = new int[n], maxLevel = new int[n], allowOnRim = new bool[n],
            overlayRank = new int[n], overlaysOnto = new bool[n, n],
            overlayByMask = new Sprite[n][][], props = new PropEntry[n][], cliff = new CompiledCliff[n],
            pairStyleId = new int[n, n + 1], foot = new CompiledFoot[n][]
        };

        // Same-level adjacency: self always allowed, listed neighbours made symmetric.
        for (int a = 0; a < n; a++) c.allowed[a] |= 1UL << a;
        for (int a = 0; a < n; a++)
            foreach (var other in defs[a].canNeighbor)
            {
                if (other == null) continue;
                if (!idx.TryGetValue(other, out int b)) { Warn(defs[a].name + " lists " + other.name + " which is not in the set."); continue; }
                c.allowed[a] |= 1UL << b; c.allowed[b] |= 1UL << a;
            }

        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (p, q) => defs[p].priority.CompareTo(defs[q].priority));
        for (int r = 0; r < n; r++) c.overlayRank[order[r]] = r;

        // ---- Cliff styles and features. Objects register in their cache BEFORE their links are resolved,
        // so styles that reference each other (compatibleWith, adapters) cannot recurse forever.
        var cliffCache = new Dictionary<CliffStyle, CompiledCliff>(); var allCliffs = new List<CompiledCliff>();
        var stairCache = new Dictionary<StairStyle, CompiledStair>(); var allFeatures = new List<CompiledStair>();
        bool Any(Sprite[] arr) { return arr != null && arr.Length > 0; }
        bool AnyRow(FaceRow row) { return row != null && (Any(row.single) || Any(row.top) || Any(row.mid) || Any(row.bottom)); }

        CompiledCliff GetCliff(CliffStyle s)
        {
            if (s == null) return null;
            if (cliffCache.TryGetValue(s, out var cc)) return cc;
            cc = new CompiledCliff
            {
                id = allCliffs.Count, name = s.name, middle = s.middle, leftEnd = s.leftEnd, rightEnd = s.rightEnd, narrow = s.narrow,
                innerLeft = s.innerLeft, innerRight = s.innerRight, sideLeft = s.sideLeft, sideRight = s.sideRight,
                family = s.family ?? "", dominance = s.dominance, minSpan = s.minSpan, rimByMask = new Sprite[256][]
            };
            for (int m = 0; m < 256; m++) cc.rimByMask[m] = s.rim != null ? s.rim.Resolve(m) : new Sprite[0];
            cliffCache[s] = cc; allCliffs.Add(cc);
            foreach (var co in s.compatibleWith) if (co != null) cc.compatible.Add(GetCliff(co));
            var feats = new List<CompiledStair>();
            var f0 = BuildStair(s.stairs); if (f0 != null) feats.Add(f0);
            foreach (var ms in s.moreFeatures) { var f = BuildStair(ms); if (f != null && !feats.Contains(f)) feats.Add(f); }
            cc.features = feats.ToArray();
            return cc;
        }

        CompiledStair BuildStair(StairStyle ss)
        {
            if (ss == null) return null;
            if (stairCache.TryGetValue(ss, out var existing)) return existing;
            ulong mask = MaskOf(ss.allowedTerrains, ss.name); if (mask == 0) mask = full;
            var o = new CompiledStair
            {
                id = allFeatures.Count, name = ss.name,
                allowSouth = ss.allowSouth, allowWest = ss.allowWest, allowEast = ss.allowEast, mirrorEast = ss.mirrorEastFromWest, replacesCliff = ss.replacesCliff,
                width = new Vector2Int(Mathf.Max(1, ss.width.x), Mathf.Max(Mathf.Max(1, ss.width.x), ss.width.y)), drop = ss.drop,
                edgeClearance = ss.edgeClearance, landingDepth = Mathf.Max(1, ss.landingDepth), maxPerRun = ss.maxPerRun, spacing = ss.spacing,
                maxTotal = ss.maxTotal, attempts = ss.attempts, chancePerRun = ss.chancePerRun, terrainMask = mask,
                sourceDepth = ss.sourceDepth, allowSeamStraddle = ss.allowSeamStraddle, minWidthInSeamZone = Mathf.Max(2, ss.minWidthInSeamZone),
                left = ss.left, middle = ss.middle, right = ss.right, narrow = ss.narrow, treadFront = ss.treadFront, treadFrontEast = ss.treadFrontEast,
                upperLanding = ss.upperLanding, lowerLanding = ss.lowerLanding, treadTop = ss.treadTop, treadTopEast = ss.treadTopEast
            };
            stairCache[ss] = o; allFeatures.Add(o);
            o.requiredUpperMask = MaskOf(ss.requiredUpperTerrains, ss.name);
            o.requiredLowerMask = MaskOf(ss.requiredLowerTerrains, ss.name);
            var ads = new List<CompiledAdapter>();
            foreach (var ad in ss.adapters)
            {
                if (ad == null) continue;
                var styleList = new List<CompiledCliff>();
                foreach (var fs in ad.forStyles) { var g = GetCliff(fs); if (g != null) styleList.Add(g); }
                if (styleList.Count > 0) ads.Add(new CompiledAdapter { styles = styleList.ToArray(), left = ad.left, right = ad.right });
            }
            o.adapters = ads.ToArray();
            o.hasSouthArt = AnyRow(ss.left) || AnyRow(ss.middle) || AnyRow(ss.right) || AnyRow(ss.narrow);
            o.hasWestArt = Any(ss.treadTop) && AnyRow(ss.treadFront);
            o.hasEastArt = (Any(ss.treadTopEast) || (ss.mirrorEastFromWest && Any(ss.treadTop))) && (AnyRow(ss.treadFrontEast) || (ss.mirrorEastFromWest && AnyRow(ss.treadFront)));
            if (!o.hasSouthArt && !o.hasWestArt && !o.hasEastArt) Warn(ss.name + " has no art for any edge; it will never be placed.");
            return o;
        }

        // ---- Per-terrain data
        for (int t = 0; t < n; t++)
        {
            var d = defs[t];
            c.baseVariants[t] = d.baseVariants;
            c.weight[t] = Mathf.Max(0.0001f, d.weight);
            c.biomeCenter[t] = d.biomeCenter;
            c.minLevel[t] = d.minLevel; c.maxLevel[t] = d.maxLevel; c.allowOnRim[t] = d.allowOnRim;
            c.props[t] = d.props.ToArray();
            if (d.baseVariants == null || d.baseVariants.Length == 0) Warn(d.name + " has no base sprites.");

            // Cliff-edge adjacency: unrestricted unless the terrain lists what may sit below its cliffs.
            if (!d.restrictBelowCliff) c.belowCliff[t] = full;
            else
            {
                c.belowCliff[t] = MaskOf(d.allowedBelowCliff, d.name);
                if (c.belowCliff[t] == 0) Warn(d.name + " restricts below-cliff terrain to nothing; use allowOnRim=false instead.");
            }

            for (int u = 0; u < n; u++)
                c.overlaysOnto[t, u] = d.overlay.mode != OverlayMode.None && d.priority > defs[u].priority && !d.noOverlayOnto.Contains(defs[u]);
            c.overlayByMask[t] = new Sprite[256][];
            for (int m = 0; m < 256; m++) c.overlayByMask[t][m] = d.overlay.mode == OverlayMode.None ? new Sprite[0] : d.overlay.Resolve(m);
        }
        for (int t = 0; t < n; t++)
            for (int u = 0; u < n; u++)
                if ((c.belowCliff[u] >> t & 1) != 0) c.aboveCliff[t] |= 1UL << u;

        // ---- Wall style resolution table: [raised terrain, terrain below] -> style id.
        // The default cliffStyle is an unspecific rule of the lowest priority. The most specific matching rule wins
        // (a rule naming the terrain below beats one that does not), then priority.
        var gaps = new List<string>();
        for (int u = 0; u < n; u++)
        {
            var d = defs[u];
            var entries = new List<RuleEntry>();
            if (d.cliffStyle != null) entries.Add(new RuleEntry { style = GetCliff(d.cliffStyle), below = 0, prio = int.MinValue, specific = false });
            foreach (var rule in d.cliffRules)
            {
                if (rule == null || rule.style == null) continue;
                ulong bm = MaskOf(rule.whenBelow, d.name);
                if (rule.whenBelow.Count > 0 && bm == 0) continue; // names only terrains that are not in the set
                entries.Add(new RuleEntry { style = GetCliff(rule.style), below = bm, prio = rule.priority, specific = bm != 0 });
            }
            for (int l = 0; l <= n; l++) // l == n is the virtual ground below row 0: only unspecific rules can match
            {
                RuleEntry best = null;
                foreach (var e in entries)
                {
                    bool match = e.below == 0 || (l < n && (e.below >> l & 1) != 0);
                    if (!match) continue;
                    if (best == null || (e.specific && !best.specific) || (e.specific == best.specific && e.prio > best.prio)) best = e;
                }
                c.pairStyleId[u, l] = best == null ? -1 : best.style.id;
                if (best == null && d.allowOnRim && entries.Count > 0 && l < n && (c.belowCliff[u] >> l & 1) != 0) gaps.Add(d.name + " over " + defs[l].name);
            }
            // Rim and side outlines are per cell: they use the terrain's default style, or failing that its first unspecific rule.
            if (d.cliffStyle != null) c.cliff[u] = GetCliff(d.cliffStyle);
            else foreach (var e in entries) if (!e.specific) { c.cliff[u] = e.style; break; }
            if (entries.Count == 0 && d.allowOnRim) Warn(d.name + " may sit on cliff edges but has no cliffStyle or cliffRules (cliffs under it will have no art).");
        }
        if (gaps.Count > 0)
            Warn("No wall art for: " + string.Join(", ", gaps.GetRange(0, Mathf.Min(gaps.Count, 8))) + (gaps.Count > 8 ? ", ..." : "")
                + ". Such columns borrow a neighbouring style along the wall when one exists; otherwise they get no art.");

        // ---- Foot rules (when a terrain is at the base of a cliff)
        for (int t = 0; t < n; t++)
        {
            var list = new List<CompiledFoot>();
            foreach (var fr in defs[t].footRules)
            {
                if (fr == null) continue;
                CompiledCliff[] above = null;
                if (fr.whenAboveStyle.Count > 0)
                {
                    var al = new List<CompiledCliff>();
                    foreach (var s in fr.whenAboveStyle) { var g = GetCliff(s); if (g != null) al.Add(g); }
                    above = al.ToArray();
                }
                var cf = new CompiledFoot
                {
                    aboveStyles = above, aboveTerrains = MaskOf(fr.whenAboveTerrain, defs[t].name), faceLayer = GetCliff(fr.faceLayer),
                    mode = fr.mode, priority = fr.priority
                };
                if (fr.groundOverlay != null && fr.groundOverlay.mode != OverlayMode.None)
                {
                    cf.groundByMask = new Sprite[256][];
                    for (int m = 0; m < 256; m++) cf.groundByMask[m] = fr.groundOverlay.Resolve(m);
                }
                list.Add(cf);
            }
            c.foot[t] = list.ToArray();
        }

        // ---- Structures: which terrains may sit under their footprint.
        var cs = new List<CompiledStructure>();
        foreach (var st in structures)
        {
            if (st == null) continue;
            ulong g = MaskOf(st.groundTerrains, st.name);
            if (g == 0) g = full;
            if (st.sprite == null) Warn(st.name + " has no sprite.");
            if (st.margin == 0) Warn(st.name + " has margin 0, so it can end up on a plateau edge.");
            cs.Add(new CompiledStructure { def = st, groundMask = g });
        }
        c.structures = cs.ToArray();
        c.allCliffs = allCliffs.ToArray();
        c.features = allFeatures.ToArray();
        return c;
    }
}
