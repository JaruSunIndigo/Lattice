using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.IR;
using Lattice.Utils;
using Unity.Assertions;
using Unity.Entities.Serialization;
using UnityEditor;
using UnityEngine;
using Hash128 = Unity.Entities.Hash128;
using Random = UnityEngine.Random;

namespace Lattice.Base
{
    /// <summary>Represents a change event that happened to a BaseGraph. All fields may be null.</summary>
    public class GraphChanges
    {
        public BaseGraph entireGraphInitialized;
        public SerializableEdge addedEdge;
        public Group addedGroups;
        public BaseNode addedNode;
        public BaseStackNode addedStackNode;
        public StickyNote addedStickyNotes;
        public BaseNode nodeChanged;
        public SerializableEdge removedEdge;
        public Group removedGroups;
        public BaseNode removedNode;
        public BaseStackNode removedStackNode;
        public StickyNote removedStickyNotes;
    }

    /// <summary>
    ///     The core graph type for representing a general graph of nodes, edges, and ports. This is our serialization
    ///     format for the graphs users edit.
    /// </summary>
    [Serializable]
    public abstract class BaseGraph : ScriptableObject
    {
        public const bool Verbose = false;
        
        /// <summary>Triggered when the graph is changed.</summary>
        public event Action<GraphChanges> OnGraphChanges;

        /// <summary>All of the nodes in the graph.</summary>
        [SerializeReference]
        public List<BaseNode> nodes = new();

        /// <summary>The list of edges which have valid ports. Skips any edges in the graph that are missing their node targets.</summary>
        public IEnumerable<SerializableEdge> NonDanglingEdges() => edges.Where(e => !e.IsDangling());

        /// <summary>All of the edges in the graph.</summary>
        public List<SerializableEdge> edges = new();

        /// <summary>All groups in the graph</summary>
        public List<Group> groups = new();

        /// <summary>All Stack Nodes in the graph</summary>
        [SerializeReference]
        public List<BaseStackNode> stackNodes = new();

        /// <summary>All of the sticky notes in the graph.</summary>
        public List<StickyNote> stickyNotes = new();

        // Where the view is positioned in the graph. We store this on the BaseGraph so the view location is retained if
        // the window is closed/opened. But we don't serialize it to disk.
        [NonSerialized]
        public Vector3 ViewPosition;

        [NonSerialized]
        public float ViewScale = 1f;

        // True, if this asset should be saved before baking. For graphs with baked nodes, we have to save this asset to disk 
        // before the bake runs, if so. This is costly, so we only do it when we modify nodes that are baked.
        // Note that this forced saving is only necessary if the dependent subscene is closed. In open mode it'll work
        // incrementally just fine.
        [NonSerialized]
        public bool NeedsSaveBeforeRecompile;

        private bool initialized; // Set when Initialize() is called. Throws errors if we call it twice.        

        /// <summary>
        ///     This guid stores the GlobalObjectId for this scriptable object. It is always empty in the editor except during
        ///     the build process, where it gets overwritten. In standalone this is used to read this object's guid.
        /// </summary>
        [SerializeField]
        [HideInInspector]
        public RuntimeGlobalObjectId runtimeAssetGuid;

        /// <summary>A cache to access nodes by GUID. Not serialized.</summary>
        public Dictionary<string, BaseNode> NodesPerFileId { get; } = new();

        /// <summary>
        ///     A stable guid for this graph, regardless of where it is moved in the project. Accessible at runtime and the
        ///     editor. This can be used to maintain state during hot-swaps of compilations. This must be stable between editor and
        ///     standalone, but it need not be stable between machines. It's tied to the lifetime of the loaded Lattice editor
        ///     assembly, because this value is baked into the subscene. Subscenes are re-baked whenever the lattice editor dll is
        ///     loaded.
        /// </summary>
        /// <remarks>
        ///     We currently use the asset's GUID. This is the only sensible choice that supports duplication, moving, etc,
        ///     without getting out of sync. In order to do this we have to store a runtime version of the GUID during build.
        /// </remarks>
        public Hash128 HashGuid
        {
            get
            {
#if !UNITY_EDITOR
                Assert.AreNotEqual(runtimeAssetGuid.AssetGUID, new Hash128(), "GID for Lattice Graph was not properly baked.");
                return runtimeAssetGuid.AssetGUID;
#else
                if (runtimeAssetGuid.AssetGUID != new Hash128())
                {
                    Debug.LogWarning($"GID for Lattice Graph should be empty in the editor. [{this}]");
                }
                
                if (editorHashCache.Equals(new Hash128()))
                {
                    editorHashCache = GlobalObjectId.GetGlobalObjectIdSlow(this).assetGUID;
                }
                
                Assert.AreNotEqual(editorHashCache, new Hash128(), "GID for Lattice Graph was invalid.");
                return editorHashCache;
#endif
            }
        }

        private Hash128 editorHashCache; // Cache for HashGuid in the editor because GlobalObjectId is slow.

