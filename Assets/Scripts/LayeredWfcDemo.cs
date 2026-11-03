// LayeredWfcDemo.cs
// ---------------------------------------------------------------------------
// Layered WFC terrain + overlay transitions + multi-height cliffs.
//
// SETUP: make an empty GameObject, add this component, press Play.
//        Works with the built-in or URP 2D pipeline (only SpriteRenderers are used).
//        Tweak any inspector value while playing to regenerate; press R for a new seed.
//
// PIPELINE (each stage only reads the stages before it):
//   1. RunWfc()      logical terrain type per cell (water / dirt / grass / sand)
//   2. BuildHeights() integer height per cell, independent of terrain
//   3. Generate()    deterministic render pass: base tiles -> transparent
//                    transition overlays -> cliff faces / trees, y-sorted
//
// Coordinates: cell (x,y) covers world [x,x+1] x [y,y+1]; +y is "north".
// Every sprite is generated procedurally at startup, so there are no assets.
// To use your own art, replace the Make*/BuildSprites code with sprite lookups.
// ---------------------------------------------------------------------------
using System;
using UnityEngine;

public class LayeredWfcDemo : MonoBehaviour
{
    public enum View { Final, Logic, Heights }

    [Header("Generation")]
    public int seed = 7;
    public int width = 36, height = 26;
    [Range(0f, 1f)] public float regionalBias = 0.7f; // 0 = pure WFC noise, 1 = strong biomes
    public float weightWater = 1, weightDirt = 1, weightGrass = 2, weightSand = 1;

    [Header("Heights / cliffs")]
    [Range(0.05f, 0.9f)] public float heightAmount = 0.45f; // fraction of the map that is raised
    [Range(1, 8)] public int maxLevel = 4;                  // highest height level
    public bool clampStepSize = false;                      // off = cliffs may be many levels tall
    [Range(1, 4)] public int maxStep = 1;                   // only used when clampStepSize is on
    [Range(0f, 0.3f)] public float treeDensity = 0.08f;

    [Header("Display")]
    public View view = View.Final;
    public bool transitions = true, cliffs = true, trees = true;
    public bool autoFrameCamera = true;

    // Terrain ids double as overlay priority: a higher id draws over a lower one.
    const int Water = 0, Dirt = 1, Grass = 2, Sand = 3, T = 16; // T = tile size in pixels
    static readonly Color32[] Col = {
        new Color32(58, 110, 170, 255), new Color32(140, 104, 70, 255),
        new Color32(84, 150, 70, 255),  new Color32(222, 200, 140, 255) };

    // Which terrain types may sit next to each type (bitmask, bit i = type i).
    // Water only touches water/sand; sand touches everything. This is the WFC rule set.
    static readonly byte[] Adj = { 0b1001, 0b1110, 0b1110, 0b1111 };

    // Neighbour directions: edges N,E,S,W then corners NE,SE,SW,NW.
    // Corner c is bounded by edges c and (c+1)%4, which the overlay code relies on.
    static readonly Vector2Int[] D4 = { new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(-1, 0) };
    static readonly Vector2Int[] CD = { new Vector2Int(1, 1), new Vector2Int(1, -1), new Vector2Int(-1, -1), new Vector2Int(-1, 1) };

    // Sprite caches, built once.
    Sprite[][] baseSpr, ovSpr, corSpr; // [terrain][variant | edge mask | corner]
    Sprite[] faceSpr;                  // 0 single, 1 top, 2 middle, 3 bottom
    Sprite sideL, sideR, treeSpr, flatSpr;

    int[] types, hm;   // logical terrain layer and height layer, index = y*width+x
    Transform root;
    bool dirty;

