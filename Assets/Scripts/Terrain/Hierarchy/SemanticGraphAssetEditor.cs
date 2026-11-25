#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WFC.Core;
// ReSharper disable HeapView.ObjectAllocation
// ReSharper disable HeapView.BoxingAllocation
// ReSharper disable HeapView.ImplicitCapture
// ReSharper disable HeapView.ClosureAllocation


// AI generated
namespace WFC.Terrain {
    /// <summary>
    /// Edits to a <see cref="SemanticGraphAsset"/> that keep its node IDs consistent. The asset stores nodes by list
    /// index, so every operation that removes a node also remaps the IDs that refer to the nodes after it.
    /// </summary>
    static class SemanticGraphEditing {
        public static readonly string[] DirectionNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        public const int CARDINALS = (1 << 0) | (1 << 2) | (1 << 4) | (1 << 6);
        public const int DIAGONALS = (1 << 1) | (1 << 3) | (1 << 5) | (1 << 7);
        public const int ALL_DIRECTIONS = 0xFF;

        public readonly struct Issue {
            public readonly bool isError;
            public readonly string message;

            public Issue(bool isError, string message) {
                this.isError = isError;
                this.message = message;
            }
        }

        public sealed class Report {
            public readonly List<Issue> issues = new();
            public int routeCount = -1;
        }

        public struct Rule {
            public int source, neighbor, mask;
        }

        #region Nodes
        public static bool IsValid(SemanticGraphAsset g, int id) =>
            id >= 0 && id < g.nodes.Count && g.nodes[id] != null;

        /// <summary>A node's name, with its ID appended only when another node shares the name.</summary>
        public static string Label(SemanticGraphAsset g, int id) {
            if (!IsValid(g, id)) return $"(missing node {id})";

            string name = g.nodes[id].name;
            if (string.IsNullOrWhiteSpace(name)) return $"Node {id}";

            return g.nodes.Where((t, i) => i != id && t != null && t.name == name).Any() ? $"{name} ({id})" : name;
        }

        public static string[] Labels(SemanticGraphAsset g) {
            string[] labels                                   = new string[g.nodes.Count];
            for (int i = 0; i < labels.Length; i++) labels[i] = Label(g, i);
            return labels;
        }

        public static HashSet<int> Descendants(SemanticGraphAsset g, int id, bool includeSelf) {
            HashSet<int> found = new();
            Stack<int>   stack = new();
            stack.Push(id);
            while (stack.Count > 0) {
                int current = stack.Pop();
                if (!IsValid(g, current) || !found.Add(current)) continue;

                foreach (SemanticGraphAsset.Child child in g.nodes[current].children) stack.Push(child.nodeId);
            }

            if (!includeSelf) {
                found.Remove(id);
            }

            return found;
        }

        public static List<int> Parents(SemanticGraphAsset g, int id) {
            List<int> parents = new();
            for (int i = 0; i < g.nodes.Count; i++) {
                if (g.nodes[i] != null && g.nodes[i].children.Any(child => child.nodeId == id)) {
                    parents.Add(i);
                }
            }

            return parents;
        }

        public static bool CanLink(SemanticGraphAsset g, int parent, int child) {
            if (!IsValid(g, parent) || !IsValid(g, child) || parent == child) return false;
            if (g.nodes[parent].children.Any(existing => existing.nodeId == child)) return false;

            // A link closes a cycle exactly when the child can already reach the parent.
            return !Descendants(g, child, true).Contains(parent);
        }

        public static int AddNode(SemanticGraphAsset g, string name, TerrainDef terrain = null) {
            g.nodes.Add(new SemanticGraphAsset.Node { name = name, terrain = terrain });
            return g.nodes.Count - 1;
        }

        public static string UniqueName(SemanticGraphAsset g, string baseName) {
            string name                                                                       = baseName;
            for (int n = 2; g.nodes.Any(node => node != null && node.name == name); n++) name = $"{baseName} {n}";
            return name;
        }