        // OnEnable is called when the Graph is first loaded from disk, after importing. Notes:
        //  - It will be called a second time (without an OnDisable()) when the asset is reimported after disk changes are picked up.
        //  - It will *not* be called when the asset changes after undo/redo.
        protected virtual void OnEnable()
        {
            if (initialized)
            {
                // In the event this asset is re-imported from disk, OnDisable() and OnDestroy() are *not* called,
                // but this function is called a second time. This feels like a bug in Unity, but for now we can just
                // disable the object manually. 
                Debug.Log(
                    $"OnEnable() without OnDisable(), assuming asset reloaded from disk. [{GraphUtils.GetAssetPathRuntime(this)}]");

                OnDisable();
            }

#if UNITY_EDITOR
            if (SerializationUtility.HasManagedReferencesWithMissingTypes(this))
            {
                // Don't call Initialize() for a graph that has invalid references when deserializing.
                initialized = true;
                return;
            }
#endif

            Initialize();
            initialized = true;

            OnGraphChanges += SetDirtyWhenChanged;
        }

        private void SetDirtyWhenChanged(GraphChanges obj)
        {
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
#endif
        }

        protected virtual void OnDisable()
        {
            OnGraphChanges -= SetDirtyWhenChanged;

            initialized = false;
        }

        /// <summary>
        ///     Initializes nodes and edges in the graph. Among other things this generates ports for nodes and properly
        ///     connects edges.
        /// </summary>
        /// This function must be idempotent. It's called multiple times after load, such as when Undo updates the data.
        public void Initialize()
        {
            // Generates the non-serialized data from the serialized data. This gets run along with Initialize after
            // this object is modified (ie. Unity Undo, File Disk import, etc)
            // Some basic setup that can always be executed.

            if (Verbose)
            {
                Debug.Log("(BaseGraph) Initializing graph: " + GraphUtils.GetAssetPathRuntime(this));
            }

            // Per node initialization. Runs after deserialization as separate phase.
            NodesPerFileId.Clear();
            foreach (BaseNode node in nodes)
            {
                if (node == null)
                {
                    // null nodes indicate missing node serialization types.
                    continue;
                }

                node.Graph = this;
                NodesPerFileId[node.FileId] = node;

                node.Initialize();
                node.OnPropertiesChanged += OnNodePropertiesChanged;
            }

            foreach (SerializableEdge edge in edges)
            {
                edge.Deserialize();
            }

            // Actually connect the edges.
            foreach (SerializableEdge edge in NonDanglingEdges())
            {
                // Add the edge to the non-serialized port data
                edge.toPort.owner.OnEdgeConnected(edge);
                edge.fromPort.owner.OnEdgeConnected(edge);
            }

            // Anytime a graph is initialized (ie. loaded from disk), we clear the graph compilation. This makes sure
            // we don't have any old data hanging around in the compilation.
            GlobalGraph.Runtime.ClearCompilation();
            GlobalGraph.LanguageServer.ClearCompilation();

            OnGraphChanges?.Invoke(new GraphChanges
            {
                entireGraphInitialized = this
            });
        }

        /// <summary>
        ///     If the graph is malformed (fails to deserialize correctly, etc). We only partially support detecting this at
        ///     runtime. This is largely intended to be used during bake and compilation.
        /// </summary>
        public bool IsMalformed()
        {
            // This computes a bunch of extra strings, but hopefully should be optimized away.
            return GetMalformedDetails() != null;
        }

        /// <summary>If the graph is malformed (fails to deserialize correctly, etc), returns the errors causing it.</summary>
        public string GetMalformedDetails()
        {
#if UNITY_EDITOR
            if (SerializationUtility.HasManagedReferencesWithMissingTypes(this))
            {
                string text;
                text = "Missing Nodes:\n";
                foreach (ManagedReferenceMissingType missing in SerializationUtility
                             .GetManagedReferencesWithMissingTypes(this))
                {
                    string emoji = new[] { "😭", "😢", "😿", "💩", "😖" }[Random.Range(0, 4)];
                    text += $"\t{emoji}  {missing.namespaceName}.{missing.className}, {missing.assemblyName}\n";
                }

                return text;
            }
#endif

            string errors = "";
            if (nodes == null)
            {
                errors += "- Nodes list is null.";
            }
            else if (nodes.Any(n => n == null))
            {
                errors += "- Graph contains null nodes\n";
            }

            if (edges == null)
            {
                errors += "- Edges list is null.";
            }
            else
            {
                int malformedEdges = edges.Count(e => string.IsNullOrEmpty(e.FileId));
                if (malformedEdges > 0)
                {
                    errors += $"- {malformedEdges} edges are malformed. (null properties)";
                }
            }

            return errors == "" ? null : errors;
        }

