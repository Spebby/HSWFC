// TerrainTopology.cs
// LOGICAL spatial description of the map, derived from types[] and hm[] only.
// Nothing here knows about sprites, projection or sorting, so a future gameplay layer can use it as is.
//   TerrainSurface : a connected plateau (4-connected cells of equal elevation)
//   CliffRun       : a maximal horizontal run of exposed south-facing boundaries (upper row vs the row below).
//                    Runs are purely geometric: they do not split where the terrain or the cliff art changes.
//   SideRun        : a vertical stretch of a plateau side whose west/east neighbours are lower
//   SideEdge       : the same, one cell at a time
//   Transition     : a generated stair / waterfall-like connection placed on a run (filled by the generator)
using System.Collections.Generic;
using UnityEngine;

public enum RunEnd { Convex, Concave, MapEdge }

public class TerrainSurface
{
    public int id, elevation;
    public List<Vector2Int> cells = new List<Vector2Int>();
    public RectInt bounds;
}

public class CliffRun
{
    public int upperRow;            // grid y of the raised cells; the lower cells are in row upperRow-1
    public int x0, x1;              // inclusive column range
    public int upperElev, lowerElev;
    public int upperSurface, lowerSurface; // TerrainSurface ids; lowerSurface = -1 for the virtual ground below row 0
    public RunEnd leftEnd, rightEnd;
    public int[] upperTerrains;     // terrain index of the raised cell, per column (index = x - x0)
    public int[] lowerTerrains;     // terrain index of the cell below, per column; -1 = virtual ground below row 0
    public int[] colStyle;          // resolved wall style id per column (filled by the generator, -1 = none)
    public int Drop { get { return upperElev - lowerElev; } }
    public int Length { get { return x1 - x0 + 1; } }
}

public class SideEdge
{
    public int x, y;
    public bool east;               // true = the lower neighbour is to the east
    public int upperElev, lowerElev, upperSurface, lowerSurface, terrain;
}

/// <summary>A vertical stretch of one plateau side: cells in rows y0..y1 of column x whose west/east neighbour is lower.</summary>
public class SideRun
{
    public int x, y0, y1;
    public bool east;               // true = the lower neighbours are to the east
    public int upperElev, lowerElev, upperSurface, lowerSurface;
    public int[] upperTerrains, lowerTerrains; // per row (index = y - y0); lower = the neighbour beside it
    public int[] rowStyle;          // resolved wall style id per row (filled by the generator, -1 = none)
    public int Drop { get { return upperElev - lowerElev; } }
    public int Length { get { return y1 - y0 + 1; } }
}

public enum EdgeSide { South, West, East }   // north edges never get transitions
public enum TransitionKind { Stairs }

/// <summary>
/// A generated visual connection between a plateau and the lower surface beside it. Pure data:
/// logical cells, directions and elevations only, nothing about sprites. A future gameplay layer can read it.
/// </summary>
public class Transition
{
    public TransitionKind kind;
    public EdgeSide side;
    public int featureId;               // index into CompiledTerrain.features
    public int styleId;                 // wall style id of the first column (see CliffRun.colStyle)
    public int leftStyleId, rightStyleId; // wall styles of the columns flanking a south feature
    public Vector2Int direction;        // grid direction of descent: (0,-1) south, (-1,0) west, (1,0) east
    public int width;                   // cells along the edge
    public int upperElev, lowerElev, upperSurface, lowerSurface;
    public Vector2Int[] upperCells;     // plateau cells at the top of the feature
    public Vector2Int[] descentCells;   // side stairs: lower-surface cells the steps are drawn over (step j, 1-based, has visual elevation upperElev - j)
    public Vector2Int[] landingCells;   // lower-surface cells at the foot
    public Vector2Int upperEnd, lowerEnd; // representative top / bottom endpoint cells
    public int Drop { get { return upperElev - lowerElev; } }
}

public class TerrainTopology
{
    public int width, height;
    public int[] surfaceOf;         // surface id per cell
    public List<TerrainSurface> surfaces = new List<TerrainSurface>();
    public List<CliffRun> runs = new List<CliffRun>();
    public List<SideEdge> sides = new List<SideEdge>();
    public List<SideRun> sideRuns = new List<SideRun>();
    public List<Transition> transitions = new List<Transition>(); // filled by the generator's placement pass

