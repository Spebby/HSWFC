using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WFC.Core;


namespace WFC.Terrain {
    [CreateAssetMenu(menuName = "Terrain/Blob47 Set", fileName = "Blob47Set")]
    public class Blob47Set : ScriptableObject {
        [Tooltip("47 slots in canonical order (right-click > Log Slots). Slot 0 = no neighbours: nothing is drawn, leave empty.")]
        public Sprite[] sprites = new Sprite[47];

        [Header("Fill from a sliced sprite sheet (editor only: right-click > Fill From Sheet)")]
        public Texture2D sheet;

        [Tooltip("Standard sheet layout. Godot 3x3 minimal = 12x4 (one empty cell), GameMaker 47 = 8x6 (first cell empty). Sprites are placed by their position on the sheet, so empty cells may be left out when slicing. The art is edge art on the sides facing the overlay terrain, with transparent fill.")]
        public BlobLayoutId layout = BlobLayoutId.GODOT12_X4;

        [Tooltip("Optional override for non-standard sheets. slotForSheetIndex[i] = slot for the i-th sprite of the sheet, read row by row from the top-left. When this is not empty, the layout above is ignored.")]
        public int[] slotForSheetIndex;

        void OnValidate() {
            if (sprites is not { Length: 47 }) System.Array.Resize(ref sprites, 47);
        }

        [ContextMenu("Log Slots")]
        void LogSlots() {
            Debug.Log(OverlayMasks.DescribeBlob47(), this);
        }

#if UNITY_EDITOR
        [ContextMenu("Fill From Sheet")]
        void FillFromSheet() {
            if (!sheet) {
                Debug.LogWarning("Assign a sliced sheet texture first.", this);
                return;
            }

            // All sprites sliced from the sheet, top row first, left to right (Unity rect.y grows upwards).
            List<Sprite> list = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(sheet)).OfType<Sprite>()
                                             .OrderByDescending(s => Mathf.Round(s.rect.y)).ThenBy(s => s.rect.x).ToList();
            Undo.RecordObject(this, "Fill Blob47");
            sprites = new Sprite[47];

            if (slotForSheetIndex is { Length: > 0 }) {
                if (list.Count != 47) Debug.LogWarning($"Sheet has {list.Count} sprites, expected 47.", this);
                for (int i = 0; i < list.Count; i++) {
                    int slot = i < slotForSheetIndex.Length ? slotForSheetIndex[i] : i;
                    if (slot is >= 0 and < 47) sprites[slot] = list[i];
                }
            }
            else FillFromLayout(list);

            EditorUtility.SetDirty(this);
        }

        // A sprite's cell comes from where it sits on the sheet, so a sheet sliced without its empty cell still lines up.
        void FillFromLayout(List<Sprite> list) {
            if (layout == BlobLayoutId.LINEAR) {
                FillLinear(list);
                return;
            }

            BlobLayout lay = BlobLayouts.Get(layout);
            if (sheet.width % lay.columns != 0 || sheet.height % lay.rows != 0)
                Debug.LogWarning($"Sheet is {sheet.width}x{sheet.height}, not an exact multiple of {lay.columns}x{lay.rows} cells. Check the layout.", this);

            int placed = 0;
            foreach (Sprite sp in list) {
                int cell = lay.CellAt(sp.rect.center.x, sp.rect.center.y, sheet.width, sheet.height);
                if (cell < 0) {
                    Debug.LogWarning($"Sprite {sp.name} lies outside the {lay.columns}x{lay.rows} grid.", this);
                    continue;
                }

                int slot = lay.cellToSlot[cell];
                switch (slot) {
                    case < 0:
                        Debug.LogWarning($"Sprite {sp.name} sits in an empty cell of the {lay.name} layout (column {cell % lay.columns}, row {cell / lay.columns}).", this);
                        continue;
                    case 0:
                        continue;
                }

                if (sprites[slot]) Debug.LogWarning($"Two sprites map to slot {slot} ({sprites[slot].name}, {sp.name}).", this);
                sprites[slot] = sp;
                placed++;
            }

            if (placed != 46) Debug.LogWarning($"Filled {placed} of 46 drawable slots from the {lay.name} layout.", this);
        }

        void FillLinear(List<Sprite> list) {
            int placed = 0;
            int slot   = 1;

            foreach (Sprite sp in list) {
                if (slot >= sprites.Length) {
                    Debug.LogWarning($"Ignoring sprite {sp.name}: the linear layout only supports 46 drawable sprites.", this);
                    continue;
                }

                sprites[slot++] = sp;
                placed++;
            }

            if (placed != 46) Debug.LogWarning($"Filled {placed} of 46 drawable slots from the linear layout.", this);
        }
#endif
    }
}