        /// <summary>Links a child under a parent. A leaf that gains a child becomes a meta-tile and drops its terrain.</summary>
        public static bool Link(SemanticGraphAsset g, int parent, int child, float weight = 1f) {
            if (!CanLink(g, parent, child)) return false;

            SemanticGraphAsset.Node node = g.nodes[parent];
            node.terrain = null;
            node.children.Add(new SemanticGraphAsset.Child { nodeId = child, weight = weight });
            return true;
        }

        /// <summary>Removes one parent-to-child link. The child stays in the graph, unattached if nothing else links to it.</summary>
        public static void Unlink(SemanticGraphAsset g, int parent, int edge) {
            if (IsValid(g, parent) && edge >= 0 && edge < g.nodes[parent].children.Count) {
                g.nodes[parent].children.RemoveAt(edge);
            }
        }

        /// <summary>Removes a node and every link and rule that mentions it. The root cannot be deleted.</summary>
        public static bool DeleteNode(SemanticGraphAsset g, int id) {
            if (!IsValid(g, id) || id == g.rootId) return false;

            Remap(g, old => old == id ? -1 : old > id ? old - 1 : old);
            g.nodes.RemoveAt(id);
            return true;
        }

        /// <summary>Rewrites every stored node ID. A map result below zero drops the link or rule that held the ID.</summary>
        public static void Remap(SemanticGraphAsset g, Func<int, int> map) {
            int root = map(g.rootId);
            g.rootId = root < 0 ? 0 : root;

            foreach (SemanticGraphAsset.Node node in g.nodes.Where(node => node != null)) {
                node.children.RemoveAll(child => map(child.nodeId) < 0);
                foreach (SemanticGraphAsset.Child child in node.children) child.nodeId = map(child.nodeId);
            }

            List<SemanticGraphAsset.Adjacency> rules = new();
            foreach (SemanticGraphAsset.Adjacency rule in g.adjacencies) {
                int source = map(rule.sourceId), neighbor = map(rule.neighborId);
                if (source < 0 || neighbor < 0) continue;

                rules.Add(new SemanticGraphAsset.Adjacency { sourceId = source, direction = rule.direction, neighborId = neighbor });
            }

            g.adjacencies = rules;
        }
        #endregion

        #region Adjacency
        /// <summary>
        /// The flat adjacency list as one rule per source and neighbour pair, each with the set of directions it covers
        /// (bit d is direction d). Sorted by source, then neighbour.
        /// </summary>
        public static List<Rule> Rules(SemanticGraphAsset g) {
            Dictionary<(int, int), int> masks = new();
            foreach (SemanticGraphAsset.Adjacency entry in g.adjacencies) {
                (int sourceId, int neighborId) key = (entry.sourceId, entry.neighborId);
                masks.TryGetValue(key, out int mask);
                masks[key] = mask | (1 << (int)entry.direction);
            }

            return masks
                  .Select(pair => new Rule { source = pair.Key.Item1, neighbor = pair.Key.Item2, mask = pair.Value })
                  .OrderBy(rule => rule.source).ThenBy(rule => rule.neighbor).ToList();
        }

        public static int RuleMask(SemanticGraphAsset g, int source, int neighbor) => g.adjacencies
           .Where(entry => entry.sourceId == source && entry.neighborId == neighbor)
           .Aggregate(0, (current, entry) => current | (1 << (int)entry.direction));

        /// <summary>Makes a source and neighbour pair cover exactly the given directions. A zero mask removes the rule.</summary>
        public static void SetRule(SemanticGraphAsset g, int source, int neighbor, int mask) {
            g.adjacencies.RemoveAll(entry => entry.sourceId == source && entry.neighborId == neighbor);
            for (int d = 0; d < DirectionNames.Length; d++) {
                if ((mask & (1 << d)) == 0) continue;
                g.adjacencies.Add(new SemanticGraphAsset.Adjacency { sourceId = source, direction = (Direction)d, neighborId = neighbor });
            }
        }

        /// <summary>Adds directions to a pair, keeping any it already covers.</summary>
        public static void AddRule(SemanticGraphAsset g, int source, int neighbor, int mask) =>
            SetRule(g, source, neighbor, RuleMask(g, source, neighbor) | mask);

