Composite Tiles and Directional Variants
Purpose

This document describes how to represent directional tile variants and composite tiles in the HSWFC editor without unnecessarily duplicating visual assets or complicating the core solver.

The goal is to distinguish between a tile's semantic identity, its concrete variant, and how it is rendered. These concepts overlap in a traditional tile editor, but treating them separately makes the editor easier to extend and maintain.

Directional variants and composite tiles are related but distinct features:

Directional variants represent different concrete tiles that serve the same semantic purpose but have different orientations or boundary conditions.
Composite tiles combine multiple visual assets into one rendered tile or logical cell.

Neither feature should require changes to the fundamental HSWFC collapse and propagation algorithms, provided each concrete tile has well-defined adjacency behavior.

1. Directional Variants
1.1 Motivation

Many tilesets contain groups of visually related tiles that should be treated as a single logical choice during authoring.

For example, a grass terrain may have several variants:

Grass with a northern sand boundary.
Grass with an eastern sand boundary.
Grass with a southern sand boundary.
Grass with a western sand boundary.
Grass without a boundary.

These are distinct concrete tiles because they have different visual appearances and may have different adjacency constraints. However, an environment designer should not necessarily have to select each variant manually.

The editor should allow the designer to select a logical Grass tile and let WFC choose an appropriate concrete variant during generation.

1.2 Representation in HSWFC

A meta-tile can group the directional variants under a shared semantic identity.

For example:

Grass
├── Grass_Default
├── Grass_North
├── Grass_East
├── Grass_South
└── Grass_West


The edges represent the existing HSWFC consists-of relationship. The child edges may have weights, as required by the HSWFC model.

The Grass meta-tile can be used during authoring, while the concrete child tiles remain the actual choices available to the solver after refinement.

The hierarchy does not, by itself, determine which directional variant is appropriate in a particular location. That behavior comes from the adjacency constraints.

For example, if Grass_North represents grass with sand along its northern edge, its directional adjacency rules should allow the appropriate neighboring sand tile to the north and constrain incompatible neighbors as necessary.

The authoring interface should present Grass as one selectable item while retaining the concrete variants internally.

1.3 Directional Constraints

Each concrete variant must have explicit or inferred adjacency relationships for each relevant direction.

For a four-directional grid, these are north, east, south, and west.

The implementation should not assume that a variant's name or orientation automatically determines its compatibility. The directional meaning of a variant is descriptive metadata; the actual behavior must be represented by adjacency constraints.

For example, a sand-to-grass boundary tile may be compatible with sand on its western side and grass on its eastern side. Other directions must also have appropriate constraints so that the tile cannot appear in invalid configurations.

Meta-tile adjacency inference can establish broad compatibility between semantic groups, but broad compatibility alone may be insufficient to enforce the precise geometry of a boundary.

When a particular arrangement must be guaranteed, the concrete leaf-level constraints must enforce it.

1.4 Weights and Variant Selection

Child weights determine the relative selection preferences within a meta-tile, according to the HSWFC implementation.

Weights can be used to make a default variant more common than a boundary variant, for example.

However, weights are not guarantees of final frequency. Adjacency constraints and propagation determine which variants remain possible at a given location.

The editor should expose the weights without requiring environment designers to select concrete variants manually.

2. Composite Tiles
2.1 Motivation

Some visual tiles are composed of multiple assets.

For example, a cell might contain grass with a flower on top, or a dirt surface with a small rock decoration. Creating a separate image for every possible combination would duplicate assets and make tileset maintenance unnecessarily difficult.

A composite tile allows the renderer to combine multiple visual components while preserving a defined logical identity for the solver.

A composite tile is not automatically an HSWFC meta-tile. A meta-tile represents a semantic abstraction that can be refined into children. A composite tile describes how one concrete choice is rendered.

These concepts should remain separate.

2.2 Composite Leaf Representation

A concrete leaf tile may contain one or more visual components.

For example:

GrassWithFlower
├── Grass sprite
└── Flower sprite


The renderer draws both components in the same logical cell.

The solver can continue to treat GrassWithFlower as one concrete tile with its own identity, weight, and adjacency constraints.

The same grass sprite can be reused by other composites, such as GrassWithRock, without duplicating the underlying image asset.

A possible data representation is:

{
  "id": "grass_with_flower",
  "components": [
    {
      "asset": "grass.png",
      "offsetX": 0,
      "offsetY": 0,
      "renderOrder": 0
    },
    {
      "asset": "flower.png",
      "offsetX": 0,
      "offsetY": 0,
      "renderOrder": 1
    }
  ]
}


This is an illustrative representation, not a format specified by the HSWFC paper. The actual schema should follow the project's existing conventions.

2.3 Composite Tiles and Adjacency

The solver must have a clear understanding of the identity of every composite tile.

If GrassWithFlower and GrassWithoutFlower are separate concrete choices, the solver can distinguish them and apply different constraints where needed.

The renderer should not silently add arbitrary objects to a tile after solving if those objects affect compatibility or placement validity. Otherwise, the solver may generate a valid tilemap that becomes invalid after rendering or decoration.

There are two reasonable approaches:

Represent each relevant composite as a concrete leaf tile in the HSWFC model.
Keep independently placed decorations outside the WFC tile model, provided their placement does not need to participate in the solver's constraints.

The first approach is appropriate when the decoration affects adjacency or generation. The second is appropriate for independent decorative overlays that do not affect terrain compatibility.

2.4 Avoiding Combinatorial Growth

Composite rendering avoids duplicating image assets, but it does not automatically eliminate the number of logical combinations.

If the solver must independently choose between ten terrain types and ten decorations, representing every combination as a separate leaf could require up to one hundred concrete choices.

For a small editor, explicit composites may be sufficient. If combinations become numerous, independently placeable objects or a separate decoration pass may be more appropriate.

The latter is an extension to the generation model and should not be confused with the core HSWFC implementation.

3. Editor Workflow

The authoring interface should distinguish between the logical tile shown to the environment designer and the concrete choices available to the solver.

A suggested workflow is:

Import or create concrete visual tiles.
Group related directional variants under a meta-tile.
Define child weights and adjacency constraints.
Optionally create composite leaf tiles that reference multiple visual assets.
Use the meta-tile as a single brush when authoring the environment.
Allow HSWFC to refine the selected meta-tile into concrete leaf tiles.
Preserve the collapse history if the semantic selections must remain inspectable after generation.

The interface should make it possible to inspect a meta-tile's descendants and understand which concrete variants may be selected.

It should also make inherited adjacency relationships inspectable. If a variant is unexpectedly allowed or excluded, the designer should be able to trace that result back to the relevant authored constraints.

4. Scope and Design Principles

The following principles should guide implementation:

Keep the HSWFC hierarchy and adjacency model independent of rendering details.
Use meta-tiles to group semantic alternatives, including directional variants.
Use concrete leaf identities to represent choices that have distinct adjacency behavior.
Allow concrete tiles to reference reusable visual assets.
Treat composite rendering as a property of concrete tile presentation, not as a replacement for the HSWFC hierarchy.
Avoid introducing independent object placement or multilayer generation unless it becomes a necessary requirement.

The immediate goal is to support directional variants and composite rendering while keeping the solver faithful to the HSWFC model.

Multilayer terrain, tall sprites, and depth-aware rendering are possible future extensions, but they require additional spatial and rendering considerations and are outside the current implementation scope.
