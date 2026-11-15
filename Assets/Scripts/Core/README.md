# Core

The core takes a grid size and a set of tile rules, and hands back a grid where
every rule is true. It has no concept of sprites, it is just a constraint solver.
Compared to a standard WFC implementation, it is able to reason about all eight
neighbours, it can check a whole 3x3 neighbourhoods (Window Constraints).

## Data Model

**Domanins** Every cell keeps a list of the tiles it could still become,
stored as a `ulong`. If bit _t_ is set, tile _t_ is still possible.
One 64-bit number per cell means a cell's whole state fits in a register and
checks/removals are very cheap. The cost is that there is a hard
limit of 64 tiles. If we need more tiles, then we'd need to allocate more ulongs
per cell, or split up solving into chunks.

**Directions** There are eight: N, NE, E, SE, S, SW, W, NW, numbered 0 to 7
to match Wangtiles configuration. +y acts as north. Direction _d_ owns bit
`1 << d` of an 8-bit mask, ~d is `(d + 4) & 7`. Directions is standardised
to allow for consistency across the various subsystems: adjacency rules,
window checks and renderer when it picks overlay sprites. One numbering means
no transition step between the solver and the renderer.

**The support table** For every tile _t_ and direction _d_, we store the set of
tiles that may sit in direction _d_ from _t_. Propagating is then trivial: take
everything a cell could still be, combine the allowed sets of all those tiles,
and intersect the result with the neighbour's domain. The table is symmetric;
_b_ is allowed east of _a_ only when _a_ is allowed west of _b_. The builder
enforces this invariant and `WFCRules.Validate()` checks it.

**Adjacency Rules** Authors never write this table directly, they only have to write
rules. The builder compiles Terrain Definitions into rules based on...

- **AllowCardinal(a, b)**: _a_ and _b_ may touch across N, E, S or W. Every tile
  may always touch itself. In the terrain layer this is the `canNeighbor` list.
- **AllowDiagonal(a, b)**: Same concept but for the corners. It is only considered
  if diagonal rules are enabled for the terrain set. In the terrain layer, these
  constraints populate `canNeighbor`.
- **Restrict(tile, direction, allowedMask, mustExist)**: The neighbour of `tile`
  in that direction must come from the allowed set. This is where fine semantic
  rules can be enforced, such as "a door can only neighbour a road to its south."
- **mustExist**: The tile may or may not sit on a border cell where that neighbour
  would be off the map. A door that needs road to the south can't be on the south
  edge. The solver applies this to the starting domains. (Consider cutting)

Everything is compiled into one table, because this is how basic WFC works, and
we want fast lookup. Pairwise and Window Constrains all flatten into the same bitsets.
Any new kind of rule is a new step in the compiler, not a change to the solver.

## Window Constraints

Pairwise rules look at two cells at a time, but sometimes we need more fine controls.
So, for non-cardinal constraints, we use "Windows". We use this for overlay textures
and the fine tuned rules.
On overlays specifically, we may have reason to not draw art for certain combinations.
If we don't want to allow a small 1xn gap in a "biome", then we need a way to enforce
that. If we have water to the north and south of a given tile, while this is a legal
pair, we don't want to permit it, and if there's no art drawn for that combination,
it cannot be rendered. No pairwise rule can express that, because the issue isn't
any one neighbour, it's a 3x3 combination.

So then, a window constraint is a constraint for a 3x3 block around a cells.
We represent the 8 neighbours as a bitwise mask, 0 to 255. This is the same mask
that the renderer uses to pick overlay sprites, so what the solver allows and what
it renderer needs are the same thing. Each overlay carries a list of which of the
256 patterns it can draw. Pattern 0, "no _tile_ nearby" is always drawable,
so a window can restrict what surrounds a tile but never make the tile impossible
by itself. Off-map neighbours count as "not _tile_".

While solving, a neighbour might for sure be water, certainly not water, or could
be either. So, for each centre cell we track two sets of neighbours: `mustMask`,
those that can only be water, and `mayMask`, those that could be water. The final
pattern `m` has to land somewhere between them, `must ⊆ m ⊆ may` holds true.
The centre can be a sandtile if at least one drawable pattern lies in that
range.

This would be very expensive, 256 checks per cell, everytime a neighbour changes
would destroy performance. So, instead we answer the question for evvery possible
`(must, may)` pair ahead of time and keep the answers in a table. The table index
puts `must` in the high byte and `may` in the low byte, one bit per pair. That is
65,536 bits, or 8KB, per window. This moves the thinking to compile time and leaves
the solver with a single bit lookup. It also keeps visual concerns out of the solver,
since the table is just binary.

So, what does this check actually do?

- **Prune the centre**: If no drawable pattern is reachable, whatever the undecided
  neighbours turn out to be, we remove the overlay's target tiles from the centre.
- **Prune the neighbours**: Once the centre is decided, for each undecided neighbour
  we examine two cases: "What if this is X, and what if it isn't" If one answer
  would make the pattern undrawable, the other must be true, and we set it.
  If both would, we've hit a contradiction.

This enforces that for any finished grid, every target cell's neighbourhood is a
pattern that can be rendered. Currently, it is only used for enforcing the constraints
of overlays.

This could be extended to rules beyond overlays, to make the rules system stronger.

## Work Queue

The work queue is a ringed buffer that keeps track of what cells need to be updated.
When the domain of a cell is reduced, its neighbours must also be constrained.
When the list is empty, the news has stopped traveling and everyone is on the
same page.

There are two kinds of to-do:

- **Adjacency**: This cell changed, so push what it allows onto its eight neighbours.
- **Window**: Something changed in this cell's 3x3 neighbourhood, so re-check
  the rules centred on it.

When a cell changes, it goes on the list as an adjacency item. If the tile set has
any windows, the nine cells around it (itself included) all go on as window items.

### Implementation Details

For more gnarly details... Each item is one integer.
`item = length(kind) * cell + kind`, where kind is 0 or 1 for adjacency or window.
The list is a circular buffer in a fixed-size array. We add at the tail,
read from the head, and wrap around to avoid allocations. A flag `(cell, kind)`
records whether it is already on the list, so a cell that changes five times before
we reach it, is only processed once. This also caps the list at two items per cell,
which is why a fixed array is big enough. There's only one queue because it makes
burst happy.

Of course, the data layout is based on the assumption of only two constraint types.
Adding a new constraint type would require adding a flag array and write a handler.

## Restarts and Speed

Lastly, there is no special backtracking method for if the solver hits a failure
state. We simply restart at the point where the starting domains were propagated.
The cost is that tightly constrained tilesets will need many attempts to solve.

## Extensions

There are a few areas this could be improved.

- Tile counts: More than 64 tiles would be good.
- Directions: Support for other grids would be neat
- Constraint Kinds: Streamline the process for adding new constraint kinds.
- Failure: More sophisticated failure handling, either backtracking or modifying
  in blocks.
- Starting state: Reserved areas, pinned tiles
- Weights: Any per-cell bias, such as a designer brush.