    // ------------------------------------------------------------------ lifecycle
    void Start() { Generate(); }
    void OnValidate() { dirty = true; } // defer: can't spawn objects inside OnValidate
    void Update()
    {
        if (dirty && Application.isPlaying) { dirty = false; Generate(); }
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.R)) { seed = UnityEngine.Random.Range(0, 1000000); Generate(); }
#endif
    }

    // ------------------------------------------------------------------ helpers
    // Deterministic per-pixel hash in [0,1). Used for sprite noise so the art is stable.
    static float Hash(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)(a * 374761393 + b * 668265263 + c * 1013904223);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    bool In(int x, int y) { return x >= 0 && y >= 0 && x < width && y < height; }
    int Ty(int x, int y) { return In(x, y) ? types[y * width + x] : -1; } // -1 outside map
    int Hg(int x, int y) { return In(x, y) ? hm[y * width + x] : -1; }

    // Smooth value noise in [0,1], used for biome bias and heights.
    static float[] Noise(int w, int h, float sc, System.Random R)
    {
        int gw = Mathf.CeilToInt(w / sc) + 2, gh = Mathf.CeilToInt(h / sc) + 2;
        var g = new float[gw * gh];
        for (int i = 0; i < g.Length; i++) g[i] = (float)R.NextDouble();
        var o = new float[w * h];
        float lo = 1e9f, hi = -1e9f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float fx = x / sc, fy = y / sc; int ix = (int)fx, iy = (int)fy;
                float tx = fx - ix, ty = fy - iy;
                tx *= tx * (3 - 2 * tx); ty *= ty * (3 - 2 * ty); // smoothstep
                float a = g[iy * gw + ix], b = g[iy * gw + ix + 1], c = g[(iy + 1) * gw + ix], d = g[(iy + 1) * gw + ix + 1];
                float v = a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty;
                o[y * w + x] = v; lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v);
            }
        for (int i = 0; i < o.Length; i++) o[i] = (o[i] - lo) / Mathf.Max(1e-6f, hi - lo); // normalise to 0..1
        return o;
    }

    // ================================================================== SPRITES
    // Everything below builds 16x16 sprites from code. The pixel callback receives
    // (x,y) with y = 0 at the BOTTOM row (Unity texture convention).
    static Sprite Make(int w, int h, Vector2 pivot, Func<int, int, Color32> px)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp }; // Point = crisp pixel art
        var pix = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) pix[y * w + x] = px(x, y);
        tex.SetPixels32(pix); tex.Apply();
        // pixelsPerUnit = T, so one 16px tile is exactly one world unit.
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, T);
    }

    static readonly Color32 Clear = new Color32(0, 0, 0, 0);

    // Base look of a terrain type: its colour with per-pixel brightness noise.
    // 'k' just selects a variant so neighbouring tiles don't look identical.
    static Color32 TerrainPx(int t, int x, int y, int k)
    {
        float v = Hash(x, y, k + t * 7), f = 0.9f + 0.2f * v; // brightness 0.9..1.1
        float r = Col[t].r * f, g = Col[t].g * f, b = Col[t].b * f;
        if (t == Grass && v > 0.9f) { r = 60; g = 125; b = 55; }                    // darker grass tufts
        if (t == Water && (y + (x >> 1)) % 5 == 0) { r *= 1.15f; g *= 1.15f; b *= 1.15f; } // faint ripples
        return new Color32((byte)Mathf.Min(255, r), (byte)Mathf.Min(255, g), (byte)Mathf.Min(255, b), 255);
    }

    // A face (cliff wall) piece. A cliff N levels tall is stacked from N pieces:
    //   lip = lighter ledge line on top, shadow = darkening at the foot.
    // Single pieces have both, top has lip only, bottom has shadow only, middle has neither.
    static Sprite MakeFace(bool lip, bool shadow)
    {
        return Make(T, T, Vector2.zero, (x, y) =>
        {
            int fromTop = T - 1 - y;
            float f = 0.9f + 0.2f * Hash(x, y, 99);
            Color32 c;
            if (lip && fromTop < 2) c = new Color32(170, 135, 95, 255);          // sunlit ledge
            else if (y % 4 == 1)    c = new Color32(95, 70, 50, 255);            // dark strata line (16 % 4 == 0, so it tiles vertically)
            else                    c = new Color32(125, 95, 68, 255);           // rock
            if (shadow && y < 3) f *= 0.7f + 0.1f * y;                           // ambient occlusion at the base
            return new Color32((byte)(c.r * f), (byte)(c.g * f), (byte)(c.b * f), 255);
        });
    }

    void BuildSprites()
    {
        if (baseSpr != null) return;
        baseSpr = new Sprite[4][]; ovSpr = new Sprite[4][]; corSpr = new Sprite[4][];

        for (int t = 0; t < 4; t++)
        {
            int tt = t; // capture for lambdas

            // 4 plain variants of the opaque base tile.
            baseSpr[t] = new Sprite[4];
            for (int k = 0; k < 4; k++) { int kk = k; baseSpr[t][k] = Make(T, T, Vector2.zero, (x, y) => TerrainPx(tt, x, y, kk)); }

            // EDGE OVERLAYS: the key trick. One transparent-edged sprite per (terrain, 4-bit mask).
            // Mask bit 1=N 2=E 4=S 8=W means "a higher-priority neighbour of type tt lies on that side".
            // Pixels near a flagged edge are painted in tt's colour; everything else stays transparent,
            // so the sprite can be drawn on top of ANY base tile.
            ovSpr[t] = new Sprite[16];
            for (int m = 1; m < 16; m++)
            {
                int mm = m;
                ovSpr[t][m] = Make(T, T, Vector2.zero, (x, y) =>
                {
                    float u = (x + 0.5f) / T, v = (y + 0.5f) / T; // pixel centre in 0..1
                    float d = 9f;                                  // distance to nearest flagged edge
                    if ((mm & 1) != 0) d = Mathf.Min(d, 1 - v);    // N edge = top
                    if ((mm & 2) != 0) d = Mathf.Min(d, 1 - u);    // E edge = right
                    if ((mm & 4) != 0) d = Mathf.Min(d, v);        // S edge = bottom
                    if ((mm & 8) != 0) d = Mathf.Min(d, u);        // W edge = left
                    // Threshold = ~0.2..0.42 of the tile, jittered per pixel -> ragged shoreline.
                    return d < 0.2f + 0.22f * Hash(x, y, tt + 50) ? TerrainPx(tt, x, y, 9) : Clear;
                });
            }

            // CORNER OVERLAYS: only needed when a DIAGONAL neighbour is the other type but neither
            // adjacent edge is, otherwise the edge overlays already cover it.
            corSpr[t] = new Sprite[4];
            for (int c = 0; c < 4; c++)
            {
                int cu = CD[c].x > 0 ? 1 : 0, cv = CD[c].y > 0 ? 1 : 0; // corner position in tile space
                corSpr[t][c] = Make(T, T, Vector2.zero, (x, y) =>
                {
                    float d = Vector2.Distance(new Vector2((x + 0.5f) / T, (y + 0.5f) / T), new Vector2(cu, cv));
                    return d < 0.3f + 0.2f * Hash(x, y, tt + 80) ? TerrainPx(tt, x, y, 9) : Clear; // quarter-blob
                });
            }
        }

        faceSpr = new[] { MakeFace(true, true), MakeFace(true, false), MakeFace(false, false), MakeFace(false, true) };

        // Thin dark outline on the side of a raised cell next to a lower one.
        sideL = Make(T, T, Vector2.zero, (x, y) => x == 0 ? new Color32(0, 0, 0, 80) : Clear);
        sideR = Make(T, T, Vector2.zero, (x, y) => x == T - 1 ? new Color32(0, 0, 0, 80) : Clear);

        // TREE: a 16x24 "tall object". Pivot is bottom-centre, so it is placed by its base point
        // and its canopy naturally overlaps the cell above (north) it.
        treeSpr = Make(T, 24, new Vector2(0.5f, 0f), (x, y) =>
        {
            float v = Hash(x, y, 5);
            if (x >= 6 && x <= 9 && y < 8) return new Color32(90, 60, 40, 255);        // trunk
            float d = Vector2.Distance(new Vector2(x, y), new Vector2(7.5f, 14.5f));    // canopy circle
            if (d >= 7.5f) return Clear;
            return new Color32((byte)(30 + v * 20), (byte)((d > 5 ? 85 : 105) + v * 35), 45, 255); // rim a bit darker
        });

        // 1x1-unit white square, tinted per cell for the Logic and Heights views.
        flatSpr = Make(T, T, Vector2.zero, (x, y) => new Color32(255, 255, 255, 255));
    }

    // ================================================================== LAYER 1: WFC
    // Classic simple-tiled WFC where each "tile" is just a terrain type and adjacency
    // is the Adj table. Per-cell weights come from a low-frequency noise field so
    // the result forms coherent regions; WFC then enforces the shoreline rules.
    int RunWfc(System.Random R, out int[] result)
    {
        int w = width, h = height, N = w * h;
        float[] bias = Noise(w, h, 7, R);
        float[] wt = { weightWater, weightDirt, weightGrass, weightSand };
        float[] center = { 0.12f, 0.85f, 0.55f, 0.3f }; // where on the noise field each type "likes" to be
        var W8 = new float[N * 4];
        for (int i = 0; i < N; i++)
            for (int t = 0; t < 4; t++)
                W8[i * 4 + t] = Mathf.Max(1e-4f, wt[t] * Mathf.Exp(-20f * regionalBias * Mathf.Pow(bias[i] - center[t], 2)));

        var dom = new byte[N]; // per-cell bitmask of still-possible types
        var stack = new System.Collections.Generic.Stack<int>();

        bool Solve()
        {
            for (; ; )
            {
                // 1. Observe: pick the uncollapsed cell with the lowest Shannon entropy.
                int best = -1; float bestE = 1e9f;
                for (int i = 0; i < N; i++)
                {
                    int m = dom[i];
                    if ((m & (m - 1)) == 0) continue; // 0 or 1 bit set = already decided
                    float s = 0, e = 0;
                    for (int t = 0; t < 4; t++) if ((m >> t & 1) != 0) { float v = W8[i * 4 + t]; s += v; e += v * Mathf.Log(v); }
                    float ent = Mathf.Log(s) - e / s + (float)R.NextDouble() * 1e-3f; // tiny noise breaks ties
                    if (ent < bestE) { bestE = ent; best = i; }
                }
                if (best < 0) return true; // everything collapsed

                // 2. Collapse it to one weighted-random type.
                int mask = dom[best]; float sum = 0;
                for (int t = 0; t < 4; t++) if ((mask >> t & 1) != 0) sum += W8[best * 4 + t];
                float r = (float)R.NextDouble() * sum; int pick = 0;
                for (int t = 0; t < 4; t++) if ((mask >> t & 1) != 0) { pick = t; r -= W8[best * 4 + t]; if (r <= 0) break; }
                dom[best] = (byte)(1 << pick); stack.Push(best);

                // 3. Propagate: shrink neighbours' domains to what the changed cell allows.
                while (stack.Count > 0)
                {
                    int i = stack.Pop(), x = i % w, y = i / w, allowed = 0;
                    for (int t = 0; t < 4; t++) if ((dom[i] >> t & 1) != 0) allowed |= Adj[t];
                    foreach (var d in D4)
                    {
                        int nx = x + d.x, ny = y + d.y;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx, nd = dom[j] & allowed;
                        if (nd == dom[j]) continue;
                        if (nd == 0) return false; // contradiction -> caller restarts
                        dom[j] = (byte)nd; stack.Push(j);
                    }
                }
            }
        }

        int tries = 0;
        for (; tries < 60; tries++)
        {
            for (int i = 0; i < N; i++) dom[i] = 0b1111;
            stack.Clear();
            if (Solve()) break;
        }
        result = new int[N];
        for (int i = 0; i < N; i++) result[i] = (int)Mathf.Log(Mathf.Max(1, (int)dom[i]), 2); // bit index
        return tries + 1;
    }

    // ================================================================== LAYER 2: HEIGHTS
    // Heights are a separate integer field. Cliffs are derived from them later;
    // there is no "cliff tile" in the generator.
    int[] BuildHeights(System.Random R)
    {
        int w = width, h = height;
        float[] n = Noise(w, h, 9, R);
        var H = new int[w * h];
        // Only the top 'heightAmount' of the noise range is raised; the rest stays at 0.
        for (int i = 0; i < H.Length; i++)
            H[i] = Mathf.RoundToInt(Mathf.Clamp01((n[i] - (1 - heightAmount)) / heightAmount) * maxLevel);

        // Minimum plateau size: a cell with <2 equal neighbours takes its neighbours' most common height.
        for (int pass = 0; pass < 3; pass++)
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

        // Optional: limit how tall a single cliff may be. Off by default, so 2-4 level walls appear.
        if (clampStepSize)
            for (bool ch = true; ch;)
            {
                ch = false;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        foreach (var d in D4)
                        {
                            int nx = x + d.x, ny = y + d.y; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            if (H[y * w + x] > H[ny * w + nx] + maxStep) { H[y * w + x] = H[ny * w + nx] + maxStep; ch = true; }
                        }
            }
        return H;
    }

    // ================================================================== LAYER 3: RENDER
    // Spawns one SpriteRenderer per piece. Sorting orders:
    //   0 ground | 1..4 overlays (by priority) | 6 side outlines | 100+ cliffs & trees, y-sorted
    // A Tilemap would be far lighter for real use; individual objects keep this demo readable.
    SpriteRenderer Put(Sprite s, float x, float y, int order, Color col)
    {
        var go = new GameObject(s.name);
        go.transform.SetParent(root, false);
        go.transform.localPosition = new Vector3(x, y, 0);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s; sr.sortingOrder = order; sr.color = col;
        return sr;
    }

    // Lower rows are closer to the camera, so they get higher sorting orders.
    int RowOrder(int y) { return 100 + (height - y) * 10; }

    public void Generate()
    {
        BuildSprites();
        if (root != null) { if (Application.isPlaying) Destroy(root.gameObject); else DestroyImmediate(root.gameObject); }
        root = new GameObject("Map").transform; root.SetParent(transform, false);

        int w = width, h = height;
        // Separate RNG streams so changing one slider doesn't reshuffle unrelated layers.
        RunWfc(new System.Random(seed), out types);
        hm = BuildHeights(new System.Random(seed ^ unchecked((int)0x9e3779b9)));

        // Water can't sit on or beside a height change. Sand is allowed next to anything,
        // so this swap never breaks the WFC adjacency rules.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (types[y * w + x] != Water) continue;
                bool bad = hm[y * w + x] > 0;
                foreach (var d in D4) if (In(x + d.x, y + d.y) && Hg(x + d.x, y + d.y) != hm[y * w + x]) bad = true;
                if (bad) types[y * w + x] = Sand;
            }

        // --- Pass 1: ground. Brightness rises with height.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int t = types[y * w + x], hv = hm[y * w + x];
                float shade = 0.7f + 0.3f * hv / maxLevel;
                if (view == View.Logic) Put(flatSpr, x, y, 0, Col[t]);
                else if (view == View.Heights) { float g = (float)hv / maxLevel; Put(flatSpr, x, y, 0, new Color(g, g, g)); }
                else Put(baseSpr[t][(int)(Hash(x, y, 1) * 4)], x, y, 0, new Color(shade, shade, shade));
            }
        if (view != View.Final) { FrameCamera(); return; }

        // --- Pass 2: transition overlays.
        // For each cell, for each terrain with HIGHER priority than the cell's own, build the 4-bit
        // edge mask of neighbours of that terrain and draw the matching transparent overlay on top.
        // Cells at different heights are skipped: a cliff separates them instead of a blend.
        if (transitions)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int t = Ty(x, y), h0 = Hg(x, y);
                    float shade = 0.7f + 0.3f * h0 / maxLevel; var tint = new Color(shade, shade, shade);
                    for (int n = t + 1; n < 4; n++)
                    {
                        int m = 0; var e = new bool[4];
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = x + D4[k].x, ny = y + D4[k].y;
                            e[k] = Ty(nx, ny) == n && Hg(nx, ny) == h0;
                            if (e[k]) m |= 1 << k;
                        }
                        if (m != 0) Put(ovSpr[n][m], x, y, 1 + n, tint);
                        for (int c = 0; c < 4; c++) // corner blob only if neither bounding edge already covers it
                            if (!e[c] && !e[(c + 1) % 4] && Ty(x + CD[c].x, y + CD[c].y) == n && Hg(x + CD[c].x, y + CD[c].y) == h0)
                                Put(corSpr[n][c], x, y, 1 + n, tint);
                    }
                }

        // --- Pass 3: cliffs. For every cell whose NORTH neighbour is higher, stack (heightDiff) face
        // pieces going south, starting in that cell. The pieces sit at a y-sorted order ABOVE the ground
        // so they hide whatever ground and objects are behind them (the Earthbound look).
        var covered = new bool[w * h];
        if (cliffs)
            for (int y = h - 2; y >= 0; y--) // top-down so taller cliffs claim their cells first
                for (int x = 0; x < w; x++)
                {
                    int lo = hm[y * w + x], up = hm[(y + 1) * w + x];
                    if (lo < up)
                    {
                        int dh = up - lo;
                        for (int k = 0; k < dh; k++)
                        {
                            int fy = y - k;
                            if (fy < 0 || hm[fy * w + x] > lo || covered[fy * w + x]) break; // don't wall over taller ground
                            covered[fy * w + x] = true;
                            int piece = dh == 1 ? 0 : k == 0 ? 1 : k == dh - 1 ? 3 : 2;
                            Put(faceSpr[piece], x, fy, RowOrder(fy), Color.white);
                        }
                    }
                    // Dark outline where this cell is higher than its east/west neighbour.
                    int hv = hm[y * w + x];
                    if (In(x - 1, y) && Hg(x - 1, y) < hv) Put(sideL, x, y, 6, Color.white);
                    if (In(x + 1, y) && Hg(x + 1, y) < hv) Put(sideR, x, y, 6, Color.white);
                }

        // --- Pass 4: trees on grass (not under a cliff face). The sorting order places them
        // in front of rows to their north and behind cliff faces to their south.
        if (trees)
        {
            var R = new System.Random(seed ^ 0x1234567);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (types[y * w + x] == Grass && !covered[y * w + x] && R.NextDouble() < treeDensity)
                        Put(treeSpr, x + 0.5f, y, RowOrder(y) + 5, Color.white);
        }
        FrameCamera();
    }

    void FrameCamera()
    {
        var cam = Camera.main;
        if (!autoFrameCamera || cam == null) return;
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(height / 2f, width / 2f / cam.aspect);
        cam.transform.position = new Vector3(width / 2f, height / 2f, -10);
    }
}
