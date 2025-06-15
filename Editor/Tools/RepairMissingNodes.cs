using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text; // Added for StringBuilder
using Lattice.Base;
using Lattice.Editor.SearchProviders;
using Lattice.Nodes;
using Lattice.StandardLibrary; // Assuming ScriptNode lives here or similar
using Lattice.Utils; // Assuming SerializableMethodInfo/SerializableType live here
using Unity.Entities; // Assuming IComponentData lives here
using UnityEditor;
using UnityEditor.Experimental.GraphView; // For SearchWindow
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UIElements;

namespace Lattice.Editor.Tools
{
    /// <summary>
    /// An editor window to find and repair missing types (Nodes, Methods, ECS Components)
    /// in LatticeGraph assets due to code refactoring or changes.
    /// </summary>
    public class RepairMissingNodes : EditorWindow
    {
        // Represents a missing Type in a SerializeReference field.
        // Equality is based on Assembly, Namespace, and Type name.
        private struct MissingManagedType : IEquatable<MissingManagedType>
        {
            public readonly string Assembly;
            public readonly string Namespace;
            public readonly string TypeName;
            public readonly string FullName; // Added for easier display/matching

            public MissingManagedType(string assembly, string ns, string typeName)
            {
                Assembly = assembly;
                Namespace = ns;
                TypeName = typeName;
                FullName = string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}";
            }

            public bool Equals(MissingManagedType other)
            {
                return Assembly == other.Assembly && Namespace == other.Namespace && TypeName == other.TypeName;
            }

