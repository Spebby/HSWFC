using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;


// AI generated
namespace WFC.Terrain {
    /// <summary>
    /// Custom Chunk Mesh generator for the terrain renderer. Definitely overkill, but runs very well
    /// and decently simple.
    /// </summary>
    public sealed class ChunkMeshTerrainRenderer : ITerrainRenderer, ITerrainSpriteSink {
        readonly Material _material;
        readonly int _chunkSize;
        readonly List<Mesh> _meshes = new();
        readonly List<Material> _materials = new();

        TerrainRenderContext _ctx;
        Dictionary<BatchKey, MeshBatch> _batches;
        int _chunksX;

        public ChunkMeshTerrainRenderer(int chunkSize, Material material = null) {
            _chunkSize = Mathf.Max(1, chunkSize);
            _material  = material;
        }

        public void Render(TerrainRenderContext ctx) {
            _ctx     = ctx;
            _batches = new Dictionary<BatchKey, MeshBatch>();
            _chunksX = (ctx.width + _chunkSize - 1) / _chunkSize;

            TerrainRenderTraversal.Render(ctx, this);
            BuildMeshes();
            _batches = null;
            _ctx     = null;
        }

        public void AddSprite(Sprite sp, int x, int y, int order, int layer) {
            if (!sp || !sp.texture) return;

            Texture2D texture = sp.texture;
            float     ppu     = sp.pixelsPerUnit;
            Vector2   size    = sp.rect.size / ppu;
            Vector2   pivot   = sp.pivot / ppu;

            float scaleX = _ctx.cellSize / size.x;
            float scaleY = _ctx.cellSize / size.y;

            Vector3 position = new(
                x * _ctx.cellSize + (pivot.x - size.x * 0.5f) * scaleX,
                y * _ctx.cellSize + (pivot.y - size.y * 0.5f) * scaleY,
                0f
            );

            MeshBatch batch     = GetBatch(texture, x, y, order);
            Vector2[] vertices  = sp.vertices;
            Vector2[] uvs       = sp.uv;
            ushort[]  triangles = sp.triangles;

            int firstVertex = batch.vertices.Count;

            for (int i = 0; i < vertices.Length; i++) {
                Vector2 v = vertices[i];

                batch.vertices.Add(new Vector3(
                                       position.x + v.x * scaleX,
                                       position.y + v.y * scaleY,
                                       position.z
                                   ));

                batch.uvs.Add(uvs[i]);
                batch.colors.Add(Color.white);
            }

            foreach (ushort t in triangles) {
                batch.triangles.Add(firstVertex + t);
            }
        }

        public void AddFlat(Color color, int x, int y, int order) {
            MeshBatch batch = GetBatch(Texture2D.whiteTexture, x, y, order);

            float half = _ctx.cellSize * 0.5f;
            float px   = x * _ctx.cellSize;
            float py   = y * _ctx.cellSize;

            int first = batch.vertices.Count;

            batch.vertices.Add(new Vector3(px - half, py - half, 0f));
            batch.vertices.Add(new Vector3(px + half, py - half, 0f));
            batch.vertices.Add(new Vector3(px + half, py + half, 0f));
            batch.vertices.Add(new Vector3(px - half, py + half, 0f));

            batch.uvs.Add(new Vector2(0f, 0f));
            batch.uvs.Add(new Vector2(1f, 0f));
            batch.uvs.Add(new Vector2(1f, 1f));
            batch.uvs.Add(new Vector2(0f, 1f));

            for (int i = 0; i < 4; i++) {
                batch.colors.Add(color);
            }

            batch.triangles.Add(first);
            batch.triangles.Add(first + 1);
            batch.triangles.Add(first + 2);
            batch.triangles.Add(first);
            batch.triangles.Add(first + 2);
            batch.triangles.Add(first + 3);
        }

        MeshBatch GetBatch(Texture2D texture, int x, int y, int order) {
            int      chunk = y / _chunkSize * _chunksX + x / _chunkSize;
            BatchKey key   = new(texture, chunk, order);

            if (_batches.TryGetValue(key, out MeshBatch batch)) return batch;
            batch = new MeshBatch(texture, order);
            _batches.Add(key, batch);

            return batch;
        }

        void BuildMeshes() {
            Shader fallback = null;
            if (!_material) {
                fallback = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
                if (!fallback) {
                    fallback = Shader.Find("Sprites/Default");
                }

                if (!fallback) {
                    Debug.LogError("No sprite shader found. Assign a mesh material.");
                    return;
                }
            }

            var materials = new Dictionary<Texture2D, Material>();

            foreach (MeshBatch batch in _batches.Values.Where(batch => batch.vertices.Count != 0)) {
                if (!materials.TryGetValue(batch.texture, out Material material)) {
                    material = _material ? new Material(_material) : new Material(fallback);

                    material.name        = $"{batch.texture.name} (Terrain)";
                    material.mainTexture = batch.texture;

                    materials.Add(batch.texture, material);
                    _materials.Add(material);
                }

                var mesh = new Mesh {
                    name = $"Terrain Chunk ({batch.order})"
                };

                if (batch.vertices.Count > 65535) {
                    mesh.indexFormat = IndexFormat.UInt32;
                }

                mesh.SetVertices(batch.vertices);
                mesh.SetUVs(0, batch.uvs);
                mesh.SetColors(batch.colors);
                mesh.SetTriangles(batch.triangles, 0, false);
                mesh.RecalculateBounds();
                _meshes.Add(mesh);

                var go = new GameObject($"Chunk - Order {batch.order}");
                go.transform.SetParent(_ctx.root, false);

                go.AddComponent<MeshFilter>().sharedMesh = mesh;

                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial       = material;
                renderer.sortingOrder         = batch.order;
                renderer.shadowCastingMode    = ShadowCastingMode.Off;
                renderer.receiveShadows       = false;
                renderer.lightProbeUsage      = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
        }

        public void Dispose() {
            foreach (Mesh mesh in _meshes) {
                DestroyObject(mesh);
            }

            _meshes.Clear();

            foreach (Material material in _materials) {
                DestroyObject(material);
            }

            _materials.Clear();
        }

        static void DestroyObject(UnityEngine.Object obj) {
            if (Application.isPlaying) {
                UnityEngine.Object.Destroy(obj);
            } else {
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        readonly struct BatchKey : IEquatable<BatchKey> {
            public readonly Texture2D texture;
            public readonly int chunk, order;

            public BatchKey(Texture2D texture, int chunk, int order) {
                this.texture = texture;
                this.chunk   = chunk;
                this.order   = order;
            }

            public bool Equals(BatchKey other) => texture == other.texture && chunk == other.chunk && order == other.order;

            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);

            // https://stackoverflow.com/questions/102742/why-is-397-used-for-resharper-gethashcode-override/102878
            public override int GetHashCode() {
                unchecked {
                    return ((((texture ? texture.GetInstanceID() : 0) * 397) ^ chunk) * 397) ^ order;
                }
            }
        }

        sealed class MeshBatch {
            public readonly Texture2D texture;
            public readonly int order;

            public readonly List<Vector3> vertices = new();
            public readonly List<Vector2> uvs = new();
            public readonly List<Color> colors = new();
            public readonly List<int> triangles = new();

            public MeshBatch(Texture2D texture, int order) {
                this.texture = texture;
                this.order   = order;
            }
        }
    }
}