    public static TerrainTopology Build(int w, int h, int[] types, int[] hm)
    {
        var t = new TerrainTopology { width = w, height = h, surfaceOf = new int[w * h] };
        for (int i = 0; i < t.surfaceOf.Length; i++) t.surfaceOf[i] = -1;
        int[] dx = { 0, 1, 0, -1 }, dy = { 1, 0, -1, 0 };

        // Surfaces: flood fill on equal elevation (terrain type is irrelevant).
        var stack = new Stack<int>();
        for (int s0 = 0; s0 < w * h; s0++)
        {
            if (t.surfaceOf[s0] >= 0) continue;
            var surf = new TerrainSurface { id = t.surfaces.Count, elevation = hm[s0] };
            int minx = w, miny = h, maxx = 0, maxy = 0;
            t.surfaceOf[s0] = surf.id; stack.Push(s0);
            while (stack.Count > 0)
            {
                int i = stack.Pop(), x = i % w, y = i / w;
                surf.cells.Add(new Vector2Int(x, y));
                minx = Mathf.Min(minx, x); maxx = Mathf.Max(maxx, x); miny = Mathf.Min(miny, y); maxy = Mathf.Max(maxy, y);
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + dx[k], ny = y + dy[k]; if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int j = ny * w + nx;
                    if (t.surfaceOf[j] < 0 && hm[j] == surf.elevation) { t.surfaceOf[j] = surf.id; stack.Push(j); }
                }
            }
            surf.bounds = new RectInt(minx, miny, maxx - minx + 1, maxy - miny + 1);
            t.surfaces.Add(surf);
        }

        // Cliff runs: row y is "upper", row y-1 is "lower". Below row 0 is virtual ground at elevation 0,
        // so plateaus on the bottom edge of the map still get a face. Runs only break on a change of elevations.
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                int up = hm[y * w + x], lo = y > 0 ? hm[(y - 1) * w + x] : 0;
                if (up <= lo) { x++; continue; }
                int x1 = x;
                while (x1 + 1 < w)
                {
                    int u2 = hm[y * w + x1 + 1], l2 = y > 0 ? hm[(y - 1) * w + x1 + 1] : 0;
                    if (u2 != up || l2 != lo) break;
                    x1++;
                }
                var run = new CliffRun
                {
                    upperRow = y, x0 = x, x1 = x1, upperElev = up, lowerElev = lo,
                    upperSurface = t.surfaceOf[y * w + x], lowerSurface = y > 0 ? t.surfaceOf[(y - 1) * w + x] : -1,
                    upperTerrains = new int[x1 - x + 1], lowerTerrains = new int[x1 - x + 1], colStyle = new int[x1 - x + 1]
                };
                for (int c = x; c <= x1; c++)
                {
                    run.upperTerrains[c - x] = types[y * w + c];
                    run.lowerTerrains[c - x] = y > 0 ? types[(y - 1) * w + c] : -1;
                    run.colStyle[c - x] = -1;
                }
                // Run ends: map edge, concave (the plateau continues south beside the run), or convex (open).
                run.leftEnd = x == 0 ? RunEnd.MapEdge : (y > 0 && hm[(y - 1) * w + x - 1] >= up ? RunEnd.Concave : RunEnd.Convex);
                run.rightEnd = x1 == w - 1 ? RunEnd.MapEdge : (y > 0 && hm[(y - 1) * w + x1 + 1] >= up ? RunEnd.Concave : RunEnd.Convex);
                t.runs.Add(run);
                x = x1 + 1;
            }
        }

        // Side edges.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (x > 0 && hm[i - 1] < hm[i])
                    t.sides.Add(new SideEdge { x = x, y = y, east = false, upperElev = hm[i], lowerElev = hm[i - 1], upperSurface = t.surfaceOf[i], lowerSurface = t.surfaceOf[i - 1], terrain = types[i] });
                if (x < w - 1 && hm[i + 1] < hm[i])
                    t.sides.Add(new SideEdge { x = x, y = y, east = true, upperElev = hm[i], lowerElev = hm[i + 1], upperSurface = t.surfaceOf[i], lowerSurface = t.surfaceOf[i + 1], terrain = types[i] });
            }

        // Side runs: group side edges per column, per direction.
        for (int x = 0; x < w; x++)
            for (int e = 0; e < 2; e++)
            {
                bool east = e == 1; int nx = east ? x + 1 : x - 1; if (nx < 0 || nx >= w) continue;
                int y = 0;
                while (y < h)
                {
                    int up = hm[y * w + x], lo = hm[y * w + nx];
                    if (up <= lo) { y++; continue; }
                    int y1 = y;
                    while (y1 + 1 < h)
                    {
                        int u2 = hm[(y1 + 1) * w + x], l2 = hm[(y1 + 1) * w + nx];
                        if (u2 != up || l2 != lo) break;
                        y1++;
                    }
                    var sr = new SideRun
                    {
                        x = x, y0 = y, y1 = y1, east = east, upperElev = up, lowerElev = lo,
                        upperSurface = t.surfaceOf[y * w + x], lowerSurface = t.surfaceOf[y * w + nx],
                        upperTerrains = new int[y1 - y + 1], lowerTerrains = new int[y1 - y + 1], rowStyle = new int[y1 - y + 1]
                    };
                    for (int r = y; r <= y1; r++) { sr.upperTerrains[r - y] = types[r * w + x]; sr.lowerTerrains[r - y] = types[r * w + nx]; sr.rowStyle[r - y] = -1; }
                    t.sideRuns.Add(sr);
                    y = y1 + 1;
                }
            }
        return t;
    }
}
