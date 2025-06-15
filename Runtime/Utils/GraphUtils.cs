using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Lattice.Base;
using Lattice.IR;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Lattice.Utils
{
    public static class GraphUtils
    {
        public static object GetDefaultForType(Type type)
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }
            return null;
        }

        /// <summary>
        /// Returns the name of the input <paramref name="type"/>, preferring type keywords.<br/>
        /// Also fixes up array names to use [], and generics to use &lt;&gt;.
        /// </summary>
        public static string GetReadableTypeName([CanBeNull] Type type)
        {
            if (type == null)
                return "";

            if (type == typeof(bool))
                return "bool";

            if (type == typeof(sbyte))
                return "sbyte";

            if (type == typeof(short))
                return "short";

            if (type == typeof(int))
                return "int";

            if (type == typeof(long))
                return "long";

            if (type == typeof(float))
                return "float";

            if (type == typeof(double))
                return "double";

            if (type == typeof(decimal))
                return "decimal";

            if (type == typeof(char))
                return "char";

            if (type == typeof(string))
                return "string";

            if (type == typeof(byte))
                return "byte";

            if (type == typeof(ushort))
                return "ushort";

            if (type == typeof(uint))
                return "uint";

            if (type == typeof(ulong))
                return "ulong";

            if (type.IsArray)
                return GetReadableTypeName(type.GetElementType()) + "[]";

            if (type.IsGenericType)
            {
                string genericArgs =
                    string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), GetReadableTypeName));
                if (typeof(ITuple).IsAssignableFrom(type))
                {
                    return $"({genericArgs})";
                }

                int genericMarkerIndex = type.Name.IndexOf('`');
                string genericTypeName = genericMarkerIndex < 0 ? type.Name : type.Name[..genericMarkerIndex];
                return $"{genericTypeName}<{genericArgs}>";
            }

            return type.Name;
        }

        /// <summary>Returns a <see cref="GetReadableTypeName"/> string with rich text highlighting.</summary>
        public static string GetReadableTypeNameWithColor([CanBeNull] Type type)
        {
            string name = GetReadableTypeName(type);

            if (type == null || type.IsPrimitive || type == typeof(string))
            {
                // Primitive, string, or null
                return $"<color=#6C95EB>{name}</color>";
            }

            if (type.IsValueType)
            {
                if (type.IsNullable())
                {
                    // Nullable
                    return
                        $"{GetReadableTypeNameWithColor(type.GetGenericArguments()[0])}<color=white><b>?</b></color>";
                }

                // Struct, or a value type like Enum.
                return $"<color=#E1BFFF>{name}</color>";
            }

            if (type == typeof(ITypeUnknown))
            {
                return "<color=#D32222>Unknown</color>";
            }

            // Class
            return $"<color=#C191FF>{name}</color>";
        }

        /// <summary>Returns an identifier string with consistent formatting.</summary>
        public static string NicifyIdentifierName(string identifier)
        {
            if (identifier.EndsWith("_in", StringComparison.Ordinal))
            {
                identifier = identifier[..^"_in".Length];
            }
            else if (identifier.EndsWith("_out", StringComparison.Ordinal))
            {
                identifier = identifier[..^"_out".Length];
            }

            // Force the identifier to Pascal case.
            if (identifier.Length >= 1 && char.IsLower(identifier[0]))
            {
                identifier = $"{char.ToUpper(identifier[0])}{identifier[1..]}";
            }
            return identifier;
        }

        /// <summary>Returns a rich-text formatted type and identifier string.</summary>
        public static string GetFormattedTypeNameWithIdentifierWithColor(
            [CanBeNull] Type type, [CanBeNull] string identifier)
        {
            if (type == null)
            {
                return identifier == null
                    ? ""
                    : NicifyIdentifierName(identifier);
            }
            return identifier == null
                ? GetReadableTypeNameWithColor(type)
                : $"{GetReadableTypeNameWithColor(type)} {NicifyIdentifierName(identifier)}";
        }

        public static bool HasCustomAttribute<T>(this ParameterInfo parameter) where T : Attribute
        {
            return parameter.GetCustomAttribute<T>() != null;
        }

        /// <summary>A debug string for a UnityEngine object, suitable for edit time or runtime.</summary>
        public static string GetAssetPathRuntime(Object obj)
        {
#if UNITY_EDITOR
            return AssetDatabase.GetAssetPath(obj);
#else
            return $"{obj.GetType().Name}:{obj.name}";
#endif
        }

#if UNITY_EDITOR
        public static void OpenGraphviz(string dot)
        {
            string encodedDotString = UnityWebRequest.EscapeURL(dot).Replace("+", "%20");
            string url = $"https://dreampuf.github.io/GraphvizOnline/#{encodedDotString}";

            // Create temporary HTML file. The url generated here is too big to fit in a Win32 command line argument,
            // so we have to save it into a temp file and use a redirector to get the browser to load the page. 
            string tempHtmlPath = FileUtil.GetUniqueTempPathInProject() + "redirect.html";

            using (StreamWriter writer = new(tempHtmlPath))
            {
                writer.WriteLine("<!DOCTYPE html>");
                writer.WriteLine("<html>");
                writer.WriteLine("<head>");
                writer.WriteLine($"<meta http-equiv=\"refresh\" content=\"0; url={url}\" />");
                writer.WriteLine("<title>Redirecting...</title>");
                writer.WriteLine("</head>");
                writer.WriteLine("<body>");
                writer.WriteLine($"If you are not redirected automatically, please click <a href=\"{url}\">here</a>");
                writer.WriteLine("</body>");
                writer.WriteLine("</html>");
            }

            // Open temporary file with default browser
            EditorUtility.OpenWithDefaultApp(tempHtmlPath);
        }

        public static void OpenGraphviz(GraphCompilation graph)
        {
            var dotString = GraphCompilation.ToDot(graph, graph.Graph.Nodes);
            OpenGraphviz(dotString);
        }

        public static void OpenGraphviz(GraphCompilation graph, IEnumerable<IRNode> nodes)
        {
            var dotString = GraphCompilation.ToDot(graph, nodes.ToList());
            OpenGraphviz(dotString);
        }

        public static void OpenGraphvizWithDeps(GraphCompilation graph, IEnumerable<IRNode> nodes)
        {
            // Gather the dependencies of the nodes, and add them to the list of nodes to visualize.
            var allNodes = new HashSet<IRNode>(nodes);
            foreach (var node in nodes)
            {
                foreach (var dep in graph.GetAllDependencies(node))
                {
                    allNodes.Add(dep);
                }
            }
            var dotString = GraphCompilation.ToDot(graph, allNodes.ToList());
            OpenGraphviz(dotString);
        }
#endif
    }
}
