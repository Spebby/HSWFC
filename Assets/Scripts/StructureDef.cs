// StructureDef.cs
// A multi-cell object that must stay on one level (building, shrine, well...).
// Placed after heights are generated; it reserves cells so WFC and props leave them alone.
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Terrain/Structure Def", fileName = "Structure_")]
public class StructureDef : ScriptableObject
{
    public enum HeightMode
    {
        RequireFlat, // only placed where the footprint + margin is already one level
        Flatten      // placed anywhere; the footprint + margin is forced to one level (height-locked)
    }

    [Tooltip("Bottom-centre pivot. Placed at the bottom-centre of the footprint.")]
    public Sprite sprite;
    public Vector2 offset;
    [Tooltip("Optional overhang drawn in a separate, more forward sorting slot (eaves, arches, canopy) so it covers things standing in rows south of the structure too. Same pivot rules as the main sprite; placed at the same anchor.")]
    public Sprite foreground;
    [Min(0), Tooltip("How many rows south of the structure's front row the foreground piece still covers.")]
    public int foregroundRowOffset = 1;
    public Vector2Int footprint = new Vector2Int(3, 2);
    [Min(0), Tooltip("Ring of cells around the footprint that must share its level, so the structure is never on a plateau edge. Use at least the height of nearby cliffs, or tall cliff faces will be cut short against it.")]
    public int margin = 1;
    public HeightMode heightMode = HeightMode.RequireFlat;
    [Tooltip("Terrains allowed under the footprint. Empty = any.")]
    public List<TerrainDef> groundTerrains = new List<TerrainDef>();
    [Tooltip("Min / max number to place.")]
    public Vector2Int count = new Vector2Int(1, 2);
    public int attempts = 60;
}