        /// <summary>Points a rule at a different neighbour, merging with any rule that pair already has.</summary>
        public static void ChangeRuleNeighbor(SemanticGraphAsset g, int source, int oldNeighbor, int newNeighbor) {
            if (oldNeighbor == newNeighbor) {
                return;
            }

            int mask = RuleMask(g, source, oldNeighbor);
            SetRule(g, source, oldNeighbor, 0);
            AddRule(g, source, newNeighbor, mask);
        }
        #endregion

        #region Validation
        /// <summary>Checks the graph the way the compiler will, with readable messages and the number of routes it produces.</summary>
        public static Report Validate(SemanticGraphAsset g) {
            Report report = new();
            if (g.nodes.Count == 0) {
                report.issues.Add(new Issue(true, "The graph has no nodes."));
                return report;
            }

            if (!IsValid(g, g.rootId)) {
                report.issues.Add(new Issue(true, "The root is not a valid node."));
                return report;
            }

            bool referencesValid = true;
            for (int i = 0; i < g.nodes.Count; i++) {
                if (g.nodes[i] == null) {
                    report.issues.Add(new Issue(true, $"Node {i} is missing."));
                    referencesValid = false;
                    continue;
                }

                foreach (SemanticGraphAsset.Child _ in g.nodes[i].children.Where(child => !IsValid(g, child.nodeId))) {
                    report.issues.Add(new Issue(true, $"{Label(g, i)} links to a node that no longer exists."));
                    referencesValid = false;
                }
            }

            if (g.adjacencies.Any(rule => !IsValid(g, rule.sourceId) || !IsValid(g, rule.neighborId))) {
                report.issues.Add(new Issue(true, "An adjacency rule refers to a node that no longer exists."));
            }

            if (!referencesValid) {
                return report;
            }

            for (int i = 0; i < g.nodes.Count; i++) {
                SemanticGraphAsset.Node node = g.nodes[i];
                bool                    leaf = node.children.Count == 0;
                switch (leaf) {
                    case true when !node.terrain:
                        report.issues.Add(new Issue(true, $"Leaf {Label(g, i)} has no terrain."));
                        break;
                    case false when node.terrain:
                        report.issues.Add(new Issue(true, $"{Label(g, i)} has children, so it cannot also have a terrain."));
                        break;
                }

                if (node.children.Any(child => child.weight is < 0f or float.NaN)) {
                    report.issues.Add(new Issue(true, $"{Label(g, i)} has a negative or invalid child weight."));
                } else if (!leaf && node.children.All(child => child.weight == 0f)) {
                    report.issues.Add(new Issue(false, $"All of {Label(g, i)}'s child weights are zero, so it will choose uniformly."));
                }

                if (node.children.GroupBy(child => child.nodeId).Any(group => group.Count() > 1)) {
                    report.issues.Add(new Issue(true, $"{Label(g, i)} lists the same child twice."));
                }
            }

            // Hand the structure to the real compiler for cycles, reachability and size limits.
            Dictionary<TerrainDef, int> terrainIds = new();
            SemanticGraph               graph      = new(g.rootId);
            for (int i = 0; i < g.nodes.Count; i++) {
                SemanticGraphAsset.Node entry     = g.nodes[i];
                int                     terrainId = -1;
                if (entry.children.Count == 0) {
                    if (!entry.terrain) {
                        terrainId = 0;
                    } else if (!terrainIds.TryGetValue(entry.terrain, out terrainId)) {
                        terrainIds.Add(entry.terrain, terrainId = terrainIds.Count);
                    }
                }

                SemanticNode node = new(i, terrainId);
                if (entry.useBiomeCenter) {
                    node.biomeCenter = entry.biomeCenter;
                }

                foreach (SemanticGraphAsset.Child child in entry.children) {
                    node.children.Add(new SemanticEdge(child.nodeId, child.weight));
                }

                graph.Nodes.Add(node);
            }

            foreach (SemanticGraphAsset.Adjacency rule in g.adjacencies)
                graph.Adjacencies.Add(new SemanticAdjacency(rule.sourceId, rule.direction, rule.neighborId));

            if (graph.HasCycle()) {
                report.issues.Add(new Issue(true, "The graph contains a cycle."));
                return report;
            }

            if (!graph.AllNodesReachableFromRoot()) {
                IEnumerable<string> loose = Enumerable.Range(0, g.nodes.Count)
                                                      .Except(Descendants(g, g.rootId, true))
                                                      .Select(id => Label(g, id));
                report.issues.Add(new Issue(true, $"Every node must be reachable from the root. Unattached: {string.Join(", ", loose)}."));
                return report;
            }

            try {
                report.routeCount = SemanticGraphCompiler.Compile(graph, Math.Max(1, terrainIds.Count)).RouteCount;
                if (report.routeCount > 1024) {
                    report.issues.Add(new Issue(false, $"{report.routeCount} routes is large. Shared leaves multiply routes, and solving cost grows with them."));
                }
            } catch (ArgumentException e) {
                report.issues.Add(new Issue(true, e.Message));
            }

            return report;
        }
        #endregion
    }