        /// <summary>Adds a node to the graph</summary>
        public BaseNode AddNode(BaseNode node)
        {
            nodes.Add(node);
            NodesPerFileId[node.FileId] = node;
            node.Graph = this;
            node.Initialize();

            node.OnPropertiesChanged += OnNodePropertiesChanged;

            OnGraphChanges?.Invoke(new GraphChanges { addedNode = node });

            return node;
        }

        private void OnNodePropertiesChanged(BaseNode node)
        {
            OnGraphChanges?.Invoke(new GraphChanges { nodeChanged = node });
        }

        /// <summary>
        ///     Invoke the onGraphChanges event, can be used as trigger to execute the graph when the content of a node is
        ///     changed
        /// </summary>
        public void NotifyNodeChanged(BaseNode node)
        {
            OnGraphChanges?.Invoke(new GraphChanges { nodeChanged = node });
        }

        /// <summary>Removes a node from the graph</summary>
        public void RemoveNode(BaseNode node)
        {
            foreach (SerializableEdge e in edges)
            {
                if (e.fromNode == node || e.toNode == node)
                {
                    Disconnect(e);
                }
            }

            node.OnPropertiesChanged -= OnNodePropertiesChanged;

            nodes.Remove(node);
            NodesPerFileId.Remove(node.FileId);

            OnGraphChanges?.Invoke(new GraphChanges { removedNode = node });
        }

        /// <summary>Connect two ports with an edge.</summary>
        public SerializableEdge CreateEdge(NodePort inputPort, NodePort outputPort,
                                           bool autoDisconnectOtherInputs = true)
        {
            SerializableEdge edge = SerializableEdge.CreateNewEdge(this, outputPort, inputPort);

            //If the input port does not support multi-connection, we remove them
            if (autoDisconnectOtherInputs && !inputPort.portData.acceptMultipleEdges)
            {
                foreach (SerializableEdge e in inputPort.GetEdges().ToList())
                {
                    Disconnect(e);
                }
            }

            // same for the output port:
            if (autoDisconnectOtherInputs && !outputPort.portData.acceptMultipleEdges)
            {
                foreach (SerializableEdge e in outputPort.GetEdges().ToList())
                {
                    Disconnect(e);
                }
            }

            edges.Add(edge);

            // Add the edge to the list of connected edges in the nodes
            inputPort.owner.OnEdgeConnected(edge);
            outputPort.owner.OnEdgeConnected(edge);

            OnGraphChanges?.Invoke(new GraphChanges { addedEdge = edge });

            return edge;
        }

        /// <summary>Disconnects and removes  an edge</summary>
        public void Disconnect(SerializableEdge edge)
        {
            int removed = edges.RemoveAll(e => e == edge);
            if (removed > 1)
            {
                Debug.LogError($"Same edge was found twice in the graph. [{edge}]");
            }

            edge.fromNode.OnEdgeDisconnected(edge);
            edge.toNode.OnEdgeDisconnected(edge);
            
            OnGraphChanges?.Invoke(new GraphChanges { removedEdge = edge });
        }

        public void AddGroup(Group block)
        {
            groups.Add(block);
            OnGraphChanges?.Invoke(new GraphChanges { addedGroups = block });
        }

        public void RemoveGroup(Group block)
        {
            groups.Remove(block);
            OnGraphChanges?.Invoke(new GraphChanges { removedGroups = block });
        }

        public void AddStackNode(BaseStackNode stackNode)
        {
            stackNodes.Add(stackNode);
            OnGraphChanges?.Invoke(new GraphChanges { addedStackNode = stackNode });
        }

        public void RemoveStackNode(BaseStackNode stackNode)
        {
            stackNodes.Remove(stackNode);
            OnGraphChanges?.Invoke(new GraphChanges { removedStackNode = stackNode });
        }

        public void AddStickyNote(StickyNote note)
        {
            stickyNotes.Add(note);
            OnGraphChanges?.Invoke(new GraphChanges { addedStickyNotes = note });
        }

        public void RemoveStickyNote(StickyNote note)
        {
            stickyNotes.Remove(note);
            OnGraphChanges?.Invoke(new GraphChanges { removedStickyNotes = note });
        }

#if UNITY_EDITOR
        internal void DeleteNullNodes()
        {
            nodes.RemoveAll(n => n == null);
            EditorUtility.SetDirty(this);
        }
#endif

        /// <summary>Tell if two types can be connected in the SharedContext of a graph</summary>
        public static bool TypesAreConnectable(Type input, Type assigned)
        {
            // If either type is null, we don't have the type known for a port, so default to allowing them to connect.
            if (input == null || assigned == null)
            {
                return true;
            }

            Assert.IsNotNull(input);
            Assert.IsNotNull(assigned);

            // Basic type assignment.
            if (assigned.IsAssignableFrom(input))
            {
                return true;
            }

            // Object can be connected to any port. Like Typescript "any"
            if (input == typeof(object) || assigned == typeof(object))
            {
                return true;
            }

            // Manual override for Nullable types.
            if (input.IsGenericType && input.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return assigned == input.GetGenericArguments()[0];
            }

            return false;
        }
    }
}
