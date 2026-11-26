# Hierarchical Semantic Wave Function Collapse

Unity implementation of Shaad Alaka and Rafael Bidarra's 2023 FDG paper.
The paper's details can be found at: <https://doi.org/10.1145/3582437.3587209>

Open `Assets/Scenes/Demo.unity` and enter Play mode to generate a map with
the semantic DAG. The demo sketches **Land** and **Water** zones and lets
the solver refine the band along the coast. Land and Shore can both produce
Dirt, and the solver retains the branch each cell followed.

- [Author a graph and place starting meta-tiles](Assets/Scripts/Terrain/README.md#semantic-terrain-generation)
- [Read how graph compilation and propagation work](Assets/Scripts/Core/README.md#semantic-dag-solving)

## Attributions

None, so far!
