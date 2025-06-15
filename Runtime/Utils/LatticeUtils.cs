using System;
using System.Collections.Generic;
using Unity.Assertions;
using Unity.Entities;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build.Content;
#endif

using UnityEngine;
using Object = UnityEngine.Object;

namespace Lattice.Utils
{
    public static class LatticeUtils
    {
        /// <summary>Whether the first system is ordered after the second. Only supports systems within the same group.</summary>
        /// <returns>Null if the system ordering between these two is not specified.</returns>
        public static bool? SystemOrderedAfter(Type system1, Type system2)
        {
            if (system1 == system2)
            {
                return false;
            }

            if (IsAfter(system1, system2) || IsBefore(system2, system1))
            {
                return true;
            }

            if (IsAfter(system2, system1) || IsBefore(system1, system2))
            {
                return false;
            }

            return null;
        }

        private static bool IsAfter(Type system, Type other)
        {
            foreach (var updateAfter in TypeManager.GetSystemAttributes(system, typeof(UpdateAfterAttribute)))
            {
                UpdateAfterAttribute target = (UpdateAfterAttribute)updateAfter;
                if (target.SystemType == other)
                {
                    return true;
                }

                if (IsAfter(target.SystemType, other))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBefore(Type system, Type other)
        {
            foreach (var updateBefore in TypeManager.GetSystemAttributes(system, typeof(UpdateBeforeAttribute)))
            {
                UpdateBeforeAttribute target = (UpdateBeforeAttribute)updateBefore;
                if (target.SystemType == other)
                {
                    return true;
                }

                if (IsBefore(target.SystemType, other))
                {
                    return true;
                }
            }

            return false;
        }

        public static Type GetSystemGroup(Type system)
        {
            var attrs = TypeManager.GetSystemAttributes(system, typeof(UpdateInGroupAttribute));
            if (attrs.Length > 0)
            {
                Type systemGroup = ((UpdateInGroupAttribute)attrs[0]).GroupType;
                Assert.IsTrue(typeof(ComponentSystemGroup).IsAssignableFrom(systemGroup), "Type has invalid UpdateInGroup attribute.");
                return systemGroup;
            }
            
            // No attributes by default places it in simulation system group.
            return typeof(SimulationSystemGroup);
        }
        
        public static List<Type> GetSystemGroupChain(Type system)
        {
            var groups = new List<Type>();
            groups.Add(system);
            
            while (system != typeof(SimulationSystemGroup))
            {
                system = GetSystemGroup(system);
                groups.Insert(0, system);
            }

            return groups;
        }
        
        public static bool TryGetFirst<T>(this List<T> list, Func<T, bool> test, out T value) {
            foreach (var x in list)
            {
                if (test(x))
                {
                    value = x;
                    return true;
                }
            }

            value = default;
            return false;
        }
        
        public static T? GetFirst<T>(this List<T> list, Func<T, bool> test) where T : struct {
            foreach (var x in list)
            {
                if (test(x))
                {
                    return x;
                }
            }

            return null;
        }
        
        // The golden ratio conjugate (approximately 0.61803).
        private const float GoldenRatioConjugate = 0.6180339887f;

        /// <summary>
        /// Generates a string representing a color in hex format (#RRGGBB)
        /// suitable for Graphviz DOT.
        /// </summary>
        /// <param name="n">The zero-based index of the color to generate.</param>
        /// <returns>A string like "#FF7F00".</returns>
        public static string GetDotColorByIndex(int n)
        {
            // 1) Compute the hue by multiplying the index by the golden ratio conjugate.
            float hue = (n * GoldenRatioConjugate) % 1f;

            // 2) Convert HSV -> RGB. Use full saturation and value for bright, distinct colors.
            Color color = Color.HSVToRGB(hue, 1f, 1f);

            // 3) Convert to #RRGGBB string for DOT:
            //    - Multiply r,g,b by 255.
            //    - Format each as a two-digit hexadecimal.
            int r = Mathf.RoundToInt(color.r * 255);
            int g = Mathf.RoundToInt(color.g * 255);
            int b = Mathf.RoundToInt(color.b * 255);

            return $"#{r:X2}{g:X2}{b:X2}";
        }

        /// <summary>
        /// Returns an infinite sequence of DOT colors as #RRGGBB strings.
        /// </summary>
        public static IEnumerable<string> GenerateInfiniteDotColors()
        {
            int i = 0;
            while (true)
            {
                yield return GetDotColorByIndex(i);
                i++;
            }
        }
        
        /// <summary>
        ///     Returns the best path we can find for this UnityEngine.Object. If it's a gameobject, or a component, we will
        ///     use the scene hierarchy path. If it's anything else, we just use the name and type.
        /// </summary>
        /// <returns>A string in the form "(Scene)/rootobject/someobject/otherobject/thisobject"</returns>
        public static string GetPathString(this Object obj)
        {
            if (obj == null)
            {
                return "null-reference";
            }

            switch (obj)
            {
                case Component comp:
                    if (comp.gameObject == null)
                    {
                        return $"null-gameobject:{comp.GetType().Name}";
                    }

                    return $"{comp.gameObject.GetPath()}:{comp.GetType().Name}";
                case GameObject gObj:
                    return gObj.gameObject.GetPath();
                default:
                    string objectName = obj.ToString();

                    // In the editor, check to see if this object is part of an asset, and return that string instead.
#if UNITY_EDITOR
                    string path = AssetDatabase.GetAssetPath(obj);
                    if (path != null)
                    {
#if UNITY_EDITOR
                        // Returns empty if the asset is a sub-asset on another.
                        if (string.IsNullOrEmpty(path))
                        {
                            ObjectIdentifier.TryGetObjectIdentifier(obj, out ObjectIdentifier ident);
                            path = ident.filePath;
                            return $"(Asset)/{path}/{obj.name}";
                        }
#endif

                        path = path.Replace("Assets/", "");
                        return $"(Asset)/{path}/{objectName}";
                    }
#endif

                    return objectName;
            }
        }

        /// <summary>Returns the full path to this transform including parents and the name of this gameobject.</summary>
        /// <returns>A string in the form "rootobject/someobject/otherobject/thisobject"</returns>
        public static string GetPath(this Transform transform)
        {
            if (transform == null)
            {
                return "???/(null-transform)";
            }

            // If this is the root of the GameObject hierarchy.
            if (transform.parent == null)
            {
                // Check if this is part of a prefab.
                string scene = transform.gameObject.scene.name; // prefabs have null scenes
                if (scene == null)
                {
#if UNITY_EDITOR
                    string assetPath = AssetDatabase.GetAssetPath(transform).Replace("Assets/", "");
                    if (!AssetDatabase.IsSubAsset(transform.gameObject))
                    {
                        // GameObject is the main object of an asset.
                        // Skip the root transform name on the prefab for conciseness.
                        return $"(Prefab)/{assetPath}"; 
                    } else {
                        // GameObject is a sub-asset of another asset.
                        // Add the root gameobject name to disambiguate.
                        return $"(Prefab)/{assetPath}/{transform.gameObject.name}"; 
                    }
#else
                    // At runtime we don't know the asset path, so just show the name. 
                    return $"(Runtime Prefab)/{transform.gameObject.name}";
#endif
                }

                return $"({scene})/{transform.name}";
            }

            return transform.parent.GetPath() + "/" + transform.name;
        }

        /// <summary>Returns the full path to this transform including parents and the name of this gameobject.</summary>
        /// <returns>A string in the form "rootobject/someobject/otherobject/thisobject"</returns>
        public static string GetPath(this GameObject gObj)
        {
            if (gObj == null)
            {
                return "null-reference";
            }

            return gObj.transform.GetPath();
        }

        /// <summary>Returns the relative path from the parent transform to the child.</summary>
        /// <returns>A string in the form "rootobject/someobject/otherobject/thisobject"</returns>
        public static string GetRelativePath(Transform parent, Transform child)
        {
            if (parent == child)
            {
                return "";
            }

            Assert.IsTrue(child.IsChildOf(parent));

            if (child.parent == parent)
            {
                return child.name;
            }

            return GetRelativePath(parent, child.parent) + "/" + child.name;
        }

        /// <summary>Gets the names of the hierarchy objects above and including this object.</summary>
        public static void GetHierarchyPath(this Transform transform, List<string> outputPath)
        {
            Transform t = transform;
            while (t != null)
            {
                outputPath.Add(t.name);
                t = t.parent;
            }

            outputPath.Reverse();
        }
    }
}
