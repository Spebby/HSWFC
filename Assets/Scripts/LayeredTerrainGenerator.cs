// LayeredTerrainGenerator.cs
// Data-driven layered terrain with a SNES-style, surface-based renderer.
//
// LOGICAL layer (authoritative, never altered by visuals)
//   types[] terrain per cell, hm[] elevation per cell, structure reservations.
//   TerrainTopology (TerrainTopology.cs) derives surfaces, cliff runs and side edges from them.
//   Stages: 1 heights -> 2 structures -> 3 terrain WFC -> 4 topology -> 4b stair placement -> 5 render.
//
// RENDER layer (only reads the logical layer)
//   Projection : cell (x,y) at elevation e is drawn at world (x*cell, y*cell + e*elevationStepWorld).
//                Surfaces, overlays, rims, props and structures all use this one rule.
//   Cliff faces: built from CliffRuns. A drop of N levels = N pieces, each elevationStepWorld tall,
//                hanging from the upper cell's projected south edge, so they exactly fill the gap down
//                to the lower surface. They overlap lower cells visually only; those cells stay intact.
//   Occlusion  : pure sorting. Each grid row is a sorting band (north = behind, south = in front) with
//                slots inside it (ground, overlays, rim, side, face, prop, structure, foreground).
//                A raised surface or face in row y therefore hides what is in rows north of it.
//   Future use : GridToWorld(x,y,elevation) + SortingOrderFor(gridY) place and sort a moving object.
//
// SETUP: empty GameObject + this component, assign a TerrainSet, press Play.
//        Edit any field while playing to regenerate; R = new seed.
using System;
using System.Collections.Generic;
using UnityEngine;

public class LayeredTerrainGenerator : MonoBehaviour
{
    public enum View { Final, Logic, Heights, Surfaces, Boundaries, Projection, RenderOrder, Transitions, CliffSpans }
    public enum TilePivot { Center, BottomLeft } // legacy: sprites are now anchored from their own rect + pivot

    [Header("Data")]
    public TerrainSet terrainSet;
    public float cellSize = 1f;
    [Tooltip("Legacy, no longer used. Any sprite pivot works: sprites are anchored by their rect and pivot.")]
    public TilePivot tilePivot = TilePivot.Center;

    [Header("Elevation projection")]
    [Min(0f), Tooltip("Screen-space rise per elevation level, in world units. Set it to the height of one cliff-face piece. 0 = flat map.")]
    public float elevationStepWorld = 1f;
    [Tooltip("Stretch each face piece to (cellSize x elevationStepWorld). Off = use the sprite at its authored size.")]
    public bool fitFacesToStep = true;

    [Header("Generation")]
    public int seed = 7;
    public int width = 36, height = 26;
    [Range(0f, 1f)] public float regionalBias = 0.7f;

    [Header("Heights / cliffs")]
    [Range(0.05f, 0.9f)] public float heightAmount = 0.45f;
    [Range(1, 8)] public int maxLevel = 4;
    [Tooltip("What counts as a plateau edge for TerrainDef.allowOnRim. On: any of the 8 neighbours is lower (exactly the cells that draw a rim sprite), plus the bottom row of a raised area. Off: only the 4 straight neighbours.")]
    public bool rimIncludesCorners = true;
    public bool clampStepSize = false;
    [Range(1, 4)] public int maxStep = 1;
    [Range(1, 4), Tooltip("Every height difference between neighbouring cells is a multiple of this, so no cliff is shorter than it (2 = all cliffs at least 2 high). Heights become 0, minStep, 2*minStep... up to maxLevel.")]
    public int minStep = 1;

    [Header("Cliff walls")]
    [Min(1), Tooltip("Along one wall, a stretch of the same wall style is at least this many columns wide. Shorter stretches are absorbed by a neighbour. 1 = off. Individual styles can override it.")]
    public int minSpan = 3;
    [Tooltip("A wall column whose terrain has no art takes the nearest real style along the same wall instead of being left without art.")]
    public bool borrowMissingWallArt = true;

    [Header("Display")]
    public View view = View.Final;
    public bool transitions = true, cliffs = true, rims = true, props = true, structures = true;
    [Tooltip("Generate and draw stairs where a cliff style has a StairStyle.")]
    public bool stairs = true;
    [Tooltip("Draws a marker at probeCell, sorted like a character would be, to check occlusion behind raised terrain.")]
    public bool showProbe = false;
    public Vector2Int probeCell = new Vector2Int(10, 10);
    [Tooltip("Off: overlays only blend between cells at the same height.")]
    public bool blendAcrossHeights = false;
    public bool autoFrameCamera = true;

    static readonly Vector2Int[] Dir8 = { // OverlayMasks bit order: N, NE, E, SE, S, SW, W, NW
        new Vector2Int(0,1), new Vector2Int(1,1), new Vector2Int(1,0), new Vector2Int(1,-1),
        new Vector2Int(0,-1), new Vector2Int(-1,-1), new Vector2Int(-1,0), new Vector2Int(-1,1) };
    static readonly Vector2Int[] D4 = { new Vector2Int(0,1), new Vector2Int(1,0), new Vector2Int(0,-1), new Vector2Int(-1,0) };

    struct Placed { public int si, x, y; }           // si = index into ct.structures, (x,y) = footprint bottom-left

    CompiledTerrain ct;
    CompiledCliff[] styles;     // = ct.allCliffs; a wall style id indexes this
    readonly HashSet<string> warned = new HashSet<string>();
    int[] types, hm;        // terrain index / height level per cell, index = y*width+x
    int[] reserved;         // -1, or index into 'placed' for footprint cells
    bool[] locked;          // footprint + margin: heights must not change
    List<Placed> placed;
    Transform root;
    Sprite flat;
    bool dirty;

