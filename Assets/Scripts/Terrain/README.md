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

## Semantic terrain generation

Open `Assets/Scenes/Demo.unity` and enter Play mode to try the hierarchy. Its
generator uses `HierarchicalTerrainSet`, which references `DemoSemanticGraph`.
Land refines to Grass or Dirt, and Shore refines to Dirt or Water. Both edges to
Dirt reference the same leaf node. The generator uses **Semantic Regions** to
start rectangular zones at Land and Water and **Semantic Placements** to
start one cell at Shore. Other cells start at Root.

Each region's **Origin** is its bottom-left cell and **Size** is its width and
height in cells. Set **Node Id** to the graph node to place. Regions may touch
or overlap when they name the same node. Assigning two different nodes to one
cell reports a conflict. For a solid ocean interior, start the region at the
Water leaf. Start land at the Land meta-tile to let it refine into Grass or Dirt.

To author a graph, create a **Terrain → Semantic Graph** asset and assign it to
a `TerrainSet`. Node IDs are the positions in the asset's **Nodes** list. Set
the root index and enter each meta-tile's children and relative edge weights.
Each leaf references a `TerrainDef` in the set, so reordering the set's terrain
list does not change those references. Reordering the graph's node list does
require updating its node IDs, edges, rules, and placements.

A semantic adjacency gives a source node, a direction toward its neighbor,
and a neighbor node. The inverse direction is inferred. Adding a rule for a
direction makes the semantic rules the allowed pairs for that direction.
Explicit directional terrain restrictions still apply. The demo permits
cardinal neighbors within each group. Dirt can touch both Grass and Water, so
one Dirt cell can bridge the two. Grass and Water cannot touch directly. The
demo uses ordinary terrain rules for the diagonals. Grass and Water each
have four times Dirt's weight under their respective parent.

In code, `WFCTerrainGenerator.GetSemanticPath(x, y)` returns the path taken in
a successfully generated cell, beginning at its authored starting node or at
Root. `TerrainAt(x, y)` still returns its concrete terrain ID. A `TerrainSet`
with an empty graph field, such as the original `TerrainSet` demo asset, runs
flat WFC when both semantic placement lists are empty.

In Play mode, select the generator and set **Display → View** to **Semantic
Paths**. Use **Previous** and **Next** in the Game view to inspect the recorded
paths by step. Step zero shows each cell's starting node, including any authored
placements. Advancing moves each cell one edge along its path, and cells that
reach a leaf keep their final color. The controls redraw the solved map by path
depth. The solver's chronological observation and propagation order is separate.

To customize the palette, open the semantic graph asset, expand a node, and
enable **Override Preview Color**. **Preview Color** then sets both the map
color and legend swatch in **Semantic Paths**.
