# Terrain Rendering

The terrain rendering system separates terrain generation from presentation through
interchangeable rendering backends. They operate on the same solved terrain layout
and render context, allowing the underlying WFC solver and terrain data to remain
independent.

Two rendering strategies are available: **Chunk Mesh** and **Tilemap**.
Chunk Mesh combines terrain sprites into meshes grouped by texture, sorting order
and spatial chunk, reducing draw-call overhead. Tilemap uses Unity's built-in
Tilemap Renderer, providing better integration with Unity's tilemap tooling,
at additional rendering cost.

The active strategy can be selected through `TerrainRenderStrategy` on
`WFCTerrainGenerator`. The mesh rendeer also exposes a configurable chunk size,
with automatic sizing available when set to`-1`.

**Tilemap** is simpler to work with, and perfectly adequate for normal sized output.
Unless you are pushing the generated size to beyond `256x256`, it should run perfectly
fine on most hardware. Past this point, **Chunk Mesh** would be preferred for raw
performance. Though, I would question why you'd want output that large.