    void Start() { Generate(); }
    void OnValidate() { dirty = true; }
    void Update()
    {
        if (dirty && Application.isPlaying) { dirty = false; Generate(); }
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.R)) { seed = UnityEngine.Random.Range(0, 1000000); Generate(); }
#endif
    }

    // ------------------------------------------------------------------ helpers
    static float Hash(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)(a * 374761393 + b * 668265263 + c * 1013904223);
            h = (h ^ (h >> 13)) * 1274126177u; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
    static T Pick<T>(T[] arr, int x, int y, int salt) where T : class
    {
        if (arr == null || arr.Length == 0) return null;
        return arr[(int)(Hash(x, y, salt) * arr.Length) % arr.Length];
    }
    static Sprite[] First(params Sprite[][] sets) { foreach (var s in sets) if (s != null && s.Length > 0) return s; return null; }
    // Sprites for one vertical role (0 single, 1 top, 2 mid, 3 bottom) of a face row, with fallbacks inside the row.
    static Sprite[] Slot(FaceRow r, int v)
    {
        if (r == null) return null;
        switch (v)
        {
            case 0: return First(r.single, r.top, r.mid, r.bottom);
            case 1: return First(r.top, r.mid, r.single);
            case 3: return First(r.bottom, r.mid, r.single);
            default: return First(r.mid, r.top, r.single);
        }
    }

    bool In(int x, int y) { return x >= 0 && y >= 0 && x < width && y < height; }
    int Ty(int x, int y) { return In(x, y) ? types[y * width + x] : -1; }
    int Hg(int x, int y) { return In(x, y) ? hm[y * width + x] : -1; }

    static float[] Noise(int w, int h, float sc, System.Random R)
    {
        int gw = Mathf.CeilToInt(w / sc) + 2, gh = Mathf.CeilToInt(h / sc) + 2;
        var g = new float[gw * gh]; for (int i = 0; i < g.Length; i++) g[i] = (float)R.NextDouble();
        var o = new float[w * h]; float lo = 1e9f, hi = -1e9f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = x / sc, fy = y / sc; int ix = (int)fx, iy = (int)fy;
                float tx = fx - ix, ty = fy - iy; tx *= tx * (3 - 2 * tx); ty *= ty * (3 - 2 * ty);
                float a = g[iy * gw + ix], b = g[iy * gw + ix + 1], c = g[(iy + 1) * gw + ix], d = g[(iy + 1) * gw + ix + 1];
                float v = a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
                o[y * w + x] = v; lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v);
            }
        for (int i = 0; i < o.Length; i++) o[i] = (o[i] - lo) / Mathf.Max(1e-6f, hi - lo);
        return o;
    }

    // ================================================================== STAGE 1: HEIGHTS
    int[] RawHeights(System.Random R)
    {
        int w = width, h = height; float[] nz = Noise(w, h, 9, R); var H = new int[w * h];
        // minStep: heights are multiples of 'stride', so any non-zero difference is at least that tall.
        int stride = Mathf.Max(1, minStep), levels = maxLevel / stride;
        if (levels < 1) WarnOnce("minstep", "maxLevel is smaller than minStep; the map will be flat.");
        for (int i = 0; i < H.Length; i++)
            H[i] = Mathf.RoundToInt(Mathf.Clamp01((nz[i] - (1 - heightAmount)) / heightAmount) * levels) * stride;
        for (int pass = 0; pass < 3; pass++) // minimum plateau size
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var cnt = new int[maxLevel + 1]; int same = 0;
                    foreach (var d in D4)
                    {
                        int nx = x + d.x, ny = y + d.y; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int v = H[ny * w + nx]; cnt[v]++; if (v == H[y * w + x]) same++;
                    }
                    if (same < 2) { int bi = 0; for (int k = 1; k < cnt.Length; k++) if (cnt[k] > cnt[bi]) bi = k; H[y * w + x] = bi; }
                }
        return H;
    }

    // Optional step limit. Locked cells (structure footprints) are never lowered; if they are the
    // violating side, the neighbour is raised to meet them instead.
    void ClampSteps()
    {
        int w = width, h = height;
        int stride = Mathf.Max(1, minStep), ms = Mathf.Max(stride, maxStep / stride * stride); // keep heights on the minStep lattice
        for (int pass = 0; pass < 60; pass++)
        {
            bool ch = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    foreach (var d in D4)
                    {
                        int nx = x + d.x, ny = y + d.y; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int i = y * w + x, j = ny * w + nx;
                        if (hm[i] <= hm[j] + ms) continue;
                        if (!locked[i]) { hm[i] = hm[j] + ms; ch = true; }
                        else if (!locked[j]) { hm[j] = hm[i] - ms; ch = true; }
                    }
            if (!ch) break;
        }
    }

    // ================================================================== STAGE 2: STRUCTURES
    // Pick spots for each StructureDef. RequireFlat only accepts spots whose footprint + margin is
    // already one level; Flatten forces it. Either way the footprint + margin is height-locked and
    // the footprint cells are reserved (WFC restricts their ground, props skip them).
    void PlaceStructures(System.Random R)
    {
        int w = width, h = height;
        placed = new List<Placed>(); reserved = new int[w * h]; locked = new bool[w * h];
        for (int i = 0; i < reserved.Length; i++) reserved[i] = -1;
        for (int si = 0; si < ct.structures.Length; si++)
        {
            var def = ct.structures[si].def; int fw = def.footprint.x, fh = def.footprint.y, m = def.margin;
            if (w - fw - 2 * m < 0 || h - fh - 2 * m < 0) continue;
            int count = R.Next(def.count.x, Mathf.Max(def.count.x, def.count.y) + 1);
            for (int k = 0; k < count; k++)
                for (int a = 0; a < def.attempts; a++)
                {
                    int x0 = R.Next(m, w - fw - m + 1), y0 = R.Next(m, h - fh - m + 1);
                    int h0 = hm[(y0 + fh / 2) * w + x0 + fw / 2];
                    bool ok = true;
                    for (int y = y0 - m; y < y0 + fh + m && ok; y++)
                        for (int x = x0 - m; x < x0 + fw + m; x++)
                            if (locked[y * w + x] || (def.heightMode == StructureDef.HeightMode.RequireFlat && hm[y * w + x] != h0)) { ok = false; break; }
                    if (!ok) continue;
                    for (int y = y0 - m; y < y0 + fh + m; y++)
                        for (int x = x0 - m; x < x0 + fw + m; x++) { hm[y * w + x] = h0; locked[y * w + x] = true; }
                    for (int y = y0; y < y0 + fh; y++)
                        for (int x = x0; x < x0 + fw; x++) reserved[y * w + x] = placed.Count;
                    placed.Add(new Placed { si = si, x = x0, y = y0 });
                    break;
                }
        }
    }

    // A "rim" cell has a lower neighbour (so a cliff or rim sprite is drawn there). Below row 0 counts as
    // virtual ground at elevation 0, matching the topology and the renderer.
    bool IsRimCell(int x, int y, int h0)
    {
        for (int k = 0; k < (rimIncludesCorners ? 8 : 4); k++)
        {
            Vector2Int d = rimIncludesCorners ? Dir8[k] : Dir8[k * 2]; // Dir8 edges are the even indices
            int nx = x + d.x, ny = y + d.y; if (nx < 0 || nx >= width || ny >= height) continue;
            if ((ny < 0 ? 0 : hm[ny * width + nx]) < h0) return true;
        }
        return false;
    }

    /// <summary>0 = every constraint held. 1 = cross-cliff rules were dropped. 2 = adjacency was dropped. 3 = height-based cell rules were dropped too.</summary>
    public int WfcRelaxationLevel { get; private set; }

    // Reports any cell that ended up breaking a per-cell rule, so a violation is never silent.
    void ReportViolations()
    {
        int w = width, rim = 0, lvl = 0, gnd = 0; string firstRim = null;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, t = types[i], h0 = hm[i];
                if (h0 < ct.minLevel[t] || h0 > ct.maxLevel[t]) lvl++;
                if (!ct.allowOnRim[t] && IsRimCell(x, y, h0)) { rim++; if (firstRim == null) firstRim = ct.defs[t].name; }
                if (reserved[i] >= 0 && (ct.structures[placed[reserved[i]].si].groundMask >> t & 1) == 0) gnd++;
            }
        if (rim + lvl + gnd > 0)
            Debug.LogWarning("[Generator] Terrain constraints violated (relaxation level " + WfcRelaxationLevel + "): " + rim + " cells on a rim that forbids them"
                + (firstRim != null ? " (e.g. " + firstRim + ")" : "") + ", " + lvl + " outside their level range, " + gnd + " on the wrong structure ground.");
    }

    // ================================================================== STAGE 3: TERRAIN WFC
    // Domain per cell = ulong bitmask over terrain indices (max 64).
    // * Starting domain: level range, allowOnRim, structure ground.
    // * Propagation across an edge depends on the (known) height relation of the two cells:
    //     same level      -> allowed[]      (normal adjacency)
    //     i above j       -> belowCliff[]   (what may sit on the lower side of i's cliff)
    //     i below j       -> aboveCliff[]   (inverse of the above)
    int RunWfc(System.Random R, out int[] result)
    {
        int w = width, h = height, N = w * h, n = ct.count;
        ulong full = n == 64 ? ulong.MaxValue : (1UL << n) - 1;
        float[] noise = Noise(w, h, 7, R);
        var W = new float[N * n];
        for (int i = 0; i < N; i++)
            for (int t = 0; t < n; t++)
                W[i * n + t] = Mathf.Max(1e-4f, ct.weight[t] * Mathf.Exp(-20f * regionalBias * Mathf.Pow(noise[i] - ct.biomeCenter[t], 2)));

        var init = new ulong[N]; int relaxed = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, h0 = hm[i]; bool rim = IsRimCell(x, y, h0);
                ulong m = 0;
                for (int t = 0; t < n; t++)
                    if (h0 >= ct.minLevel[t] && h0 <= ct.maxLevel[t] && (ct.allowOnRim[t] || !rim)) m |= 1UL << t;
                if (reserved[i] >= 0) { ulong g = ct.structures[placed[reserved[i]].si].groundMask; m &= g; if (m == 0) m = g; }
                if (m == 0) { m = full; relaxed++; }
                init[i] = m;
            }
        if (relaxed > 0) Debug.LogWarning("[Generator] " + relaxed + " cells have no terrain satisfying their level / rim / structure rules; the rules are ignored there (check allowOnRim, minLevel and maxLevel across your terrains).");

        var dom = new ulong[N];
        var stack = new Stack<int>();
        // Relaxation levels. Per-cell rules (level range, allowOnRim, structure ground) are the LAST thing given up:
        //   0 everything   1 drop cross-cliff rules   2 drop adjacency, keep per-cell rules   3 drop everything
        int level = 0;

        ulong Allow(ulong dm, int hi, int hj)
        {
            if (level == 2) return ulong.MaxValue; // adjacency off
            ulong a = 0;
            if (level >= 1 || hi == hj) { for (int t = 0; t < n; t++) if ((dm >> t & 1) != 0) a |= ct.allowed[t]; }
            else if (hi > hj) { for (int t = 0; t < n; t++) if ((dm >> t & 1) != 0) a |= ct.belowCliff[t]; }
            else { for (int t = 0; t < n; t++) if ((dm >> t & 1) != 0) a |= ct.aboveCliff[t]; }
            return a;
        }

        bool Propagate()
        {
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                foreach (var d in D4)
                {
                    int nx = x + d.x, ny = y + d.y; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx; ulong nd = dom[j] & Allow(dom[i], hm[i], hm[j]);
                    if (nd == dom[j]) continue;
                    if (nd == 0) return false;
                    dom[j] = nd; stack.Push(j);
                }
            }
            return true;
        }

        bool Reset()
        {
            stack.Clear();
            for (int i = 0; i < N; i++) dom[i] = level < 3 ? init[i] : full;
            if (level >= 2) return true; // nothing to propagate
            for (int i = 0; i < N; i++) stack.Push(i); // initial propagation of the starting domains
            return Propagate();
        }

        bool Solve()
        {
            for (; ; )
            {
                int best = -1; float bestE = 1e9f;
                for (int i = 0; i < N; i++)
                {
                    ulong m = dom[i]; if ((m & (m - 1)) == 0) continue;
                    float s = 0, e = 0;
                    for (int t = 0; t < n; t++) if ((m >> t & 1) != 0) { float v = W[i * n + t]; s += v; e += v * Mathf.Log(v); }
                    float ent = Mathf.Log(s) - e / s + (float)R.NextDouble() * 1e-3f;
                    if (ent < bestE) { bestE = ent; best = i; }
                }
                if (best < 0) return true;
                ulong mask = dom[best]; float sum = 0;
                for (int t = 0; t < n; t++) if ((mask >> t & 1) != 0) sum += W[best * n + t];
                float r = (float)R.NextDouble() * sum; int pick = 0;
                for (int t = 0; t < n; t++) if ((mask >> t & 1) != 0) { pick = t; r -= W[best * n + t]; if (r <= 0) break; }
                dom[best] = 1UL << pick; stack.Push(best);
                if (!Propagate()) return false;
            }
        }

        string[] why = {
            "",
            "Cross-cliff rules (allowedBelowCliff) conflict with the other constraints; ignoring them. Level range, allowOnRim and structure ground still apply.",
            "Adjacency rules conflict with the per-cell constraints; ignoring adjacency. Level range, allowOnRim and structure ground still apply.",
            "The per-cell constraints (level range, allowOnRim, structure ground) cannot be satisfied together; ignoring them too."
        };
        int tries = 0, atLevel = 0;
        for (; tries < 100; tries++)
        {
            bool ok = Reset();
            if (ok && Solve()) break;
            // A failed initial propagation is deterministic, so move on at once; random failures get 20 tries per level.
            if ((!ok || ++atLevel >= 20) && level < 3) { level++; atLevel = 0; Debug.LogWarning("[Generator] " + why[level]); }
        }
        WfcRelaxationLevel = level;
        result = new int[N];
        for (int i = 0; i < N; i++) { int t = 0; while (t < n - 1 && (dom[i] >> t & 1) == 0) t++; result[i] = t; }
        return tries + 1;
    }

    // ================================================================== STAGE 4: TOPOLOGY
    // Wall style resolution. Runs are geometric, so the style is decided per column and then smoothed along the wall:
    //   1. each column asks its terrains (raised terrain over the terrain below) for a style via the rule table
    //   2. columns with no art borrow the nearest real style on the wall (optional)
    //   3. spans narrower than minSpan, and seams between incompatible styles, are absorbed by a neighbour
    // Dominance only decides ties and which side absorbs: a span that is wide enough is never overridden.
    class WallSpan { public int style, start, len; }

    int MinSpanOf(int styleId)
    {
        if (styleId < 0) return 1;
        int m = styles[styleId].minSpan; return Mathf.Max(1, m < 0 ? minSpan : m);
    }
    bool StylesCompatible(int a, int b) { return a < 0 || b < 0 || styles[a].IsCompatible(styles[b]); }
    bool WeakerSpan(WallSpan a, WallSpan b) // is a the weaker of the two? shorter, then less dominant, then the later one
    {
        if (a.len != b.len) return a.len < b.len;
        int da = a.style < 0 ? 0 : styles[a.style].dominance, db = b.style < 0 ? 0 : styles[b.style].dominance;
        if (da != db) return da < db;
        return a.start > b.start;
    }

    int[] SmoothSpans(int[] cand)
    {
        int n = cand.Length; var res = (int[])cand.Clone();
        if (borrowMissingWallArt)
        {
            int last = -1;
            for (int i = 0; i < n; i++) { if (res[i] >= 0) last = res[i]; else if (last >= 0) res[i] = last; }
            last = -1;
            for (int i = n - 1; i >= 0; i--) { if (res[i] >= 0) last = res[i]; else if (last >= 0) res[i] = last; }
        }
        var sp = new List<WallSpan>();
        for (int i = 0; i < n; i++)
        {
            if (sp.Count > 0 && sp[sp.Count - 1].style == res[i]) sp[sp.Count - 1].len++;
            else sp.Add(new WallSpan { style = res[i], start = i, len = 1 });
        }
        for (int guard = 0; guard < 4 * n + 4 && sp.Count > 1; guard++)
        {
            int victim = -1;
            for (int k = 0; k < sp.Count; k++)
            {
                bool bad = sp[k].len < MinSpanOf(sp[k].style);
                if (!bad && k > 0 && !StylesCompatible(sp[k - 1].style, sp[k].style) && WeakerSpan(sp[k], sp[k - 1])) bad = true;          // incompatible seam: the weaker side gives way
                if (!bad && k < sp.Count - 1 && !StylesCompatible(sp[k].style, sp[k + 1].style) && WeakerSpan(sp[k], sp[k + 1])) bad = true;
                if (bad && (victim < 0 || WeakerSpan(sp[k], sp[victim]))) victim = k;
            }
            if (victim < 0) break;
            int nb = victim == 0 ? 1 : victim == sp.Count - 1 ? victim - 1 : (WeakerSpan(sp[victim - 1], sp[victim + 1]) ? victim + 1 : victim - 1);
            if (nb < victim) sp[nb].len += sp[victim].len; else { sp[nb].start = sp[victim].start; sp[nb].len += sp[victim].len; }
            sp.RemoveAt(victim);
            for (int k = sp.Count - 1; k > 0; k--) if (sp[k].style == sp[k - 1].style) { sp[k - 1].len += sp[k].len; sp.RemoveAt(k); }
        }
        foreach (var span in sp) for (int i = span.start; i < span.start + span.len; i++) res[i] = span.style;
        return res;
    }

    void ResolveWallStyles()
    {
        styles = ct.allCliffs;
        foreach (var run in Topology.runs)
        {
            var cand = new int[run.Length];
            for (int i = 0; i < cand.Length; i++)
                cand[i] = ct.pairStyleId[run.upperTerrains[i], run.lowerTerrains[i] < 0 ? ct.count : run.lowerTerrains[i]];
            run.colStyle = SmoothSpans(cand);
        }
        foreach (var sr in Topology.sideRuns)
        {
            var cand = new int[sr.Length];
            for (int i = 0; i < cand.Length; i++) cand[i] = ct.pairStyleId[sr.upperTerrains[i], sr.lowerTerrains[i]];
            sr.rowStyle = SmoothSpans(cand);
        }
    }

    /// <summary>Logical surfaces, cliff runs and side edges. Read-only; reusable by future gameplay code.</summary>
    public TerrainTopology Topology { get; private set; }
    public int TerrainAt(int x, int y) { return Ty(x, y); }
    public int ElevationAt(int x, int y) { return Hg(x, y); }

    // ================================================================== STAGE 5: RENDER
    // ---- Projection ---------------------------------------------------------------------------
    // The one rule that places everything: elevation raises a cell on screen, grid coordinates are untouched.
    public Vector3 GridToWorld(float gx, float gy, int elevation)
    {
        return new Vector3(gx * cellSize, gy * cellSize + elevation * elevationStepWorld, 0);
    }
    Vector2 CellOrigin(int x, int y, int elev) { return GridToWorld(x, y, elev); } // bottom-left of the projected cell

    // ---- Sorting ------------------------------------------------------------------------------
    // One band per grid row (north rows get lower orders = behind). Slots order things inside a band.
    // Why rows alone are enough: a cell in a more northern row always projects higher on screen than
    // one in a more southern row of the same column, so anything that overlaps is correctly in front.
    public const int Band = 100;
    public const int SlotGround = 0, SlotOverlay = 1 /* +rank, up to 64 */, SlotRim = 70, SlotSide = 72,
                     SlotFootGround = 66, SlotLanding = 71, SlotFace = 80, SlotProp = 90, SlotStructure = 92, SlotForeground = 94, SlotProbe = 96;
    public int RowBase(int gridY) { return (height - 1 - Mathf.Clamp(gridY, 0, height - 1)) * Band; }
    /// <summary>Sorting order for an object standing in grid row gridY (e.g. a future character: SlotProp).</summary>
    public int SortingOrderFor(int gridY, int slot = SlotProp) { return RowBase(gridY) + slot; }

    // ---- Sprite placement ---------------------------------------------------------------------
    // Sprites are anchored from their own rect and pivot, so any pivot convention works.
    // stretch = scale the sprite to boxSize (used for face pieces); otherwise authored size is kept.
    SpriteRenderer PutBox(Sprite s, Vector2 boxMin, Vector2 boxSize, int order, Color col, bool stretch, bool flipX = false)
    {
        Vector2 size = s.rect.size / s.pixelsPerUnit, piv = s.pivot / s.pixelsPerUnit;
        if (flipX) piv.x = size.x - piv.x; // a mirrored sprite pivots on the opposite side of its rect
        Vector2 scale = stretch && size.x > 0 && size.y > 0 ? new Vector2(boxSize.x / size.x, boxSize.y / size.y) : Vector2.one;
        var go = new GameObject(s.name);
        go.transform.SetParent(root, false);
        go.transform.localPosition = (Vector3)(boxMin + Vector2.Scale(piv, scale));
        go.transform.localScale = new Vector3(scale.x, scale.y, 1);
        var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = s; sr.sortingOrder = order; sr.color = col; sr.flipX = flipX;
        return sr;
    }
    // Tall object standing on a ground point: bottom-centre of the sprite's rect sits on 'foot'.
    SpriteRenderer PutAtFoot(Sprite s, Vector2 foot, int order, Color col)
    {
        Vector2 size = s.rect.size / s.pixelsPerUnit;
        return PutBox(s, new Vector2(foot.x - size.x / 2f, foot.y), Vector2.zero, order, col, false);
    }
    Sprite FlatSprite()
    {
        if (flat != null) return flat;
        var tex = new Texture2D(1, 1) { filterMode = FilterMode.Point }; tex.SetPixel(0, 0, Color.white); tex.Apply();
        return flat = Sprite.Create(tex, new Rect(0, 0, 1, 1), Vector2.zero, 1); // 1x1 world unit, pivot bottom-left
    }
    void PutFlat(Vector2 min, Vector2 size, Color c, int order)
    {
        var go = new GameObject("flat"); go.transform.SetParent(root, false);
        go.transform.localPosition = min; go.transform.localScale = new Vector3(size.x, size.y, 1);
        var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = FlatSprite(); sr.color = c; sr.sortingOrder = order;
    }
    Color Shade(int hv) { float sh = 0.7f + 0.3f * hv / Mathf.Max(1, maxLevel); return new Color(sh, sh, sh); }
    void WarnOnce(string key, string msg) { if (warned.Add(key)) Debug.LogWarning("[Generator] " + msg); }

    // ---- Entry point --------------------------------------------------------------------------
    public void Generate()
    {
        if (terrainSet == null) { Debug.LogWarning("Assign a TerrainSet."); return; }
        ct = terrainSet.Compile(true);
        if (root != null) { if (Application.isPlaying) Destroy(root.gameObject); else DestroyImmediate(root.gameObject); }
        root = new GameObject("Map").transform; root.SetParent(transform, false);
        if (height * Band > 32000) WarnOnce("rows", "Map has too many rows for 16-bit sorting orders (max about 320).");

        // Logical stages: identical to before, and never touched by anything below.
        hm = RawHeights(new System.Random(seed ^ unchecked((int)0x9e3779b9)));
        PlaceStructures(new System.Random(seed ^ 0x5bd1e995));
        if (clampStepSize) ClampSteps();
        RunWfc(new System.Random(seed), out types);
        ReportViolations();
        Topology = TerrainTopology.Build(width, height, types, hm);
        ResolveWallStyles();
        PlaceTransitions(new System.Random(seed ^ 0x7a1b2c3d));
        BuildTransitionMasks();

        // Render stage.
        bool projected = view == View.Final || view == View.Projection || view == View.RenderOrder;
        switch (view)
        {
            case View.Final: RenderFinal(); break;
            case View.Projection: RenderProjectedDebug(false); break;
            case View.RenderOrder: RenderProjectedDebug(true); break;
            case View.Transitions: case View.CliffSpans: RenderLogicalDebug(); break;
            default: RenderLogicalDebug(); break;
        }
        if (showProbe && projected) DrawProbe();
        FrameCamera(projected);
    }

    // ---- Final render -------------------------------------------------------------------------
    void RenderFinal()
    {
        int w = width, h = height;

        // Surfaces: every cell at its projected position, in its row's ground slot.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int t = types[y * w + x], hv = hm[y * w + x];
                var s = Pick(ct.baseVariants[t], x, y, 1);
                if (s != null) PutBox(s, CellOrigin(x, y, hv), Vector2.zero, RowBase(y) + SlotGround, Shade(hv), false);
            }

        // Terrain overlays: raw 8-bit mask of neighbours that are the overlay terrain -> precomputed sprites.
        // Neighbours on the same level are projected side by side, so blends stay seamless.
        if (transitions)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int t = Ty(x, y), h0 = Hg(x, y); var tint = Shade(h0);
                    for (int o = 0; o < ct.count; o++)
                    {
                        if (!ct.overlaysOnto[o, t]) continue;
                        int mask = 0;
                        for (int k = 0; k < 8; k++)
                        {
                            int nx = x + Dir8[k].x, ny = y + Dir8[k].y;
                            if (Ty(nx, ny) == o && (blendAcrossHeights || Hg(nx, ny) == h0)) mask |= 1 << k;
                        }
                        if (mask == 0) continue;
                        foreach (var s in ct.overlayByMask[o][mask])
                            PutBox(s, CellOrigin(x, y, h0), Vector2.zero, RowBase(y) + SlotOverlay + ct.overlayRank[o], tint, false);
                    }
                }

        if (cliffs) { RenderCliffFaces(); RenderRimsAndSides(); }
        if (cliffs && stairs) RenderStairs();

        // Props: stand on their cell's projected ground; hidden ones stay (they are occluded by sorting, not removed).
        if (props)
        {
            var R = new System.Random(seed ^ 0x1234567);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x; if (reserved[i] >= 0) continue;
                    foreach (var p in ct.props[types[i]])
                    {
                        if (p.variants == null || p.variants.Length == 0 || R.NextDouble() >= p.density) continue;
                        Vector2 foot = (Vector2)GridToWorld(x + 0.5f, y, hm[i]) + p.offset;
                        PutAtFoot(Pick(p.variants, x, y, 3), foot, SortingOrderFor(y, SlotProp), Color.white);
                        break;
                    }
                }
        }

        // Structures: one ground anchor (centre of the front edge of the footprint) sorted by the front row.
        // The optional foreground piece sorts a few rows further forward so overhangs cover things in front too.
        if (structures)
            foreach (var p in placed)
            {
                var def = ct.structures[p.si].def;
                Vector2 foot = (Vector2)GridToWorld(p.x + def.footprint.x / 2f, p.y, hm[p.y * w + p.x]) + def.offset;
                if (def.sprite != null) PutAtFoot(def.sprite, foot, SortingOrderFor(p.y, SlotStructure), Color.white);
                if (def.foreground != null) PutAtFoot(def.foreground, foot, SortingOrderFor(p.y - def.foregroundRowOffset, SlotForeground), Color.white);
            }
    }

    // ---- Cliff faces: one pass over CliffRuns (no per-cell coverage bookkeeping) ---------------
    static bool HasAny(FaceRow r) { return r != null && (First(r.single, r.top, r.mid, r.bottom) != null); }

    FaceRow RoleFor(CompiledCliff st, CliffRun r, int x)
    {
        // A column beside a south stair is the end of a wall just like a real run end.
        bool stairL = x > r.x0 && southStairTop[r.upperRow * width + x - 1], stairR = x < r.x1 && southStairTop[r.upperRow * width + x + 1];
        bool leftOpen = stairL || (x == r.x0 && r.leftEnd != RunEnd.MapEdge), rightOpen = stairR || (x == r.x1 && r.rightEnd != RunEnd.MapEdge);
        if (leftOpen && rightOpen) return st.narrow;
        if (leftOpen) return !stairL && r.leftEnd == RunEnd.Concave && HasAny(st.innerLeft) ? st.innerLeft : st.leftEnd;
        if (rightOpen) return !stairR && r.rightEnd == RunEnd.Concave && HasAny(st.innerRight) ? st.innerRight : st.rightEnd;
        return st.middle; // interior of a run, or a run that continues off the map
    }

    // Best foot rule for (wall style above, raised terrain, terrain at the foot): most specific, then priority.
    CompiledFoot FindFoot(int styleId, int upperT, int lowerT)
    {
        if (lowerT < 0 || lowerT >= ct.count) return null;
        CompiledFoot best = null; int bestScore = -1;
        foreach (var f in ct.foot[lowerT])
        {
            if (f.aboveStyles != null && System.Array.IndexOf(f.aboveStyles, styles[styleId]) < 0) continue;
            if (f.aboveTerrains != 0 && (f.aboveTerrains >> upperT & 1) == 0) continue;
            int score = (f.aboveStyles != null ? 1 : 0) + (f.aboveTerrains != 0 ? 1 : 0);
            if (best == null || score > bestScore || (score == bestScore && f.priority > best.priority)) { best = f; bestScore = score; }
        }
        return best;
    }
    // The art of a foot layer for the bottom piece: single for a 1-high cliff, bottom otherwise. Optional, no fallback to other roles.
    static Sprite[] FootPiece(FaceRow r, bool single)
    {
        if (r == null) return null;
        return single ? First(r.single, r.bottom) : First(r.bottom);
    }

    void RenderCliffFaces()
    {
        foreach (var run in Topology.runs)
        {
            int dh = run.Drop; if (dh <= 0 || elevationStepWorld <= 0) continue;
            // The upper cell's projected south edge. Piece k occupies [yTop-(k+1)*step, yTop-k*step],
            // so the stack ends exactly on the lower surface's projected north edge.
            float yTop = run.upperRow * cellSize + run.upperElev * elevationStepWorld;
            int order = RowBase(run.upperRow) + SlotFace;
            for (int x = run.x0; x <= run.x1; x++)
            {
                int ci = x - run.x0, sid = run.colStyle[ci];
                if (sid < 0) { WarnOnce("nowall", "Some cliff columns have no wall art at all (no style for that terrain pair); they are left empty."); continue; }
                if (southStairTop[run.upperRow * width + x]) continue; // a stair replaces this stretch of face
                var st = styles[sid]; FaceRow row = RoleFor(st, run, x);
                CompiledFoot foot = FindFoot(sid, run.upperTerrains[ci], run.lowerTerrains[ci]);
                for (int k = 0; k < dh; k++)
                {
                    int v = VRole(dh, k); // single / top / mid / bottom
                    var box = new Vector2(x * cellSize, yTop - (k + 1) * elevationStepWorld);
                    Sprite layer = null; bool replace = false;
                    if (foot != null && foot.faceLayer != null && k == dh - 1)
                    {
                        layer = Pick(FootPiece(foot.faceLayer.middle, dh == 1), x, run.upperRow * 64 + k, 4);
                        replace = layer != null && foot.mode == FootMode.Replace;
                    }
                    if (!replace)
                    {
                        var s = Pick(First(Slot(row, v), Slot(st.middle, v)), x, run.upperRow * 64 + k, 2); // empty slot -> middle row
                        if (s == null) { WarnOnce("face" + sid, "Wall style '" + st.name + "' has no face sprite for some slot; those pieces are skipped."); }
                        else PutBox(s, box, new Vector2(cellSize, elevationStepWorld), order, Color.white, fitFacesToStep);
                    }
                    if (layer != null) PutBox(layer, box, new Vector2(cellSize, elevationStepWorld), order + 1, Color.white, fitFacesToStep);
                }

                // Optional art on the foot cell itself (foam, shadow...): mask bit = that neighbour is HIGHER than the foot cell.
                if (foot != null && foot.groundByMask != null && run.upperRow >= 1)
                {
                    int fy = run.upperRow - 1, mask = 0;
                    for (int k = 0; k < 8; k++) if (Hg(x + Dir8[k].x, fy + Dir8[k].y) > run.lowerElev) mask |= 1 << k;
                    if (mask != 0)
                        foreach (var gs in foot.groundByMask[mask])
                            PutBox(gs, CellOrigin(x, fy, run.lowerElev), Vector2.zero, RowBase(fy) + SlotFootGround, Shade(run.lowerElev), false);
                }
            }
        }
    }

    // Elevation of a neighbour for rim purposes: below row 0 is virtual ground at 0 (matches the topology).
    int ElevOrVirtual(int x, int y)
    {
        if (x < 0 || x >= width || y >= height) return -1;
        return y < 0 ? 0 : hm[y * width + x];
    }

    void RenderRimsAndSides()
    {
        int w = width, h = height;
        foreach (var e in Topology.sides) // thin outline on the raised cell's west/east side
        {
            var es = ct.cliff[e.terrain]; if (es == null) continue;
            if ((rimClear[e.y * w + e.x] & (e.east ? OverlayMasks.E : OverlayMasks.W)) != 0) continue; // stair opening
            var s = e.east ? es.sideRight : es.sideLeft; if (s == null) continue;
            PutBox(s, CellOrigin(e.x, e.y, e.upperElev), Vector2.zero, RowBase(e.y) + SlotSide, Color.white, false);
        }
        if (!rims) return;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, h0 = hm[i]; var st = ct.cliff[types[i]]; if (st == null) continue;
                int mask = 0;
                for (int k = 0; k < 8; k++)
                {
                    int nh = ElevOrVirtual(x + Dir8[k].x, y + Dir8[k].y);
                    if (nh >= 0 && nh < h0) mask |= 1 << k; // mask bit = that neighbour is LOWER
                }
                mask &= ~rimClear[i]; // no rim across a stair opening
                if (mask == 0) continue;
                foreach (var s in st.rimByMask[mask]) PutBox(s, CellOrigin(x, y, h0), Vector2.zero, RowBase(y) + SlotRim, Shade(h0), false);
            }
    }


    // ================================================================== TRANSITIONS (stairs)
    // Placement works on logical data only (topology runs + hm/types/reserved) and records Transition objects.
    // Rendering later replaces the matching stretch of cliff face; nothing in types[]/hm[] changes.
    public IReadOnlyList<Transition> Transitions { get { return Topology.transitions; } }
    bool[] southStairTop;   // cell is the top cell of a south stair (its face column is replaced)
    int[] rimClear;         // per cell: OverlayMasks bits to clear from the rim mask (stair openings)

    bool StairCellFree(CompiledStair s, int x, int y, int elev, bool[] claimed)
    {
        if (!In(x, y)) return false;
        int i = y * width + x;
        return hm[i] == elev && reserved[i] < 0 && !claimed[i] && (s.terrainMask >> types[i] & 1) != 0;
    }

    CompiledAdapter AdapterFor(CompiledStair f, int styleId)
    {
        if (styleId < 0) return null;
        foreach (var ad in f.adapters) if (System.Array.IndexOf(ad.styles, styles[styleId]) >= 0) return ad;
        return null;
    }

    // Can feature f sit on columns xs..xs+wd-1 of this run? Besides the stair rules (free flat cells, allowed terrain,
    // spacing) this enforces the seam rules: the columns it covers must all be wall styles that offer it, and if the
    // columns beside it differ in style (a seam zone) it needs allowSeamStraddle, a width of at least 2, and adapters
    // for both flanking styles when the feature defines adapters.
    bool SouthFeatureFits(CliffRun run, CompiledStair f, int xs, int wd, bool[] claimed)
    {
        int y = run.upperRow, w = width;
        for (int x = xs - f.spacing; x < xs + wd + f.spacing; x++) if (In(x, y) && claimed[y * w + x]) return false;
        for (int x = xs; x < xs + wd; x++) { int sid = run.colStyle[x - run.x0]; if (sid < 0 || !styles[sid].Offers(f)) return false; }
        int fl = Mathf.Max(run.x0, xs - 1), fr = Mathf.Min(run.x1, xs + wd), first = run.colStyle[fl - run.x0]; bool seam = false;
        for (int x = fl; x <= fr; x++) if (run.colStyle[x - run.x0] != first) seam = true;
        if (seam)
        {
            if (!f.allowSeamStraddle || wd < Mathf.Max(2, f.minWidthInSeamZone)) return false; // never a width-1 feature in a seam zone
            if (f.adapters.Length > 0 && (AdapterFor(f, run.colStyle[fl - run.x0]) == null || AdapterFor(f, run.colStyle[fr - run.x0]) == null)) return false;
        }
        for (int x = xs; x < xs + wd; x++)
        {
            int ci = x - run.x0;
            if (!StairCellFree(f, x, y, run.upperElev, claimed)) return false;
            if (f.requiredUpperMask != 0 && (f.requiredUpperMask >> run.upperTerrains[ci] & 1) == 0) return false;
            for (int d = 1; d <= f.landingDepth; d++)
            {
                if (!StairCellFree(f, x, y - d, run.lowerElev, claimed)) return false;
                if (f.requiredLowerMask != 0 && (f.requiredLowerMask >> types[(y - d) * w + x] & 1) == 0) return false;
            }
            for (int d = 1; d <= f.sourceDepth; d++) // e.g. the pond behind a waterfall
            {
                if (!In(x, y + d) || hm[(y + d) * w + x] != run.upperElev) return false;
                if (f.requiredUpperMask != 0 && (f.requiredUpperMask >> types[(y + d) * w + x] & 1) == 0) return false;
            }
        }
        return true;
    }

    void PlaceTransitions(System.Random R)
    {
        int w = width; var list = Topology.transitions; list.Clear();
        if (!stairs) return;
        var claimed = new bool[w * height]; var totals = new Dictionary<int, int>();
        bool Room(CompiledStair f) { totals.TryGetValue(f.id, out int n0); return f.maxTotal <= 0 || n0 < f.maxTotal; }
        void Count(CompiledStair f) { totals.TryGetValue(f.id, out int n0); totals[f.id] = n0 + 1; }

        // South features: along exposed south boundaries. The wall styles of the columns decide which features are offered.
        foreach (var run in Topology.runs)
        {
            if (run.upperRow < 1) continue; // runs over the virtual ground below row 0 never get features
            var offered = new List<CompiledStair>();
            foreach (int sid in run.colStyle) if (sid >= 0) foreach (var f0 in styles[sid].features) if (!offered.Contains(f0)) offered.Add(f0);
            foreach (var s in offered)
            {
                if (!s.allowSouth || !s.hasSouthArt || run.Drop < Mathf.Max(1, s.drop.x) || run.Drop > s.drop.y) continue;
                if (R.NextDouble() >= s.chancePerRun) continue;
                int y = run.upperRow, made = 0;
                for (int a = 0; a < s.attempts && made < s.maxPerRun && Room(s); a++)
                {
                    int wd = R.Next(s.width.x, s.width.y + 1);
                    int lo = run.x0 + s.edgeClearance, hi = run.x1 - s.edgeClearance - wd + 1; if (hi < lo) continue;
                    int xs = R.Next(lo, hi + 1);
                    if (!SouthFeatureFits(run, s, xs, wd, claimed)) continue;
                    var up = new List<Vector2Int>(); var land = new List<Vector2Int>();
                    for (int x = xs; x < xs + wd; x++)
                    {
                        up.Add(new Vector2Int(x, y)); claimed[y * w + x] = true;
                        for (int d = 1; d <= s.landingDepth; d++) { land.Add(new Vector2Int(x, y - d)); claimed[(y - d) * w + x] = true; }
                    }
                    list.Add(new Transition
                    {
                        kind = TransitionKind.Stairs, side = EdgeSide.South, featureId = s.id, styleId = run.colStyle[xs - run.x0],
                        leftStyleId = run.colStyle[Mathf.Max(run.x0, xs - 1) - run.x0], rightStyleId = run.colStyle[Mathf.Min(run.x1, xs + wd) - run.x0],
                        direction = new Vector2Int(0, -1), width = wd,
                        upperElev = run.upperElev, lowerElev = run.lowerElev, upperSurface = run.upperSurface, lowerSurface = run.lowerSurface,
                        upperCells = up.ToArray(), descentCells = new Vector2Int[0], landingCells = land.ToArray(),
                        upperEnd = up[up.Count / 2], lowerEnd = new Vector2Int(up[up.Count / 2].x, y - 1)
                    });
                    made++; Count(s);
                }
            }
        }

        // Side stairs: along west / east plateau sides. They need a drop of at least 2 (one tread) and flat lower
        // cells for every step plus the landing, because the steps are drawn over those lower cells. Side features
        // never straddle a seam: every row they touch, and the rows beside them, share one wall style.
        foreach (var sr in Topology.sideRuns)
        {
            var offered = new List<CompiledStair>();
            foreach (int sid in sr.rowStyle) if (sid >= 0) foreach (var f0 in styles[sid].features) if (!offered.Contains(f0)) offered.Add(f0);
            foreach (var s in offered)
            {
                if (sr.east ? !(s.allowEast && s.hasEastArt) : !(s.allowWest && s.hasWestArt)) continue;
                if (sr.Drop < Mathf.Max(2, s.drop.x) || sr.Drop > s.drop.y) continue;
                if (R.NextDouble() >= s.chancePerRun) continue;
                int dir = sr.east ? 1 : -1, dh = sr.Drop, made = 0;
                for (int a = 0; a < s.attempts && made < s.maxPerRun && Room(s); a++)
                {
                    int wd = R.Next(s.width.x, s.width.y + 1);
                    int lo = sr.y0 + s.edgeClearance, hi = sr.y1 - s.edgeClearance - wd + 1; if (hi < lo) continue;
                    int ys = R.Next(lo, hi + 1); bool ok = true;
                    int fl = Mathf.Max(sr.y0, ys - 1), fr = Mathf.Min(sr.y1, ys + wd), first = sr.rowStyle[fl - sr.y0];
                    for (int y = fl; y <= fr && ok; y++) if (sr.rowStyle[y - sr.y0] != first) ok = false;
                    if (first < 0 || !styles[first].Offers(s)) ok = false;
                    for (int y = ys - s.spacing; y < ys + wd + s.spacing && ok; y++) if (In(sr.x, y) && claimed[y * w + sr.x]) ok = false;
                    for (int y = ys; y < ys + wd && ok; y++)
                    {
                        if (!StairCellFree(s, sr.x, y, sr.upperElev, claimed)) ok = false;
                        if (s.requiredUpperMask != 0 && (s.requiredUpperMask >> sr.upperTerrains[y - sr.y0] & 1) == 0) ok = false;
                        for (int j = 1; j <= dh + s.landingDepth - 1 && ok; j++)
                        {
                            if (!StairCellFree(s, sr.x + dir * j, y, sr.lowerElev, claimed)) ok = false;
                            else if (j >= dh && s.requiredLowerMask != 0 && (s.requiredLowerMask >> types[y * w + sr.x + dir * j] & 1) == 0) ok = false;
                        }
                    }
                    if (!ok) continue;
                    var up = new List<Vector2Int>(); var steps = new List<Vector2Int>(); var land = new List<Vector2Int>();
                    for (int y = ys; y < ys + wd; y++)
                    {
                        up.Add(new Vector2Int(sr.x, y)); claimed[y * w + sr.x] = true;
                        for (int j = 1; j <= dh + s.landingDepth - 1; j++)
                        {
                            var c = new Vector2Int(sr.x + dir * j, y); claimed[c.y * w + c.x] = true;
                            if (j < dh) steps.Add(c); else land.Add(c);
                        }
                    }
                    int mid = up.Count / 2;
                    list.Add(new Transition
                    {
                        kind = TransitionKind.Stairs, side = sr.east ? EdgeSide.East : EdgeSide.West, featureId = s.id, styleId = first, leftStyleId = first, rightStyleId = first,
                        direction = new Vector2Int(dir, 0), width = wd,
                        upperElev = sr.upperElev, lowerElev = sr.lowerElev, upperSurface = sr.upperSurface, lowerSurface = sr.lowerSurface,
                        upperCells = up.ToArray(), descentCells = steps.ToArray(), landingCells = land.ToArray(),
                        upperEnd = up[mid], lowerEnd = new Vector2Int(sr.x + dir * dh, up[mid].y)
                    });
                    made++; Count(s);
                }
            }
        }
    }

    // Per-cell lookups derived from the transitions, so the face/rim passes need no searching.
    void BuildTransitionMasks()
    {
        int w = width; southStairTop = new bool[w * height]; rimClear = new int[w * height];
        foreach (var t in Topology.transitions)
        {
            // Stair styles that do not replace the cliff leave the face, rim and side outline untouched;
            // the stair is just drawn on top of them.
            if (!ct.features[t.featureId].replacesCliff) continue;
            foreach (var c in t.upperCells)
            {
                int i = c.y * w + c.x;
                if (t.side == EdgeSide.South) { southStairTop[i] = true; rimClear[i] |= OverlayMasks.S | OverlayMasks.SE | OverlayMasks.SW; }
                else if (t.side == EdgeSide.West) rimClear[i] |= OverlayMasks.W | OverlayMasks.NW | OverlayMasks.SW;
                else rimClear[i] |= OverlayMasks.E | OverlayMasks.NE | OverlayMasks.SE;
            }
        }
    }

    // Vertical role (single/top/mid/bottom) for piece k of an n-piece stack.
    static int VRole(int n, int k) { return n == 1 ? 0 : k == 0 ? 1 : k == n - 1 ? 3 : 2; }

    // End piece of a south feature: the adapter for the wall style beside it if one exists, otherwise the feature's own left/right.
    FaceRow AdapterRow(CompiledStair sa, int styleId, bool left)
    {
        var ad = AdapterFor(sa, styleId);
        if (ad != null) { var r = left ? ad.left : ad.right; if (HasAny(r)) return r; }
        return left ? sa.left : sa.right;
    }

    void RenderStairs()
    {
        if (elevationStepWorld <= 0) return;
        foreach (var t in Topology.transitions)
        {
            var sa = ct.features[t.featureId]; int dh = t.Drop;
            if (t.side == EdgeSide.South)
            {
                // Same geometry as a cliff face: pieces hang from the upper cells' projected south edge down to the lower surface.
                int y = t.upperCells[0].y; float yTop = y * cellSize + t.upperElev * elevationStepWorld;
                for (int ci = 0; ci < t.upperCells.Length; ci++)
                {
                    int x = t.upperCells[ci].x;
                    FaceRow row = t.width == 1 ? sa.narrow : ci == 0 ? AdapterRow(sa, t.leftStyleId, true) : ci == t.width - 1 ? AdapterRow(sa, t.rightStyleId, false) : sa.middle;
                    for (int k = 0; k < dh; k++)
                    {
                        int v = VRole(dh, k);
                        var s = Pick(First(Slot(row, v), Slot(sa.middle, v), Slot(sa.narrow, v)), x, y * 64 + k, 5);
                        if (s == null) { WarnOnce("stair" + t.styleId, "A stair style is missing a south stair sprite; those pieces are skipped."); continue; }
                        PutBox(s, new Vector2(x * cellSize, yTop - (k + 1) * elevationStepWorld), new Vector2(cellSize, elevationStepWorld), RowBase(y) + SlotFace + 1, Color.white, fitFacesToStep); // +1: above the cliff face when it is not replaced
                    }
                    var up = Pick(sa.upperLanding, x, y, 6);
                    if (up != null) PutBox(up, CellOrigin(x, y, t.upperElev), Vector2.zero, RowBase(y) + SlotLanding, Shade(t.upperElev), false);
                }
                foreach (var c in t.landingCells)
                {
                    var lo = Pick(sa.lowerLanding, c.x, c.y, 7);
                    if (lo != null) PutBox(lo, CellOrigin(c.x, c.y, t.lowerElev), Vector2.zero, RowBase(c.y) + SlotLanding, Shade(t.lowerElev), false);
                }
                continue;
            }

            // Side stairs: tread j (1..dh-1) is a one-cell block at elevation upper-j over the lower cell, drawn as a top tile
            // plus (on the southernmost row only) a front face down to the ground. Art is west-facing; east mirrors it
            // unless dedicated east art exists.
            bool east = t.side == EdgeSide.East;
            Sprite[] tops = east && sa.treadTopEast != null && sa.treadTopEast.Length > 0 ? sa.treadTopEast : sa.treadTop;
            FaceRow front = east && HasAny(sa.treadFrontEast) ? sa.treadFrontEast : sa.treadFront;
            bool flipTop = east && sa.mirrorEast && tops == sa.treadTop;      // west art reused on the east side -> mirror it
            bool flipFront = east && sa.mirrorEast && front == sa.treadFront;
            int bottomRow = int.MaxValue; foreach (var c in t.upperCells) bottomRow = Mathf.Min(bottomRow, c.y);
            foreach (var c in t.descentCells)
            {
                int j = Mathf.Abs(c.x - t.upperCells[0].x), e = t.upperElev - j; // visual elevation of this step
                int order = RowBase(c.y) + SlotFace + 1;
                var top = Pick(tops, c.x, c.y, 8);
                if (top != null) PutBox(top, CellOrigin(c.x, c.y, e), Vector2.zero, order, Color.white, false, flipTop);
                if (c.y != bottomRow) continue; // rows inside the stair rest on the next tread, no face needed
                int n = e - t.lowerElev; float yTop = c.y * cellSize + e * elevationStepWorld;
                for (int k = 0; k < n; k++)
                {
                    var s = Pick(Slot(front, VRole(n, k)), c.x, k, 9);
                    if (s == null) { WarnOnce("tread" + t.styleId, "A stair style is missing a tread front sprite; those pieces are skipped."); continue; }
                    PutBox(s, new Vector2(c.x * cellSize, yTop - (k + 1) * elevationStepWorld), new Vector2(cellSize, elevationStepWorld), order, Color.white, fitFacesToStep, flipFront);
                }
            }
            foreach (var c in t.landingCells)
            {
                var lo = Pick(sa.lowerLanding, c.x, c.y, 7);
                if (lo != null) PutBox(lo, CellOrigin(c.x, c.y, t.lowerElev), Vector2.zero, RowBase(c.y) + SlotLanding, Shade(t.lowerElev), false);
            }
            foreach (var c in t.upperCells)
            {
                var up = Pick(sa.upperLanding, c.x, c.y, 6);
                if (up != null) PutBox(up, CellOrigin(c.x, c.y, t.upperElev), Vector2.zero, RowBase(c.y) + SlotLanding, Shade(t.upperElev), false);
            }
        }
    }

    // Wall spans (logical grid, unprojected): each wall column is coloured by its resolved style, white ticks mark seams.
    void DrawSpanDebug()
    {
        float th = cellSize * 0.3f;
        foreach (var r in Topology.runs)
            for (int x = r.x0; x <= r.x1; x++)
            {
                int sid = r.colStyle[x - r.x0];
                PutFlat(new Vector2(x * cellSize, r.upperRow * cellSize - th / 2), new Vector2(cellSize, th), sid < 0 ? Color.black : Color.HSVToRGB(Hash(sid, 11, 3), 0.7f, 0.9f), 5);
                if (x < r.x1 && r.colStyle[x - r.x0 + 1] != sid)
                    PutFlat(new Vector2((x + 1) * cellSize - th * 0.15f, r.upperRow * cellSize - th), new Vector2(th * 0.3f, th * 2), Color.white, 6);
            }
        foreach (var sr in Topology.sideRuns)
            for (int y = sr.y0; y <= sr.y1; y++)
            {
                int sid = sr.rowStyle[y - sr.y0];
                PutFlat(new Vector2((sr.east ? sr.x + 1 : sr.x) * cellSize - th / 2, y * cellSize), new Vector2(th, cellSize), sid < 0 ? Color.black : Color.HSVToRGB(Hash(sid, 11, 3), 0.7f, 0.9f), 5);
            }
    }

    // Transitions debug view (logical grid, unprojected): green = top cells, yellow = cells the steps are drawn over,
    // blue = landing cells, white tick = descent direction at the stair's upper endpoint.
    void DrawTransitionDebug()
    {
        float th = cellSize * 0.2f;
        foreach (var t in Topology.transitions)
        {
            foreach (var c in t.upperCells) PutFlat(new Vector2(c.x * cellSize, c.y * cellSize), new Vector2(cellSize, cellSize), new Color(0.2f, 1f, 0.4f, 0.65f), 5);
            foreach (var c in t.descentCells) PutFlat(new Vector2(c.x * cellSize, c.y * cellSize), new Vector2(cellSize, cellSize), new Color(1f, 0.85f, 0.2f, 0.65f), 5);
            foreach (var c in t.landingCells) PutFlat(new Vector2(c.x * cellSize, c.y * cellSize), new Vector2(cellSize, cellSize), new Color(0.3f, 0.6f, 1f, 0.65f), 5);
            var u = t.upperEnd; var d = t.direction;
            PutFlat(new Vector2((u.x + 0.5f + d.x * 0.35f) * cellSize - th / 2, (u.y + 0.5f + d.y * 0.35f) * cellSize - th / 2), new Vector2(th, th), Color.white, 6);
        }
    }

    // ---- Debug views --------------------------------------------------------------------------
    // Logic / Heights / Surfaces / Boundaries draw the LOGICAL grid at elevation 0 (no projection).
    void RenderLogicalDebug()
    {
        int w = width, h = height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, t = types[i], hv = hm[i]; Color c;
                switch (view)
                {
                    case View.Logic: c = ct.defs[t].debugColor; break;
                    case View.Heights: { float g = (float)hv / Mathf.Max(1, maxLevel); c = new Color(g, g, g); break; }
                    case View.Surfaces: c = Color.HSVToRGB(Hash(Topology.surfaceOf[i], 7, 7), 0.45f, Mathf.Clamp01(0.5f + 0.08f * hv)); break;
                    default: { float g = 0.15f + 0.5f * hv / Mathf.Max(1, maxLevel); c = new Color(g, g, g); break; }
                }
                PutFlat(new Vector2(x * cellSize, y * cellSize), new Vector2(cellSize, cellSize), c, 0);
                if (view == View.Logic && reserved[i] >= 0) PutFlat(new Vector2(x * cellSize, y * cellSize), new Vector2(cellSize, cellSize), new Color(1, 1, 1, 0.45f), 1);
            }
        if (view == View.CliffSpans) { DrawSpanDebug(); DrawTransitionDebug(); return; }
        if (view == View.Transitions) { DrawTransitionDebug(); return; }
        if (view != View.Boundaries) return;
        float th = cellSize * 0.12f;
        foreach (var r in Topology.runs) // red = exposed south boundary; end markers: yellow convex, green concave, white map edge
        {
            for (int x = r.x0; x <= r.x1; x++) PutFlat(new Vector2(x * cellSize, r.upperRow * cellSize - th / 2), new Vector2(cellSize, th), Color.red, 5);
            Color L = r.leftEnd == RunEnd.Convex ? Color.yellow : r.leftEnd == RunEnd.Concave ? Color.green : Color.white;
            Color R = r.rightEnd == RunEnd.Convex ? Color.yellow : r.rightEnd == RunEnd.Concave ? Color.green : Color.white;
            PutFlat(new Vector2(r.x0 * cellSize, r.upperRow * cellSize - th), new Vector2(th * 1.6f, th * 2), L, 6);
            PutFlat(new Vector2((r.x1 + 1) * cellSize - th * 1.6f, r.upperRow * cellSize - th), new Vector2(th * 1.6f, th * 2), R, 6);
        }
        foreach (var e in Topology.sides) // blue = side edge
            PutFlat(new Vector2(e.east ? (e.x + 1) * cellSize - th : e.x * cellSize, e.y * cellSize), new Vector2(th, cellSize), new Color(0.3f, 0.5f, 1f), 5);
    }

    // Projection / RenderOrder draw flat boxes at their PROJECTED positions using the real sorting
    // slots, so overlap and occlusion can be judged without art. Faces are the darker boxes.
    void RenderProjectedDebug(bool byRow)
    {
        int w = width, h = height; float inset = cellSize * 0.03f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int e = hm[y * w + x]; Vector2 o = CellOrigin(x, y, e);
                Color c = byRow ? Color.HSVToRGB(((h - 1 - y) * 0.07f) % 1f, 0.55f, 0.9f)
                                : Color.Lerp(new Color(0.2f, 0.3f, 0.5f), new Color(0.9f, 0.95f, 1f), (float)e / Mathf.Max(1, maxLevel));
                PutFlat(o + new Vector2(inset, inset), new Vector2(cellSize - 2 * inset, cellSize - 2 * inset), c, RowBase(y) + SlotGround);
            }
        foreach (var r in Topology.runs)
        {
            float yBottom = r.upperRow * cellSize + r.lowerElev * elevationStepWorld;
            Color c = byRow ? Color.HSVToRGB(((h - 1 - r.upperRow) * 0.07f) % 1f, 0.8f, 0.5f) : new Color(0.6f, 0.15f, 0.15f);
            for (int x = r.x0; x <= r.x1; x++)
                PutFlat(new Vector2(x * cellSize + inset, yBottom), new Vector2(cellSize - 2 * inset, r.Drop * elevationStepWorld), c, RowBase(r.upperRow) + SlotFace);
        }
    }

    void DrawProbe()
    {
        int px = Mathf.Clamp(probeCell.x, 0, width - 1), py = Mathf.Clamp(probeCell.y, 0, height - 1);
        Vector2 foot = GridToWorld(px + 0.5f, py, hm[py * width + px]);
        PutFlat(new Vector2(foot.x - 0.25f * cellSize, foot.y), new Vector2(0.5f * cellSize, 1.3f * cellSize), Color.magenta, SortingOrderFor(py, SlotProbe));
    }

    void FrameCamera(bool projected)
    {
        var cam = Camera.main; if (!autoFrameCamera || cam == null) return;
        float top = height * cellSize + (projected ? maxLevel * elevationStepWorld : 0f);
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(top / 2f, width * cellSize / 2f / cam.aspect);
        cam.transform.position = new Vector3(width * cellSize / 2f, top / 2f, -10);
    }
}
