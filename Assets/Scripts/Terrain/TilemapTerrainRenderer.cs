using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;


// AI generated
namespace WFC.Terrain {
    public sealed class TilemapTerrainRenderer : ITerrainRenderer, ITerrainSpriteSink {
        readonly Dictionary<Sprite, Tile> _tiles = new();
        readonly Dictionary<(int order, int layer), Tilemap> _maps = new();

        TerrainRenderContext _ctx;
        Transform _gridRoot;
        Sprite _whiteSprite;
        Tile _whiteTile;

        public void Render(TerrainRenderContext ctx) {
            _ctx = ctx;

            var gridObject = new GameObject("Terrain Grid");
            gridObject.transform.SetParent(ctx.root, false);
            gridObject.transform.localPosition = new Vector3(
                -ctx.cellSize * 0.5f,
                -ctx.cellSize * 0.5f,
                0f
            );

            Grid grid = gridObject.AddComponent<Grid>();
            grid.cellSize = new Vector3(ctx.cellSize, ctx.cellSize, 1f);
            _gridRoot = gridObject.transform;

            TerrainRenderTraversal.Render(ctx, this);

            _ctx = null;
        }

        public void AddSprite(Sprite sprite, int x, int y, int order, int layer) {
            if (!sprite)
                return;

            Tilemap map = GetMap(order, layer);
            Tile tile = GetTile(sprite);
            Vector3Int cell = new(x, y, 0);

            map.SetTile(cell, tile);
            map.SetTransformMatrix(cell, SpriteMatrix(sprite));
        }

        public void AddFlat(Color color, int x, int y, int order) {
            Tilemap map = GetMap(order, 0);
            Vector3Int cell = new(x, y, 0);

            map.SetTile(cell, GetWhiteTile());
            map.SetColor(cell, color);
            map.SetTransformMatrix(cell, Matrix4x4.Scale(Vector3.one * _ctx.cellSize));
        }

        Tilemap GetMap(int order, int layer) {
            var key = (order, layer);

            if (_maps.TryGetValue(key, out Tilemap map))
                return map;

            var go = new GameObject($"Tilemap - Order {order}, Layer {layer}");
            go.transform.SetParent(_gridRoot, false);

            map = go.AddComponent<Tilemap>();
            map.tileAnchor = new Vector3(0.5f, 0.5f, 0f);

            TilemapRenderer renderer = go.AddComponent<TilemapRenderer>();
            renderer.mode = TilemapRenderer.Mode.Chunk;
            renderer.sortingOrder = order;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _maps.Add(key, map);
            return map;
        }

        Tile GetTile(Sprite sprite) {
            if (_tiles.TryGetValue(sprite, out Tile tile))
                return tile;

            tile = ScriptableObject.CreateInstance<Tile>();
            tile.name = $"{sprite.name} (Terrain Tile)";
            tile.sprite = sprite;
            tile.flags = TileFlags.LockTransform;

            _tiles.Add(sprite, tile);
            return tile;
        }

        Tile GetWhiteTile() {
            if (_whiteTile)
                return _whiteTile;

            _whiteSprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0, 0, 1, 1),
                new Vector2(0.5f, 0.5f),
                1f
            );

            _whiteTile = GetTile(_whiteSprite);
            return _whiteTile;
        }

        Matrix4x4 SpriteMatrix(Sprite sprite) {
            float ppu = sprite.pixelsPerUnit;
            Vector2 size = sprite.rect.size / ppu;
            Vector2 pivot = sprite.pivot / ppu;

            Vector3 offset = new(
                (pivot.x - size.x * 0.5f) * (_ctx.cellSize / size.x),
                (pivot.y - size.y * 0.5f) * (_ctx.cellSize / size.y),
                0f
            );

            Vector3 scale = new(
                _ctx.cellSize / size.x,
                _ctx.cellSize / size.y,
                1f
            );

            return Matrix4x4.TRS(offset, Quaternion.identity, scale);
        }

        public void Dispose() {
            foreach (Tile tile in _tiles.Values) {
                DestroyObject(tile);
            }

            _tiles.Clear();

            _maps.Clear();

            if (_whiteSprite)
                DestroyObject(_whiteSprite);

            _whiteSprite = null;
            _whiteTile = null;
            _gridRoot = null;
        }

        static void DestroyObject(Object obj) {
            if (Application.isPlaying)
                Object.Destroy(obj);
            else
                Object.DestroyImmediate(obj);
        }
    }
}