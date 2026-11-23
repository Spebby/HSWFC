using System;
using UnityEngine;
using WFC.Core;


namespace WFC.Terrain {
    /// <summary>
    /// Interface for Terrain renderer implementations.
    /// </summary>
    public interface ITerrainRenderer : IDisposable {
        void Render(TerrainRenderContext ctx);
    }

    public enum TerrainRenderStrategy {
        ChunkMesh,
        Tilemap
    }

    public interface ITerrainSpriteSink {
        void AddSprite(Sprite sprite, int x, int y, int order, int layer);
        void AddFlat(Color color, int x, int y, int order);
    }

    public sealed class TerrainRenderContext {
        public readonly Transform root;
        public readonly CompiledTerrain terrain;
        public readonly int[] layout;
        public readonly float[] regionNoise;

        public readonly int width, height;
        public readonly float cellSize;
        public readonly WFCTerrainGenerator.View view;
        public readonly bool overlays;
        internal readonly Func<int, Color> semanticColorAt;

        public TerrainRenderContext(WFCTerrainGenerator generator, Transform root) {
            this.root   = root;
            terrain     = generator.ct;
            layout      = generator.Layout;
            regionNoise = generator.regionNoise;

            width    = generator.width;
            height   = generator.height;
            cellSize = generator.cellSize;
            view     = generator.view;
            overlays = generator.overlays;
            if (view == WFCTerrainGenerator.View.SemanticPaths)
                semanticColorAt = generator.SemanticColorAt;
        }

        public int TerrainAt(int x, int y) => WFCSolver.IsOutOfBounds(x, y, width, height) ? -1 : layout[y * width + x];
    }
}
