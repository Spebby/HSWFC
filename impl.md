# HSWFC Implementation

## 1. Purpose and Scope

This document outlines an implementation of Hierarchical Semantic Wave Function Collapse (HSWFC), based as closely as practical on the original paper by Shaad Alaka and Rafael Bidarra.

The objective is to reproduce the paper's hierarchical representation, adjacency inference, and generation behavior while providing an editor suitable for authoring and inspecting more complicated tilesets.

The paper does not specify every detail of the underlying data structures, serialization format, or editor workflow. Where implementation choices are necessary, they should support the paper's model without introducing new semantics into the core algorithm.

The initial implementation will focus on the simple tiled model. Automatic training, semantic hierarchy discovery, overlapping-pattern generation, and multilayer terrain are outside the immediate scope.

## 2. Core Concepts

### 2.1 Concrete Tiles

A concrete tile is a leaf in the semantic hierarchy.

It represents a specific tile choice available to the solver and has a defined set of visual assets, adjacency relationships, and any other required tile metadata.

Concrete tiles are the final choices produced by collapse. They should have stable identities independent of their display names or image filenames.

### 2.2 Meta-Tiles

A meta-tile represents a semantic category that can be refined into more specific tiles or categories.

For example:

```
Terrain
├── Land
│   ├── Grass
│   └── Sand
└── Water
    ├── ShallowWater
    └── DeepWater
```

This is an illustrative hierarchy.

Meta-tiles allow environment designers to specify higher-level intentions without selecting every concrete tile manually.

A meta-tile may have other meta-tiles or concrete tiles as children.

### 2.3 Hierarchy and DAG Structure

The semantic hierarchy is a directed acyclic graph (DAG), rather than necessarily a strict tree.

A node may have multiple parents if it participates in more than one semantic category.

For example, a concrete grass tile might belong to both a Meadow meta-tile and a ForestFloor meta-tile.

The implementation must support shared descendants without duplicating the concrete tile itself.

Cycles must be prevented because they would make refinement ambiguous or non-terminating.

Each parent-child edge carries a weight used by the HSWFC model to represent the relative contribution of a child to its parent.

The precise interpretation and normalization of these weights should follow the paper's definitions. They should not be replaced with arbitrary probability rules without checking the original algorithm.

### 2.4 Adjacency Constraints

Adjacency constraints describe which nodes may appear next to one another in a given direction.

For a four-directional grid, the relevant directions are north, east, south, and west.

Constraints can be authored between concrete tiles, meta-tiles, or combinations of these, according to the paper's model.

The distinction between authored constraints and inferred constraints should be maintained internally. The solver needs effective compatibility information, but the editor should preserve the original semantic rules so they can be inspected and edited.

### 2.5 Environment Design

The environment designer places tiles or meta-tiles into the initial grid.

Concrete tiles represent fully specified choices. Meta-tiles represent unresolved semantic choices that can be refined during generation.

The system must preserve enough information to distinguish a designer's original semantic selection from the final concrete tile selected during collapse.

## 3. Adjacency Inference

One of HSWFC's central ideas is that adjacency relationships can be expressed at higher levels of the hierarchy instead of being authored individually for every concrete tile pair.

For example, an authored relationship between Land and Water can induce compatibility relationships between their descendants.

If Land contains Grass and Sand, while Water contains ShallowWater and DeepWater, the high-level relationship can imply relevant compatibility between the descendant groups.

The implementation should derive the effective adjacency constraints from the hierarchy and the authored rules.

This inference must follow the original paper's algorithm, including its handling of descendant relationships and constraints that involve nodes at different hierarchy levels.

It should not be replaced with an unrelated rule such as treating every pair of descendants as compatible in all circumstances without checking the paper's exact definitions.

### 3.1 Directionality

Adjacency must be associated with a direction.

A relationship between two categories in the east-west direction is not automatically equivalent to every possible arrangement involving those categories.

Directional constraints are particularly important for transition tiles, edges, corners, and other tiles whose visual appearance depends on their neighbors.

The implementation should use the paper's directional convention consistently throughout constraint inference and propagation.

### 3.2 Inferred Versus Explicit Constraints

A useful internal distinction is:

Authored constraints: rules explicitly supplied by the designer.
Inferred constraints: rules derived from the hierarchy and authored relationships.
Effective constraints: the compatibility information used by the solver after applying the inference procedure.

The editor should provide a way to inspect the effective constraints without requiring the designer to edit them as if they had been authored individually.

This makes it easier to debug unintended combinations in large tilesets.

### 3.3 Transition Tiles

Directional transition tiles should be represented as concrete leaf tiles.

For example, a SandGrassTransition meta-tile may contain several concrete transition variants, each with its own orientation and adjacency behavior.

The hierarchy groups these variants for authoring. Their concrete adjacency constraints enforce the required geometric relationships.

A broad relationship between Sand and SandGrassTransition, or between Grass and SandGrassTransition, does not by itself guarantee that a transition appears only between sand and grass.

If the tile must have sand on one side and grass on the other, its concrete directional constraints must enforce that requirement.

## 4. Collapse and Propagation

The implementation should preserve the ordinary WFC constraint-solving process while extending it to hierarchical choices.

At a high level, generation involves:

Initializing the grid from the designer's input.
Determining the possible choices at each cell.
Selecting an unresolved choice according to the algorithm's selection strategy.
Refining meta-tile choices into children when required.
Propagating the resulting constraints to neighboring cells.
Continuing until the grid is fully resolved or a contradiction is encountered.
Applying the paper's handling of contradictions and any supported recovery mechanism.

This is an outline, not a replacement for the paper's algorithm. The implementation should reproduce the specific refinement, weighting, propagation, and contradiction behavior described in the source.