    [CustomEditor(typeof(SemanticGraphAsset))]
    public sealed class SemanticGraphAssetEditor : Editor {
        const float INDENT = 16f;

        readonly HashSet<SemanticGraphAsset.Node> _collapsedNodes = new();
        readonly HashSet<int> _collapsedRuleSources = new();
        SemanticGraphEditing.Report _report;
        Action _deferred;
        int _newSource, _newNeighbor;
        bool _bothWays = true;
        bool _showRawData;

        void OnEnable() => Undo.undoRedoPerformed += OnUndoRedo;
        void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

        void OnUndoRedo() {
            _report = null;
            Repaint();
        }

        public override void OnInspectorGUI() {
            SemanticGraphAsset g = (SemanticGraphAsset)target;
            _report ??= SemanticGraphEditing.Validate(g);

            DrawReport(g);
            EditorGUILayout.Space();
            DrawNodes(g);
            EditorGUILayout.Space();
            DrawAdjacency(g);
            EditorGUILayout.Space();

            _showRawData = EditorGUILayout.Foldout(_showRawData, "Raw data", true);
            if (_showRawData) {
                EditorGUI.BeginChangeCheck();
                DrawDefaultInspector();
                if (EditorGUI.EndChangeCheck()) {
                    _report = null;
                }
            }

            // Structural changes run after drawing so no list changes while it is being walked.
            if (_deferred == null) return;

            Action change = _deferred;
            _deferred = null;
            change();
        }

        #region Editing
        /// <summary>Applies a change with undo, marks the asset dirty and refreshes validation.</summary>
        void Edit(string undoName, Action change) {
            SemanticGraphAsset g = (SemanticGraphAsset)target;
            Undo.RecordObject(g, undoName);
            change();
            EditorUtility.SetDirty(g);
            _report = null;
            Repaint();
        }

        void Defer(string undoName, Action change) => _deferred = () => Edit(undoName, change);
        #endregion

        #region Report
        void DrawReport(SemanticGraphAsset g) {
            string summary = $"{g.nodes.Count} nodes";
            if (_report.routeCount >= 0) {
                summary += $", {_report.routeCount} routes";
            }

            EditorGUILayout.LabelField(summary, EditorStyles.boldLabel);

            foreach (SemanticGraphEditing.Issue issue in _report.issues) {
                EditorGUILayout.HelpBox(issue.message, issue.isError ? MessageType.Error : MessageType.Warning);
            }
        }
        #endregion

