// Blob47Set.cs
// One asset holding all 47 blob sprites, so a TerrainDef / CliffStyle takes a single reference
// instead of 47 hand-copied list entries.
using UnityEngine;
#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
#endif

[CreateAssetMenu(menuName = "Terrain/Blob47 Set", fileName = "Blob47Set")]
public class Blob47Set : ScriptableObject
{
    [Tooltip("47 slots in canonical order (right-click > Log Slots). Slot 0 = no neighbours: nothing is drawn, leave empty.")]
    public Sprite[] sprites = new Sprite[47];

    [Header("Fill from a sliced sprite sheet (editor only: right-click > Fill From Sheet)")]
    public Texture2D sheet;
    [Tooltip("Optional remap. slotForSheetIndex[i] = slot for the i-th sprite of the sheet, read row by row from the top-left. Leave empty if the sheet is already in slot order.")]
    public int[] slotForSheetIndex;

    void OnValidate() { if (sprites is not { Length: 47 }) System.Array.Resize(ref sprites, 47); }

    [ContextMenu("Log Slots")]
    void LogSlots() { Debug.Log(OverlayMasks.DescribeBlob47(), this); }

#if UNITY_EDITOR
    [ContextMenu("Fill From Sheet")]
    void FillFromSheet()
    {
        if (sheet == null) { Debug.LogWarning("Assign a sliced sheet texture first.", this); return; }
        // All sprites sliced from the sheet, top row first, left to right (Unity rect.y grows upwards).
        var list = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(sheet)).OfType<Sprite>()
            .OrderByDescending(s => Mathf.Round(s.rect.y)).ThenBy(s => s.rect.x).ToList();
        if (list.Count != 47) Debug.LogWarning("Sheet has " + list.Count + " sprites, expected 47.", this);
        Undo.RecordObject(this, "Fill Blob47");
        sprites = new Sprite[47];
        for (int i = 0; i < list.Count; i++)
        {
            int slot = slotForSheetIndex != null && i < slotForSheetIndex.Length ? slotForSheetIndex[i] : i;
            if (slot is >= 0 and < 47) sprites[slot] = list[i];
        }
        EditorUtility.SetDirty(this);
    }
#endif
}
