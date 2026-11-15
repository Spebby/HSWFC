// BlobLayouts.cs
// The two standard 47-tile blob sheet layouts, as tables of the standard neighbour mask of every cell
// (bit = neighbour is the same terrain, N=1 NE=2 E=4 SE=8 S=16 SW=32 W=64 NW=128, corner only when both sides are set).
//
//   Godot12x4    : Godot's "3x3 minimal" template (autotile_template_3x3_minimal.png), 12 columns x 4 rows, one empty cell.
//   GameMaker8x6 : GameMaker's 47-tile auto-tile template order, 8 columns x 6 rows. The first cell is GameMaker's reserved tile 0.
//
// Cells are indexed row-major from the top-left: cell = row * Columns + column. A sheet sliced into sprites in reading
// order gives sprites[cell].
//
// A sheet tile with standard mask M draws edge art on the sides where the neighbour is NOT the same terrain. Used as overlay
// art on a target cell, the sprite for "overlay terrain present at P" is therefore the tile with standard mask ~P; SlotToCell
// holds that mapping for every OverlayMasks slot. The overlay of a fully surrounded tile (slot 0, no overlay neighbour) is
// never drawn, so the sheet's full-fill tile is not used for overlays.
using System;
using WFC.Core;


namespace WFC.Terrain {
    public enum BlobLayoutId {
        LINEAR,
        GODOT12_X4,
        GAME_MAKER8_X6
    }

    public sealed class BlobLayout {
        public readonly string name;
        public readonly int columns, rows;

        /// <summary>Standard mask per cell, row-major; -1 = empty cell.</summary>
        public readonly int[] cellMasks;

        /// <summary>Per OverlayMasks slot (0..46): the sheet cell that holds its sprite.</summary>
        public readonly int[] slotToCell;

        /// <summary>Per sheet cell: the OverlayMasks slot it holds, or -1 for an empty cell. Slot 0 is the all-fill tile.</summary>
        public readonly int[] cellToSlot;

        internal BlobLayout(string name, int columns, int rows, int[] cellMasks) {
            if (cellMasks.Length != columns * rows) throw new ArgumentException("grid size mismatch");
            this.name      = name;
            this.columns   = columns;
            this.rows      = rows;
            this.cellMasks = cellMasks;
            slotToCell     = new int[OverlayMasks.BLOB_COUNT];
            for (int i = 0; i < slotToCell.Length; i++) slotToCell[i] = -1;
            for (int cell = 0; cell < cellMasks.Length; cell++) {
                int std = cellMasks[cell];
                if (std < 0) continue;
                if (OverlayMasks.StandardCanonical(std) != std) {
                    throw new ArgumentException(name + ": cell " + cell + " is not a canonical standard mask");
                }

                int slot = OverlayMasks.BlobIndex(OverlayMasks.FromStandardMask(std));
                if (slotToCell[slot] != -1) throw new ArgumentException(name + ": mask " + std + " appears twice");
                slotToCell[slot] = cell;
            }

            for (int i = 0; i < slotToCell.Length; i++) {
                if (slotToCell[i] != -1) continue;
                throw new ArgumentException(name + ": no cell for slot " + i);
            }

            cellToSlot = new int[cellMasks.Length];
            for (int cell = 0; cell < cellToSlot.Length; cell++) cellToSlot[cell]             = -1;
            for (int slot = 0; slot < slotToCell.Length; slot++) cellToSlot[slotToCell[slot]] = slot;
        }

        /// <summary>
        /// Cell index of a sprite from the centre of its rect on the sheet texture (pixels, y measured upwards from the
        /// bottom as in Unity sprite rects). -1 when the point lies outside the grid. Lets a sheet that was sliced without its
        /// empty cell still line up.
        /// </summary>
        public int CellAt(float centerX, float centerYUp, float sheetWidth, float sheetHeight) {
            int col = (int)Math.Floor(centerX / (sheetWidth / columns));
            int row = rows - 1 - (int)Math.Floor(centerYUp / (sheetHeight / rows));
            if (col < 0 || col >= columns || row < 0 || row >= rows) return -1;
            return row * columns + col;
        }

        public int CellCount => columns * rows;
    }

    public static class BlobLayouts {
        public static readonly BlobLayout GODOT12_X4 = new("Godot 3x3 minimal (12x4)", 12, 4,
                                                           new[] {
                                                               16, 20, 84, 80, 213, 92, 116, 87, 28, 125, 124, 112,
                                                               17, 21, 85, 81, 29, 127, 253, 113, 31, 119, -1, 245,
                                                               1, 5, 69, 65, 23, 223, 247, 209, 95, 255, 221, 241,
                                                               0, 4, 68, 64, 117, 71, 197, 93, 7, 199, 215, 193
                                                           });

        public static readonly BlobLayout GAME_MAKER8_X6 = new("GameMaker 47 (8x6)", 8, 6,
                                                               new[] {
                                                                   -1, 255, 127, 253, 125, 247, 119, 245,
                                                                   117, 223, 95, 221, 93, 215, 87, 213,
                                                                   85, 31, 29, 23, 21, 124, 116, 92,
                                                                   84, 241, 209, 113, 81, 199, 71, 197,
                                                                   69, 17, 68, 28, 20, 112, 80, 193,
                                                                   65, 7, 5, 16, 4, 1, 64, 0
                                                               });

        public static BlobLayout Get(BlobLayoutId id) {
            return id switch {
                BlobLayoutId.GAME_MAKER8_X6 => GAME_MAKER8_X6,
                BlobLayoutId.GODOT12_X4     => GODOT12_X4,
                _                           => throw new ArgumentOutOfRangeException(nameof(id), id, null)
            };
        }
    }
}