The hierarchy should not be treated as merely a preprocessing step that expands every meta-tile into a flat set of leaf tiles before generation. Doing so risks losing the hierarchical choices and interaction behavior that HSWFC is intended to support.

## 5. Collapse-Path Tracking

Once a meta-tile has been refined into a concrete leaf tile, the original semantic choice may no longer be recoverable from the final tilemap.

For example, a concrete Grass tile could have been selected through a Meadow meta-tile, a ForestFloor meta-tile, or a more general Terrain meta-tile.

The final concrete tile alone does not identify which path was taken.

The implementation should preserve the collapse path or equivalent metadata where required by the paper's interaction model.

This history should be associated with the relevant cell and updated as refinement proceeds.

It should not be assumed that semantic information can be reconstructed reliably from the final visual output.

Preserving the history also allows the editor to display the original semantic intent, inspect generation decisions, and continue working with partially refined environments.

## 6. Authoring Workflow

The editor should support two distinct roles: defining the input model and designing an environment using that model.

### 6.1 Input Model Authoring

The input designer should be able to:

Create or import concrete tiles.
Create meta-tiles.
Connect parent and child nodes.
Set child weights.
Define directional adjacency constraints.
Inspect inferred compatibility relationships.
Validate the hierarchy and constraints.

The editor should prevent cycles in the DAG and make shared descendants explicit.

It should also provide enough information to identify invalid or contradictory model definitions before generation begins.

### 6.2 Environment Authoring

The environment designer should be able to:

Select a concrete tile or meta-tile.
Place it in the grid.
Generate or refine the surrounding environment.
Inspect the concrete choices and their semantic ancestry.
Preserve or restore semantic placement information as supported by the implementation.

Meta-tiles should remain meaningful authoring tools even when their descendants include many concrete variants.

For example, directional grass variants can be grouped under one Grass meta-tile so that the designer does not have to select individual orientations manually.

### 6.3 Composite Tile Rendering

Composite rendering may be supported as a separate presentation feature.

A concrete tile can reference multiple visual components, such as a ground sprite and a flower overlay, while retaining one logical identity for the solver.

This does not change the HSWFC hierarchy or adjacency inference.

If an object needs to be independently placed or constrained, it may require a separate logical representation. That behavior should not be assumed to follow automatically from the DAG.

See composite-tiles-and-directional-variants.md for the proposed representation and authoring workflow.

## 7. Data Representation

The paper specifies the conceptual model but does not mandate a particular serialization format or set of programming-language data structures.

The implementation should keep the following information logically distinct:

Concrete tile definitions.
Meta-tile definitions.
Parent-child edges and weights.
Authored adjacency constraints.
Inferred or effective adjacency information.
Environment grid state.
Collapse-path metadata.
Rendering metadata.

Stable node identifiers should be used instead of relying on display names or asset paths.

The DAG should be represented in a way that supports efficient descendant traversal, parent lookup, cycle detection, and weight access.

The adjacency representation should support directional lookup and efficient constraint propagation.

Derived data such as effective adjacency relationships may be cached, provided the cache can be invalidated when the hierarchy, weights, or authored constraints change.

The serialized model should preserve authored information rather than storing only a fully expanded representation that loses the original semantic relationships.

## 8. Validation and Testing

A faithful implementation should include tests for both the data model and the generation behavior.

### 8.1 Hierarchy Tests

A simple tree-shaped hierarchy can be created.
A node can have multiple parents.
Cycles are rejected.
Child weights are stored and interpreted correctly.
Shared descendants are handled without duplicating node identity.

### 8.2 Adjacency Tests

Concrete directional constraints are respected.
Meta-tile relationships induce the expected descendant compatibility.
Constraints involving nodes at different hierarchy levels behave as specified by the paper.
Transition tiles cannot appear in configurations prohibited by their directional constraints.
Changes to the hierarchy update any derived compatibility information.

### 8.3 Generation Tests

Fully specified grids remain valid.
Unresolved meta-tiles can be refined.
Constraint propagation removes incompatible choices.
Contradictions are detected and handled according to the implemented algorithm.
Collapse paths preserve the semantic ancestry needed by the editor.
Results remain valid after saving and reloading the model.

Tests should distinguish between an invalid result, an unsatisfiable input, and a valid but unexpected visual arrangement. These are different failure modes and should not be conflated.

## 9. Implementation Scope

The initial implementation should prioritize the central HSWFC model:

DAG-based semantic hierarchy.
Weighted parent-child relationships.
Hierarchical adjacency inference.
Hierarchical collapse and constraint propagation.
Collapse-path tracking.
Input-model and environment authoring.

Automatic training of a flat WFC model is not required for this scope. The initial model can be authored manually, which also avoids the separate and underspecified problem of discovering meaningful semantic groupings from a completed tileset.

Directional variants and composite rendering can be supported without changing the core solver, provided their logical identities and adjacency behavior are represented explicitly.

Overlapping-pattern WFC and multilayer terrain should remain out of scope for the initial implementation.

Multilayer terrain may be investigated later by examining layered tilemap representations, sprite footprints, depth sorting, and cross-layer constraints. These are substantial extensions and should not delay a faithful implementation of the paper's central hierarchical model.

## 10. Reference and Implementation Discipline

The original paper should remain the authority for algorithmic behavior.

Where the paper specifies an algorithm or semantic relationship, the implementation should follow it rather than substitute a more convenient alternative.

Where the paper leaves a detail unspecified, the implementation should document the chosen behavior and keep it separate from the paper's stated contributions.

In particular, distinguish between:

Behavior explicitly described by HSWFC.
Data structures and editor interactions needed to implement that behavior.
Additional presentation or workflow features that do not alter the core model.

This distinction will make the implementation easier to validate and help prevent useful editor extensions from accidentally changing the underlying algorithm.