        #region Nodes
        void DrawNodes(SemanticGraphAsset g) {
            EditorGUILayout.LabelField("Nodes", EditorStyles.boldLabel);

            if (g.nodes.Count == 0) {
                if (GUILayout.Button("Create root node")) {
                    Defer("Create root node", () => g.rootId = SemanticGraphEditing.AddNode(g, "Root"));
                }

                return;
            }

            if (!SemanticGraphEditing.IsValid(g, g.rootId)) {
                int picked = EditorGUILayout.Popup("Root", 0, SemanticGraphEditing.Labels(g));
                if (picked != 0 || GUILayout.Button("Use first node as root")) {
                    Defer("Set root", () => g.rootId = picked);
                }

                return;
            }

            // Root selection is rarely changed, so it sits above the tree rather than in it.
            string[] labels = SemanticGraphEditing.Labels(g);
            EditorGUI.BeginChangeCheck();
            int root = EditorGUILayout.Popup("Root", g.rootId, labels);
            if (EditorGUI.EndChangeCheck()) {
                Edit("Change root", () => g.rootId = root);
            }

            HashSet<int> shown = new();
            DrawNode(g, g.rootId, -1, -1, 0, shown);

            HashSet<int> reachable = SemanticGraphEditing.Descendants(g, g.rootId, true);
            List<int>    loose     = Enumerable.Range(0, g.nodes.Count).Where(id => !reachable.Contains(id)).ToList();
            if (loose.Count > 0) {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Unattached nodes", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("These cannot be reached from the root. Attach them to a group with the menu, or delete them.", MessageType.Warning);

                // Subtrees that nothing links to come first, then anything left over (a cycle among unattached nodes).
                foreach (int id in loose.Where(id => SemanticGraphEditing.Parents(g, id).Count == 0)) {
                    if (!shown.Contains(id)) {
                        DrawNode(g, id, -1, -1, 0, shown);
                    }
                }

                foreach (int id in loose.Where(id => !shown.Contains(id))) {
                    DrawNode(g, id, -1, -1, 0, shown);
                }
            }

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+ New unattached node", GUILayout.Width(160))) {
                    Defer("Add node",
                          () => SemanticGraphEditing.AddNode(g, SemanticGraphEditing.UniqueName(g, "New node")));
                }
            }
        }

        /// <summary>
        /// Draws one node and, for a group, its children. A node reachable through several parents is drawn in full the
        /// first time and as a link afterwards.
        /// </summary>
        void DrawNode(SemanticGraphAsset g, int id, int parent, int edge, int depth, HashSet<int> shown) {
            if (!SemanticGraphEditing.IsValid(g, id)) {
                return;
            }

            SemanticGraphAsset.Node node   = g.nodes[id];
            bool                    isLink = !shown.Add(id);
            bool                    isLeaf = node.children.Count == 0;
            bool                    isOpen = !_collapsedNodes.Contains(node);

            using (EditorGUILayout.HorizontalScope rowScope = new()) {
                Rect rowRect = rowScope.rect;
                GUILayout.Space(depth * INDENT);

                if (isLink) {
                    GUILayout.Label(new GUIContent("\u21AA", "Shared node. Edit it where it first appears."),
                                    GUILayout.Width(14));
                    GUILayout.Label(SemanticGraphEditing.Label(g, id), EditorStyles.label, GUILayout.MinWidth(80));
                    GUILayout.FlexibleSpace();
                    if (parent >= 0) {
                        DrawEdge(g, parent, edge);
                    }

                    if (parent >= 0 && GUILayout.Button(new GUIContent("\u2715", "Remove this link"),
                                                        EditorStyles.miniButton, GUILayout.Width(22))) {
                        Defer("Remove link", () => SemanticGraphEditing.Unlink(g, parent, edge));
                    }
                } else {
                    if (isLeaf) {
                        GUILayout.Space(14);
                    } else {
                        bool open = GUILayout.Toggle(isOpen, GUIContent.none, EditorStyles.foldout,
                                                     GUILayout.Width(14));
                        if (open != isOpen) {
                            if (open) {
                                _collapsedNodes.Remove(node);
                            } else {
                                _collapsedNodes.Add(node);
                            }
                        }
                    }

                    EditorGUI.BeginChangeCheck();
                    string n = EditorGUILayout.TextField(node.name, GUILayout.MinWidth(70));
                    if (EditorGUI.EndChangeCheck()) {
                        Edit("Rename node", () => node.name = n);
                    }

                    if (isLeaf) {
                        EditorGUI.BeginChangeCheck();
                        TerrainDef terrain =
                            (TerrainDef)EditorGUILayout.ObjectField(node.terrain, typeof(TerrainDef), false,
                                                                    GUILayout.MinWidth(90));
                        if (EditorGUI.EndChangeCheck()) {
                            Edit("Assign terrain", () => {
                                node.terrain = terrain;
                                // A fresh node takes its terrain's name.
                                if (terrain && (string.IsNullOrWhiteSpace(node.name) || node.name.StartsWith("New "))) {
                                    node.name = terrain.name;
                                }
                            });
                        }
                    } else {
                        GUILayout.Label($"group \u00B7 {node.children.Count}", EditorStyles.miniLabel, GUILayout.Width(70));
                    }

                    if (parent >= 0) {
                        DrawEdge(g, parent, edge);
                    }

                    DrawBiome(node);
                    DrawPreviewColor(node);

                    if (GUILayout.Button(new GUIContent("+", "Add a child"), EditorStyles.miniButton, GUILayout.Width(22))) {
                        ShowAddChildMenu(g, id);
                    }

                    if (GUILayout.Button(new GUIContent("\u22EE", "More"), EditorStyles.miniButton, GUILayout.Width(22))) {
                        ShowNodeMenu(g, id, parent, edge);
                    }
                }

                // Right-click anywhere on the row opens the same menu as the button. A control that handles the
                // click itself, such as the name field's text menu, marks the event used first.
                Event current = Event.current;
                if (current.type == EventType.ContextClick && rowRect.Contains(current.mousePosition)) {
                    switch (isLink) {
                        case true when parent >= 0:
                            ShowLinkMenu(g, parent, edge);
                            break;
                        case false:
                            ShowNodeMenu(g, id, parent, edge);
                            break;
                    }

                    current.Use();
                }
            }

            if (isLink || isLeaf || !isOpen) {
                return;
            }

            for (int i = 0; i < node.children.Count; i++) {
                DrawNode(g, node.children[i].nodeId, id, i, depth + 1, shown);
            }
        }

        void DrawEdge(SemanticGraphAsset g, int parent, int edge) {
            SemanticGraphAsset.Node parentNode = g.nodes[parent];
            if (edge < 0 || edge >= parentNode.children.Count) return;

            SemanticGraphAsset.Child link = parentNode.children[edge];
            EditorGUI.BeginChangeCheck();
            float weight = EditorGUILayout.FloatField(link.weight, GUILayout.Width(40));
            if (EditorGUI.EndChangeCheck()) {
                Edit("Edit weight", () => link.weight = Mathf.Max(0f, weight));
            }

            float total = parentNode.children.Sum(child => child.weight);
            float share = total > 0f ? link.weight / total : 1f / parentNode.children.Count;
            GUILayout.Label($"{share * 100f:0}%", EditorStyles.miniLabel, GUILayout.Width(34));
        }

        void DrawBiome(SemanticGraphAsset.Node node) {
            bool on = GUILayout.Toggle(node.useBiomeCenter, new GUIContent("B", "Prefer a place on the regional noise (biome). Applies to everything below this node unless a deeper node sets its own."), EditorStyles.miniButton, GUILayout.Width(22));
            if (on != node.useBiomeCenter) {
                Defer("Toggle biome center", () => node.useBiomeCenter = on);
            }

            if (!node.useBiomeCenter) return;

            EditorGUI.BeginChangeCheck();
            float center = EditorGUILayout.FloatField(node.biomeCenter, GUILayout.Width(36));
            if (EditorGUI.EndChangeCheck()) {
                Edit("Change biome center", () => node.biomeCenter = Mathf.Clamp01(center));
            }
        }

        void DrawPreviewColor(SemanticGraphAsset.Node node) {
            bool custom = GUILayout.Toggle(node.overridePreviewColor,
                                           new GUIContent("\u25CF", "Override the preview color"),
                                           EditorStyles.miniButton, GUILayout.Width(22));
            if (custom != node.overridePreviewColor) {
                Defer("Toggle preview color", () => node.overridePreviewColor = custom);
            }

            if (!node.overridePreviewColor) return;

            EditorGUI.BeginChangeCheck();
            Color color = EditorGUILayout.ColorField(node.previewColor, GUILayout.Width(44));
            if (EditorGUI.EndChangeCheck()) {
                Edit("Change preview color", () => node.previewColor = color);
            }
        }

        void ShowAddChildMenu(SemanticGraphAsset g, int parent) {
            GenericMenu menu = new();
            AddChildItems(menu, g, parent, "");
            menu.ShowAsContext();
        }

        /// <summary>Adds "New node" and the existing nodes that can be linked without a cycle. The prefix is a submenu path.</summary>
        void AddChildItems(GenericMenu menu, SemanticGraphAsset g, int parent, string prefix) {
            menu.AddItem(new GUIContent(prefix + "New node"), false, () => Edit("Add child", () => {
                int child = SemanticGraphEditing.AddNode(g, SemanticGraphEditing.UniqueName(g, "New node"));
                SemanticGraphEditing.Link(g, parent, child);
            }));

            bool anyExisting = false;
            for (int id = 0; id < g.nodes.Count; id++) {
                if (!SemanticGraphEditing.CanLink(g, parent, id)) continue;

                if (!anyExisting) {
                    menu.AddSeparator(prefix);
                }

                anyExisting = true;
                int existing = id;
                menu.AddItem(new GUIContent(prefix + "Link existing/" + SemanticGraphEditing.Label(g, id)), false,
                             () => Edit("Link child", () => SemanticGraphEditing.Link(g, parent, existing)));
            }
        }

        void ShowLinkMenu(SemanticGraphAsset g, int parent, int edge) {
            GenericMenu menu = new();
            menu.AddItem(new GUIContent("Remove this link"), false, () => Edit("Remove link", () => SemanticGraphEditing.Unlink(g, parent, edge)));
            menu.ShowAsContext();
        }

        void ShowNodeMenu(SemanticGraphAsset g, int id, int parent, int edge) {
            GenericMenu menu = new();

            AddChildItems(menu, g, id, "Add child/");
            menu.AddSeparator("");

            if (parent >= 0) {
                menu.AddItem(new GUIContent("Unlink from parent"), false, () => Edit("Unlink node", () => SemanticGraphEditing.Unlink(g, parent, edge)));
            } else {
                menu.AddDisabledItem(new GUIContent("Unlink from parent"));
            }

            if (id != g.rootId) {
                menu.AddItem(new GUIContent("Delete node"), false, () => Edit("Delete node", () => SemanticGraphEditing.DeleteNode(g, id)));
            } else {
                menu.AddDisabledItem(new GUIContent("Delete node"));
            }

            // Nodes the root cannot reach can be attached under any node that would not make a cycle.
            if (parent < 0 && id != g.rootId) {
                menu.AddSeparator("");
                for (int candidate = 0; candidate < g.nodes.Count; candidate++) {
                    if (!SemanticGraphEditing.CanLink(g, candidate, id)) continue;
                    int targ = candidate;
                    menu.AddItem(new GUIContent("Attach to/" + SemanticGraphEditing.Label(g, candidate)), false, () => Edit("Attach node", () => SemanticGraphEditing.Link(g, targ, id)));
                }
            }

            menu.ShowAsContext();
        }
        #endregion

        #region Adjacency
        void DrawAdjacency(SemanticGraphAsset g) {
            EditorGUILayout.LabelField("Adjacency", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Routes through a node may sit beside routes through its listed neighbours, in the chosen directions. " +
                "Each rule also grants the opposite direction the other way round. Rules on a group apply to everything below it. " +
                "Once any rule covers a direction, the terrain adjacency table is ignored in that direction.",
                MessageType.None);

            if (g.nodes.Count == 0) return;

            string[]                        labels = SemanticGraphEditing.Labels(g);
            List<SemanticGraphEditing.Rule> rules  = SemanticGraphEditing.Rules(g);

            foreach (IGrouping<int, SemanticGraphEditing.Rule> group in rules.GroupBy(rule => rule.source)) {
                int  source = group.Key;
                bool open   = !_collapsedRuleSources.Contains(source);
                bool nowOpen = EditorGUILayout.Foldout(open, $"{SemanticGraphEditing.Label(g, source)}  ({group.Count()})", true);
                if (nowOpen != open) {
                    if (nowOpen) {
                        _collapsedRuleSources.Remove(source);
                    } else {
                        _collapsedRuleSources.Add(source);
                    }
                }

                if (!nowOpen) continue;

                foreach (SemanticGraphEditing.Rule rule in group) DrawRule(g, rule, labels);
            }

            DrawRuleComposer(g, labels);
        }

        void DrawRule(SemanticGraphAsset g, SemanticGraphEditing.Rule rule, string[] labels) {
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(INDENT);
                GUILayout.Label("beside", EditorStyles.miniLabel, GUILayout.Width(38));

                bool valid  = SemanticGraphEditing.IsValid(g, rule.neighbor);
                int  picked = EditorGUILayout.Popup(valid ? rule.neighbor : 0, labels, GUILayout.MinWidth(90));
                if (valid && picked != rule.neighbor) {
                    Defer("Change neighbour", () => SemanticGraphEditing.ChangeRuleNeighbor(g, rule.source, rule.neighbor, picked));
                }

                int mask = rule.mask;
                for (int d = 0; d < SemanticGraphEditing.DirectionNames.Length; d++) {
                    bool  on    = (rule.mask & (1 << d)) != 0;
                    float width = d % 2 == 1 ? 28f : 22f;
                    bool want = GUILayout.Toggle(on, SemanticGraphEditing.DirectionNames[d], EditorStyles.miniButton,
                                                 GUILayout.Width(width));
                    // A rule with no directions would vanish, so the last one stays on. Use the remove button instead.
                    if (want != on && !(on && mask == 1 << d)) {
                        mask ^= 1 << d;
                    }
                }

                if (mask != rule.mask) {
                    Defer("Edit directions", () => SemanticGraphEditing.SetRule(g, rule.source, rule.neighbor, mask));
                }

                if (GUILayout.Button(new GUIContent("\u25BE", "Direction presets"), EditorStyles.miniButton, GUILayout.Width(22))) {
                    GenericMenu menu = new();
                    AddPreset(menu, "Cardinal (N E S W)", g, rule, SemanticGraphEditing.CARDINALS);
                    AddPreset(menu, "Diagonal", g, rule, SemanticGraphEditing.DIAGONALS);
                    AddPreset(menu, "All eight", g, rule, SemanticGraphEditing.ALL_DIRECTIONS);
                    menu.ShowAsContext();
                }

                if (GUILayout.Button(new GUIContent("\u2715", "Remove rule"), EditorStyles.miniButton, GUILayout.Width(22))) {
                    Defer("Remove rule", () => SemanticGraphEditing.SetRule(g, rule.source, rule.neighbor, 0));
                }
            }
        }

        void AddPreset(GenericMenu menu, string label, SemanticGraphAsset g, SemanticGraphEditing.Rule rule, int mask) =>
            menu.AddItem(new GUIContent(label), rule.mask == mask, () => Edit("Set directions", () => SemanticGraphEditing.SetRule(g, rule.source, rule.neighbor, mask)));

        void DrawRuleComposer(SemanticGraphAsset g, string[] labels) {
            _newSource   = Mathf.Clamp(_newSource, 0, g.nodes.Count - 1);
            _newNeighbor = Mathf.Clamp(_newNeighbor, 0, g.nodes.Count - 1);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Add rule (starts with N E S W; adjust the directions afterwards)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope()) {
                _newSource = EditorGUILayout.Popup(_newSource, labels, GUILayout.MinWidth(80));
                GUILayout.Label("beside", EditorStyles.miniLabel, GUILayout.Width(38));
                _newNeighbor = EditorGUILayout.Popup(_newNeighbor, labels, GUILayout.MinWidth(80));
                _bothWays = GUILayout.Toggle(
                    _bothWays,
                    new GUIContent("both ways", "Also allow the neighbour to sit beside the source in the same directions"), GUILayout.Width(70));

                if (!GUILayout.Button("Add", GUILayout.Width(44))) return;
                int  source = _newSource, neighbor = _newNeighbor;
                bool both   = _bothWays;
                Defer("Add adjacency rule", () => {
                    SemanticGraphEditing.AddRule(g, source, neighbor, SemanticGraphEditing.CARDINALS);
                    if (both && source != neighbor) {
                        SemanticGraphEditing.AddRule(g, neighbor, source, SemanticGraphEditing.CARDINALS);
                    }
                });
            }
        }
        #endregion
    }
}
#endif