            public override bool Equals(object obj) => obj is MissingManagedType other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Assembly, Namespace, TypeName);
            public override string ToString() => $"Assembly: {Assembly}, Type: {FullName}";
        }

        // --- UI Elements ---
        private ScrollView _resultsScrollView;
        private Button _refreshButton;
        private Button _autoMatchButton;
        private Button _refactorButton;

        // --- State ---
        // Store the missing items found
        private Dictionary<MissingManagedType, HashSet<LatticeGraph>> _missingNodeTypes = new();
        private Dictionary<SerializableMethodInfo, HashSet<ScriptNode>> _missingMethods = new();
        private Dictionary<SerializableType, HashSet<EcsComponentNode>> _missingComponentTypes = new();

        // Map missing items to their UI controls and potential replacement
        private class UIMapping
        {
            public VisualElement RowElement;
            public TextField InputField;
            public Type ProposedTypeReplacement; // For Nodes and ECS Components
            public SerializableMethodInfo ProposedMethodReplacement; // For Methods
        }
        private Dictionary<object, UIMapping> _uiMapping = new(); // Key: MissingManagedType, SerializableMethodInfo, or SerializableType

        // --- Menu Item ---
        [MenuItem("Lattice/Tools/Repair Missing Nodes")]
        public static void OpenRepairWindow()
        {
            var window = GetWindow<RepairMissingNodes>();
            window.titleContent = new GUIContent("Lattice: Repair Missing Nodes");
            // window.ShowPopup(); // ShowPopup might not be ideal for a utility window, Show() is more typical
            window.Show();
        }

        // --- Unity Methods ---
        private void CreateGUI()
        {
            rootVisualElement.Clear(); // Clear previous elements
            _uiMapping.Clear();      // Clear previous mappings

            // Add description
            rootVisualElement.Add(new Label(
                "This tool scans Lattice Graph assets for missing Nodes, Methods (in ScriptNodes), or ECS Components (in EcsComponentNodes) " +
                "referenced in serialized data. Use the list below to specify replacements.\n" +
                "'Attempt Auto-Match' tries to find replacements based on type names." +
                "'Refactor' applies the specified changes. \n" +
                "Warning: Refactoring directly modifies asset files. Ensure version control is clean.")
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 10 }
            });

            // Add control buttons
            var buttonContainer = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 10 } };
            _refreshButton = new Button(RefreshUI) { text = "Refresh Scan" };
            _autoMatchButton = new Button(AttemptAutoMatch) { text = "Attempt Auto-Match", tooltip = "Tries to find likely replacements based on type/method names." };
            _refactorButton = new Button(ApplyRefactoring) { text = "Refactor" };
            buttonContainer.Add(_refreshButton);
            buttonContainer.Add(_autoMatchButton);
            buttonContainer.Add(_refactorButton);
            rootVisualElement.Add(buttonContainer);

            // Add scroll view for results
            _resultsScrollView = new ScrollView(ScrollViewMode.Vertical);
            rootVisualElement.Add(_resultsScrollView);

            // Initial Scan
            RefreshUI();
        }

        // Renamed from original RefreshUI to better reflect its purpose
        private void FindMissingItems()
        {
            _missingNodeTypes.Clear();
            _missingMethods.Clear();
            _missingComponentTypes.Clear();

            string[] guids = AssetDatabase.FindAssets("t:" + nameof(LatticeGraph));
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                LatticeGraph graph = AssetDatabase.LoadAssetAtPath<LatticeGraph>(path);
                if (graph == null) continue; // Skip if loading failed

                // 1. Find Missing Managed Reference Types (likely BaseNode derivatives)
                // Use try-catch as GetManagedReferencesWithMissingTypes can sometimes throw exceptions on corrupted data
                try
                {
                    if (SerializationUtility.HasManagedReferencesWithMissingTypes(graph))
                    {
                        List<ManagedReferenceMissingType> missingRefs = SerializationUtility.GetManagedReferencesWithMissingTypes(graph).ToList();
                        foreach (ManagedReferenceMissingType missingRef in missingRefs)
                        {
                            // Basic check to filter out potentially non-node types if needed, though often these ARE nodes.
                            // This depends heavily on how SerializeReference is used elsewhere.
                             if (string.IsNullOrEmpty(missingRef.className)) continue; // Skip invalid entries

                            var missing = new MissingManagedType(missingRef.assemblyName, missingRef.namespaceName, missingRef.className);
                            if (!_missingNodeTypes.TryGetValue(missing, out var graphSet))
                            {
                                graphSet = new HashSet<LatticeGraph>();
                                _missingNodeTypes.Add(missing, graphSet);
                            }
                            graphSet.Add(graph);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error checking missing references in graph '{path}': {e.Message}", graph);
                }


                // 2. Find Missing Methods in ScriptNodes
                foreach (var scriptNode in graph.LatticeNodes<ScriptNode>())
                {
                    var method = scriptNode.Method; // This is a SerializableMethodInfo
                    if (method == null || !method.IsValid())
                    {
                        // Log error or handle ScriptNodes with invalid method references if needed
                        // Debug.LogWarning($"ScriptNode has null or invalid method reference in graph '{path}'. Node ID: {scriptNode.GetInstanceID()}", graph);
                        continue;
                    }

                    if (!method.Exists()) // Checks if the underlying MethodInfo can be found
                    {
                        if (!_missingMethods.TryGetValue(method, out var nodeSet))
                        {
                            nodeSet = new HashSet<ScriptNode>();
                            _missingMethods.Add(method, nodeSet);
                        }
                        nodeSet.Add(scriptNode);
                    }
                }

                // 3. Find Missing Component Types in EcsComponentNodes
                foreach(var ecsComponentNode in graph.LatticeNodes<EcsComponentNode>())
                {
                    var componentType = ecsComponentNode.ComponentType; // This is a SerializableType
                    if (componentType != null && componentType.IsMissing()) // IsMissing checks if Type.GetType fails
                    {
                         if (!_missingComponentTypes.TryGetValue(componentType, out var nodeSet))
                        {
                            nodeSet = new HashSet<EcsComponentNode>();
                            _missingComponentTypes.Add(componentType, nodeSet);
                        }
                        nodeSet.Add(ecsComponentNode);
                    }
                }
            }
             Debug.Log($"Scan Complete: Found {_missingNodeTypes.Count} missing node types, " +
                      $"{_missingMethods.Count} missing methods, {_missingComponentTypes.Count} missing component types.");
        }

        // Builds the UI based on the found missing items
        private void BuildUI()
        {
            _resultsScrollView.Clear(); // Clear previous results
            _uiMapping.Clear();       // Clear previous mappings

            bool itemsFound = false;

            // Section for Missing Node Types
            if (_missingNodeTypes.Any())
            {
                 itemsFound = true;
                _resultsScrollView.Add(new Label("Missing Node Types (Managed References):") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
                foreach (var kvp in _missingNodeTypes)
                {
                    MissingManagedType missing = kvp.Key;
                    HashSet<LatticeGraph> graphs = kvp.Value;
                    string graphList = string.Join("\n", graphs.Select(g => AssetDatabase.GetAssetPath(g)));
                    string label = $"[{missing.TypeName}] ns:[{missing.Namespace}] asm:[{missing.Assembly}]";

                    VisualElement row = BuildMissingItemRow(
                        label,
                        graphList,
                        missing, // Pass the key for mapping
                        SearchNodeType, // Action for search button
                        DeleteMissingNode // Action for delete button
                    );
                    _resultsScrollView.Add(row);
                }
            }

            // Section for Missing Methods
            if (_missingMethods.Any())
            {
                 itemsFound = true;
                _resultsScrollView.Add(new Label("\nMissing Methods (Script Nodes):") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
                foreach (var kvp in _missingMethods)
                {
                    SerializableMethodInfo missingMethod = kvp.Key;
                    HashSet<ScriptNode> nodes = kvp.Value;
                    string nodeInfo = string.Join("\n", nodes.Select(n => $"{AssetDatabase.GetAssetPath(n.Graph)} NodeID:{n.Guid}")); // More specific info
                    string label = missingMethod.ToString(); // Uses SerializableMethodInfo's ToString()

                     VisualElement row = BuildMissingItemRow(
                        label,
                        nodeInfo,
                        missingMethod, // Pass the key for mapping
                        SearchMethod,  // Action for search button
                        null // No delete action for methods currently
                    );
                    _resultsScrollView.Add(row);
                }
            }

             // Section for Missing ECS Component Types
            if (_missingComponentTypes.Any())
            {
                 itemsFound = true;
                _resultsScrollView.Add(new Label("\nMissing ECS Component Types:") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
                foreach (var kvp in _missingComponentTypes)
                {
                    SerializableType missingType = kvp.Key;
                    HashSet<EcsComponentNode> nodes = kvp.Value;
                     string nodeInfo = string.Join("\n", nodes.Select(n => $"{AssetDatabase.GetAssetPath(n.Graph)} NodeID:{n.Guid}")); // More specific info
                    string label = missingType.serializedType; // Display the stored string

                    VisualElement row = BuildMissingItemRow(
                        label,
                        nodeInfo,
                        missingType, // Pass the key for mapping
                        SearchComponentType, // Action for search button
                        null // No delete action for component types currently
                    );
                     _resultsScrollView.Add(row);
                }
            }

            if (!itemsFound)
            {
                _resultsScrollView.Add(new Label("No missing references found in Lattice Graphs."));
            }

            // Enable/Disable buttons based on findings
            _autoMatchButton.SetEnabled(itemsFound);
            _refactorButton.SetEnabled(itemsFound);
        }

        // Generic method to create a row for a missing item
        private VisualElement BuildMissingItemRow(string itemLabel, string tooltipText, object missingItemKey, Action<object, TextField> searchAction, Action<object> deleteAction)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 5, marginBottom = 5 } };

            var tooltipLabel = new Label("[?]") { tooltip = $"Used in:\n{tooltipText}", style = { marginRight = 5 } };
            row.Add(tooltipLabel);

            var label = new Label(itemLabel) { style = { width = 350, textOverflow = TextOverflow.Ellipsis, marginRight = 10 } }; // Fixed width helps alignment
            row.Add(label);

            var textField = new TextField() { style = { flexGrow = 1, marginRight = 5 } };
            row.Add(textField);

            var searchButton = new Button(() => searchAction(missingItemKey, textField)) { text = "🔎", tooltip = "Choose Replacement...", style = { marginRight = 5 } };
            row.Add(searchButton);

            if (deleteAction != null)
            {
                 var deleteButton = new Button(() => deleteAction(missingItemKey)) { text = "💀", tooltip = "Delete Objects...", style = { marginRight = 5 } };
                row.Add(deleteButton);
            }

            // Store the mapping
            _uiMapping[missingItemKey] = new UIMapping { RowElement = row, InputField = textField };

            return row;
        }

        // --- Button Actions ---

        // Refresh: Rescan assets and rebuild the UI
        private void RefreshUI()
        {
            FindMissingItems();
            BuildUI();
        }

        // Search Actions for Different Types
        private void SearchNodeType(object key, TextField targetField)
        {
            MissingManagedType missingType = (MissingManagedType)key; // Cast key back
            TypesSearchProvider.Instance.BaseType = typeof(BaseNode); // Ensure BaseType is set correctly
            SearchWindow.Open(new SearchWindowContext(GUIUtility.GUIToScreenPoint(Event.current.mousePosition), 800), TypesSearchProvider.Instance);
            TypesSearchProvider.Instance.Callback = type =>
            {
                if (type != null)
                {
                    targetField.value = type.AssemblyQualifiedName;
                    if (_uiMapping.TryGetValue(key, out var mapping))
                    {
                         mapping.ProposedTypeReplacement = type; // Store the selected type
                    }
                }
            };
        }

        private void SearchMethod(object key, TextField targetField)
        {
            SerializableMethodInfo missingMethod = (SerializableMethodInfo)key; // Cast key back
            // Potentially filter ScriptNodeMethodSearchProvider based on static/instance if known
            SearchWindow.Open(new SearchWindowContext(GUIUtility.GUIToScreenPoint(Event.current.mousePosition), 800), ScriptNodeMethodSearchProvider.Instance);
            ScriptNodeMethodSearchProvider.Instance.Callback = methodInfo =>
            {
                 if (methodInfo != null)
                {
                    // Determine appropriate binding flags (this might need adjustment based on your script node requirements)
                    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic; // Default broad flags
                    flags |= methodInfo.IsStatic ? BindingFlags.Static : BindingFlags.Instance;

                    var newSerializableMethod = new SerializableMethodInfo(methodInfo, flags);
                    targetField.value = newSerializableMethod.ToString(); // Display consistent format
                    if (_uiMapping.TryGetValue(key, out var mapping))
                    {
                        mapping.ProposedMethodReplacement = newSerializableMethod; // Store the selected method
                    }
                }
            };
        }

         private void SearchComponentType(object key, TextField targetField)
        {
            SerializableType missingType = (SerializableType)key; // Cast key back
            TypesSearchProvider.Instance.BaseType = typeof(IComponentData); // Ensure BaseType is set correctly
            SearchWindow.Open(new SearchWindowContext(GUIUtility.GUIToScreenPoint(Event.current.mousePosition), 800), TypesSearchProvider.Instance);
            TypesSearchProvider.Instance.Callback = type =>
            {
                if (type != null)
                {
                    targetField.value = type.AssemblyQualifiedName;
                     if (_uiMapping.TryGetValue(key, out var mapping))
                    {
                         mapping.ProposedTypeReplacement = type; // Store the selected type
                    }
                }
            };
        }

        // Delete Action for Missing Nodes
         private void DeleteMissingNode(object key)
        {
            MissingManagedType missing = (MissingManagedType)key;
            if (!_missingNodeTypes.TryGetValue(missing, out var graphs)) return;

            string graphList = string.Join("\n", graphs.Select(g => AssetDatabase.GetAssetPath(g) ?? "Unknown Path"));
            if (EditorUtility.DisplayDialog($"Confirm Deletion",
                    $"Permanently remove all nodes matching type [{missing.FullName}] from {graphs.Count} graphs?\n\n{graphList}\n\nThis action cannot be undone.",
                    "Delete Nodes", "Cancel"))
            {
                bool changed = false;
                foreach (var graph in graphs)
                {
                    if (graph == null) continue; // Skip if graph was deleted/unloaded

                    // Important: We need the *original* missing type info from SerializationUtility for removal
                    List<ManagedReferenceMissingType> missingRefsInGraph = SerializationUtility.GetManagedReferencesWithMissingTypes(graph).ToList();

                    foreach (var m in missingRefsInGraph)
                    {
                        // Match using the original components
                        if (m.assemblyName == missing.Assembly &&
                            m.namespaceName == missing.Namespace &&
                            m.className == missing.TypeName)
                        {
                           if (SerializationUtility.ClearManagedReferenceWithMissingType(graph, m.referenceId))
                           {
                                Debug.Log($"Removed node with missing type '{missing.FullName}' (Ref ID: {m.referenceId}) from graph '{AssetDatabase.GetAssetPath(graph)}'");
                                EditorUtility.SetDirty(graph);
                                changed = true;
                           }
                           else
                           {
                                Debug.LogWarning($"Failed to remove node with missing type '{missing.FullName}' (Ref ID: {m.referenceId}) from graph '{AssetDatabase.GetAssetPath(graph)}'");
                           }
                        }
                    }
                }

                if (changed)
                {
                    EditorUtility.DisplayDialog("Deletion Complete", $"Removed nodes in {graphs.Count} graphs. Saving assets.", "OK");
                    AssetDatabase.SaveAssets(); // Save changes
                    RefreshUI(); // Refresh the list
                }
                else
                {
                     EditorUtility.DisplayDialog("Deletion Notice", "No matching nodes found or removed in the selected graphs.", "OK");
                }
            }
        }

        // Attempt to automatically find replacements
        private void AttemptAutoMatch()
        {
             Debug.Log("Attempting Auto-Match...");
             int matchesFound = 0;

            // Pre-fetch types for efficiency
            var allTypes = TypeCache.GetTypesDerivedFrom<BaseNode>();
            var allComponentTypes = TypeCache.GetTypesDerivedFrom<IComponentData>();
            var allMethods =  TypeCache.GetMethodsWithAttribute<LatticeNodeAttribute>()
                .Concat(TypeCache.GetTypesWithAttribute<LatticeNodesAttribute>()
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)))
                .ToList();

            // Match Node Types
            foreach (var kvp in _missingNodeTypes)
            {
                MissingManagedType missing = kvp.Key;
                 if (!_uiMapping.TryGetValue(missing, out var mapping) || !string.IsNullOrEmpty(mapping.InputField.value)) continue; // Skip if already filled

                Type foundType = FindBestTypeMatch(missing.FullName, missing.TypeName, allTypes);
                if (foundType != null)
                {
                    mapping.InputField.value = foundType.AssemblyQualifiedName;
                    mapping.ProposedTypeReplacement = foundType;
                    matchesFound++;
                    Debug.Log($"Auto-matched Node Type: '{missing.FullName}' -> '{foundType.AssemblyQualifiedName}'");
                }
            }

             // Match Methods
            foreach (var kvp in _missingMethods)
            {
                SerializableMethodInfo missingMethod = kvp.Key;
                 if (!_uiMapping.TryGetValue(missingMethod, out var mapping) || !string.IsNullOrEmpty(mapping.InputField.value)) continue;

                // Try to find based on TypeName and MethodName
                // This is less reliable as signatures might change. Needs a good strategy.
                // Basic Strategy: Find type by name, then method by name.
                string missingMethodName = missingMethod.GetMethodName(); // Assuming SerializableMethodInfo stores this
                 if (!string.IsNullOrEmpty(missingMethodName))
                 {
                    foreach(var method in allMethods)
                    {
                        if (method.Name == missingMethodName)
                        {
                            // Find static public methods matching the name
                            var newSerializableMethod = new SerializableMethodInfo(method, BindingFlags.Public | BindingFlags.Static);
                            mapping.ProposedMethodReplacement = newSerializableMethod;
                            mapping.InputField.value = newSerializableMethod.ToString();
                            matchesFound++;
                            Debug.Log($"Auto-matched Method: '{missingMethod}' -> '{newSerializableMethod}'");
                        }
                    }
                 }
            }

            // Match Component Types
            foreach (var kvp in _missingComponentTypes)
            {
                SerializableType missing = kvp.Key;
                 if (!_uiMapping.TryGetValue(missing, out var mapping) || !string.IsNullOrEmpty(mapping.InputField.value)) continue;

                // Extract type name from serialized string (basic parsing)
                string typeName = GetTypeNameFromSerializedString(missing.serializedType);
                string fullTypeName = GetFullTypeNameFromSerializedString(missing.serializedType);


                 Type foundType = FindBestTypeMatch(fullTypeName, typeName, allComponentTypes);
                 if (foundType != null)
                {
                    mapping.InputField.value = foundType.AssemblyQualifiedName;
                    mapping.ProposedTypeReplacement = foundType;
                    matchesFound++;
                    Debug.Log($"Auto-matched Component Type: '{missing.serializedType}' -> '{foundType.AssemblyQualifiedName}'");
                }
            }

            if (matchesFound > 0)
            {
                 EditorUtility.DisplayDialog("Auto-Match", $"Found {matchesFound} potential matches. Please review before refactoring.", "OK");
            }
            else
            {
                 EditorUtility.DisplayDialog("Auto-Match", "No automatic matches found.", "OK");
            }
        }

        // Helper for auto-matching types
        private Type FindBestTypeMatch(string fullName, string typeNameOnly, IEnumerable<Type> candidatePool)
        {
             if (string.IsNullOrEmpty(typeNameOnly)) return null;

            // 1. Try exact full name match (Namespace.TypeName)
             Type match = candidatePool.FirstOrDefault(t => t.FullName == fullName);
             if (match != null) return match;

            // 2. Try type name only match
             var nameMatches = candidatePool.Where(t => t.Name == typeNameOnly).ToList();
             if (nameMatches.Count == 1) return nameMatches[0]; // Unambiguous match

            // 3. Handle ambiguity or no match (return null for now)
             if (nameMatches.Count > 1)
             {
                 Debug.LogWarning($"Ambiguous match for type name '{typeNameOnly}'. Found: {string.Join(", ", nameMatches.Select(t=>t.FullName))}. Cannot auto-match.");
             }

             return null; // No match found
        }

         // Basic helpers to extract type names from assembly-qualified or simple names
        private string GetTypeNameFromSerializedString(string serialized)
        {
             if (string.IsNullOrEmpty(serialized)) return null;
             string namePart = serialized.Split(',')[0].Trim(); // Get part before first comma
             if (namePart.Contains(".")) return namePart.Substring(namePart.LastIndexOf('.') + 1);
             return namePart;
        }
         private string GetFullTypeNameFromSerializedString(string serialized)
        {
             if (string.IsNullOrEmpty(serialized)) return null;
             return serialized.Split(',')[0].Trim(); // Get part before first comma
        }


        // Apply the specified refactoring changes
        private void ApplyRefactoring()
        {
            bool changesMade = false;
            bool needsSave = false;
            bool userConfirmed = false;
            var graphsToDirty = new HashSet<LatticeGraph>();
            var logBuilder = new StringBuilder();

            // Process Node Type Replacements (Direct File Edit)
            foreach (var kvp in _missingNodeTypes)
            {
                MissingManagedType missing = kvp.Key;
                if (!_uiMapping.TryGetValue(missing, out var mapping) || mapping.ProposedTypeReplacement == null) continue; // Skip if no replacement specified

                Type replacementType = mapping.ProposedTypeReplacement; // Use the stored type

                if (!userConfirmed) {
                    if (!EditorUtility.DisplayDialog("Confirm Refactor",
                         "This action will modify asset files directly on disk for Node Type changes, and update loaded assets for others.\n\nIt's HIGHLY recommended to have a clean Git status before proceeding.\n\nContinue?",
                         "Yes, Refactor", "Cancel"))
                    {
                         return; // Abort
                    }
                    userConfirmed = true;
                }

                logBuilder.AppendLine($"\nProcessing Node Type: {missing.FullName} -> {replacementType.FullName}");
                foreach (var graph in kvp.Value)
                {
                     if (graph == null) continue;
                     if (RefactorManagedTypeInAsset(graph, missing, replacementType, logBuilder))
                     {
                         changesMade = true; // Mark that *file* changes were made
                         needsSave = true; // Indicate that AssetDatabase refresh/reload might be needed
                     }
                }
            }


             // Process Method Replacements (C# Object Edit)
            foreach (var kvp in _missingMethods)
            {
                SerializableMethodInfo missingMethod = kvp.Key;
                if (!_uiMapping.TryGetValue(missingMethod, out var mapping) || mapping.ProposedMethodReplacement == null) continue;

                 // Confirmation dialog handled above if needed
                 if (!userConfirmed) {
                     if (!EditorUtility.DisplayDialog("Confirm Refactor",
                         "This action will modify files on disk. Are you sure?\n\nIt's recommended to have a clean Git status before proceeding.\n\nContinue?",
                         "Yes, Refactor", "Cancel"))
                     {
                         return; // Abort
                     }
                     userConfirmed = true;
                 }

                SerializableMethodInfo newMethod = mapping.ProposedMethodReplacement;
                Assert.IsNotNull(newMethod, $"Replacement method for {missingMethod} should not be null here.");

                 logBuilder.AppendLine($"\nProcessing Method: {missingMethod} -> {newMethod}");
                 foreach (var node in kvp.Value)
                 {
                     if (node == null || node.Graph == null) continue; // Node or graph might have been deleted

                     logBuilder.AppendLine($"  Updating node {node.Guid} in graph {AssetDatabase.GetAssetPath(node.Graph)}");
                     node.Method = newMethod;
                     graphsToDirty.Add(node.Graph);
                     changesMade = true;
                 }
            }

             // Process Component Type Replacements (C# Object Edit)
            foreach (var kvp in _missingComponentTypes)
            {
                 SerializableType missingType = kvp.Key;
                 if (!_uiMapping.TryGetValue(missingType, out var mapping) || mapping.ProposedTypeReplacement == null) continue;

                 // Confirmation dialog handled above if needed
                 if (!userConfirmed) {
                     if (!EditorUtility.DisplayDialog("Confirm Refactor",
                         "This action will modify loaded assets in memory. Save assets afterwards?\n\nIt's recommended to have a clean Git status before proceeding.\n\nContinue?",
                         "Yes, Refactor", "Cancel"))
                     {
                         return; // Abort
                     }
                     userConfirmed = true;
                 }

                 Type newType = mapping.ProposedTypeReplacement;
                 Assert.IsNotNull(newType, $"Replacement type for {missingType.serializedType} should not be null here.");

                 logBuilder.AppendLine($"\nProcessing Component Type: {missingType.serializedType} -> {newType.FullName}");
                 foreach (var node in kvp.Value)
                 {
                      if (node == null || node.Graph == null) continue; // Node or graph might have been deleted

                     logBuilder.AppendLine($"  Updating node {node.Guid} in graph {AssetDatabase.GetAssetPath(node.Graph)}");
                     node.ComponentType = new SerializableType(newType); // Update using constructor or direct assignment if possible
                     graphsToDirty.Add(node.Graph);
                     changesMade = true;
                 }
            }

            // --- Finalization ---
            if (changesMade)
            {
                // Set dirty flags for C# changes
                foreach(var graph in graphsToDirty)
                {
                    if (graph != null) EditorUtility.SetDirty(graph);
                }

                 // Save C# changes
                 if (graphsToDirty.Any())
                 {
                     AssetDatabase.SaveAssets();
                     logBuilder.AppendLine("\nApplied C# reference changes and saved assets.");
                 }

                 // Refresh assets if file changes were made
                 if (needsSave) // Only if direct file edits happened
                 {
                     logBuilder.AppendLine("\nApplied direct file modifications. Refreshing AssetDatabase...");
                     AssetDatabase.Refresh();
                     // Requesting script reload can be disruptive, only do if necessary (e.g., if script types themselves changed assembly)
                     // Consider if this is truly needed after just data changes. Often Refresh is enough.
                     // EditorUtility.RequestScriptReload();
                     logBuilder.AppendLine("AssetDatabase refreshed. Manual reload might be needed if issues persist.");

                 }

                Debug.Log($"Refactoring complete. Log:\n{logBuilder}");
                EditorUtility.DisplayDialog("Refactor Complete", "Refactoring process finished. Check the console log for details.", "OK");

                // Refresh the UI to show updated status
                RefreshUI();
            }
            else
            {
                 EditorUtility.DisplayDialog("Refactor Notice", "No replacements were specified or applied.", "OK");
            }
        }

        // --- Helper Methods ---

        /// <summary>
        /// Modifies the asset file directly to replace SerializeReference type information.
        /// Warning: This is brittle and relies on Unity's YAML format.
        /// </summary>
        /// <returns>True if the file was modified, false otherwise.</returns>
        private static bool RefactorManagedTypeInAsset(LatticeGraph graph, MissingManagedType original, Type replacement, StringBuilder logBuilder)
        {
            var path = AssetDatabase.GetAssetPath(graph);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                 logBuilder.AppendLine($"  Skipping graph (path invalid or file not found): {path}");
                 return false;
            }

            try
            {
                var fileText = File.ReadAllText(path);
                string originalText = $"type: {{class: {original.TypeName}, ns: {original.Namespace}, asm: {original.Assembly}}}";

                 // Handle case where namespace might be empty in the original YAML
                if (string.IsNullOrEmpty(original.Namespace)) {
                     originalText = $"type: {{class: {original.TypeName}, ns: , asm: {original.Assembly}}}";
                 }

                string replacementAssembly = replacement.Assembly.GetName().Name; // Get assembly name without version/culture etc.
                string replacementText = $"type: {{class: {replacement.Name}, ns: {replacement.Namespace ?? ""}, asm: {replacementAssembly}}}"; // Ensure ns is not null

                if (fileText.Contains(originalText))
                {
                    fileText = fileText.Replace(originalText, replacementText);
                    File.WriteAllText(path, fileText);
                    logBuilder.AppendLine($"  Modified file: {path} (Replaced '{original.FullName}' with '{replacement.FullName}')");
                    return true;
                }
                else
                {
                     logBuilder.AppendLine($"  Skipping file (original type string not found): {path}");
                     // This might happen if the format is different than expected, or if the reference was already cleared.
                     return false;
                }
            }
            catch (Exception ex)
            {
                 Debug.LogError($"Failed to refactor type '{original.FullName}' in asset '{path}'. Error: {ex.Message}");
                 logBuilder.AppendLine($"  ERROR processing file {path}: {ex.Message}");
                 return false;
            }

            // TODO: Add support for generic types if needed. This requires more complex parsing/replacement.
            // TODO: Add support for replacing types within our custom SerializedType class if it uses string serialization directly in YAML.
        }
    }
}