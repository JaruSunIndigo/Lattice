using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Lattice.Utils
{
    [Serializable]
    public class SerializableType : ISerializationCallbackReceiver
    {
        private static Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
        private static Dictionary<Type, string> typeNameCache = new Dictionary<Type, string>();

        [SerializeField] public string serializedType;

        [NonSerialized] public Type type;

        /// <summary>If there's a type stored here, but it couldn't be resolved on deserialization.</summary>
        public bool IsMissing()
        {
            return type == null && !string.IsNullOrEmpty(serializedType);
        }

        public SerializableType(Type t)
        {
            type = t;
        }

        public static implicit operator SerializableType(Type t)
        {
            return new SerializableType(t);
        }

        public void OnAfterDeserialize()
        {
            if (!string.IsNullOrEmpty(serializedType))
            {
                if (!typeCache.TryGetValue(serializedType, out type))
                {
                    type = Type.GetType(serializedType);
                    if (type == null)
                    {
                        Debug.LogError($"Couldn't find type [{serializedType}]");
                    }
                    else
                    {
                        typeCache[serializedType] = type;
                    }
                }
            }
        }

        public void OnBeforeSerialize()
        {
            if (type != null)
            {
                if (!typeNameCache.TryGetValue(type, out serializedType))
                {
                    serializedType = type.AssemblyQualifiedName;
                    typeNameCache[type] = serializedType;
                }
            }
        }

        public (string assembly, string namespaceName, string className) ParseParts()
        {
            if (string.IsNullOrEmpty(serializedType))
            {
                // todo: probably want to wrap calls to this in a try/catch.
                throw new Exception("Serialized type is null or empty.");
            }

            var parsed = TypeNameParser.Parse(serializedType);
            if (parsed == null) {
                throw new Exception($"Serialized type is malformed. [{serializedType}]");
            }

            return parsed.Value;
        }

        /// <summary>
        /// Provides static methods for parsing assembly-qualified type names.
        /// </summary>
        public static class TypeNameParser
        {
            /// <summary>
            /// Parses a potentially assembly-qualified type name string into its components.
            /// Handles namespaces, nested types (+), generics (`), arrays ([]), and
            /// assembly-qualified generic type arguments.
            /// </summary>
            /// <param name="assemblyQualifiedName">The assembly-qualified type name string.</param>
            /// <returns>A ParsedTypeName object with the components, or null if the input is null, whitespace, or fundamentally invalid (e.g., completely unbalanced brackets before assembly delimiter).</returns>
            public static (string assemblyName, string namespaceName, string className)? Parse(string assemblyQualifiedName)
            {
                if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
                {
                    return null; // Or throw ArgumentNullException
                }

                // Find the index of the comma that separates the Type Name from the Assembly Name.
                // This comma must be outside any balanced square brackets [].
                int? assemblyDelimiterIndex = FindAssemblyDelimiterIndex(assemblyQualifiedName);

                string typeNamePart;
                string assemblyNamePart = null;

                if (assemblyDelimiterIndex.HasValue)
                {
                    // Potential Type name exists before the delimiter
                    typeNamePart = assemblyQualifiedName.Substring(0, assemblyDelimiterIndex.Value).Trim();

                    // Ensure there's something after the delimiter for the Assembly name
                    if (assemblyDelimiterIndex.Value + 1 < assemblyQualifiedName.Length)
                    {
                        assemblyNamePart = assemblyQualifiedName.Substring(assemblyDelimiterIndex.Value + 1).Trim();
                        if (string.IsNullOrEmpty(assemblyNamePart))
                        {
                            assemblyNamePart = null; // Treat empty or whitespace-only assembly part as null
                        }
                    }
                    // Else: Comma was the last character, technically invalid AQN but we treat as no assembly name.
                }
                else
                {
                    // No assembly delimiter found, the entire string is the type name part.
                    typeNamePart = assemblyQualifiedName.Trim();
                }

                // If the type name part ended up empty (e.g., input was just ", AssemblyName"), it's invalid.
                if (string.IsNullOrEmpty(typeNamePart))
                {
                    return null; // Or throw FormatException
                }

                // Now parse the typeNamePart into Namespace and ClassName
                string namespaceName = string.Empty; // Default to no namespace
                string className;

                // Find the last dot (.) that is NOT part of a nested type (+) or generic definition (` or [])
                // The easiest way is to find the last dot and check if it comes before any special chars.
                int lastDotIndex = typeNamePart.LastIndexOf('.');
                int firstSpecialCharIndex = typeNamePart.IndexOfAny(new[] { '+', '`', '[' });

                // A dot is considered a namespace separator if it exists AND
                // (there are no special chars OR the dot appears before the first special char)
                if (lastDotIndex != -1 && (firstSpecialCharIndex == -1 || lastDotIndex < firstSpecialCharIndex))
                {
                    namespaceName = typeNamePart.Substring(0, lastDotIndex);
                    className = typeNamePart.Substring(lastDotIndex + 1);
                }
                else
                {
                    // No namespace detected (or dot is part of a nested type name which is unlikely here)
                    // The entire typeNamePart is the ClassName
                    className = typeNamePart;
                }

                // Basic validation: className should not be empty here if typeNamePart wasn't.
                if (string.IsNullOrEmpty(className))
                {
                    // This case implies the type name ended with a dot, e.g. "MyNamespace." which is invalid.
                    return null; // Or throw FormatException
                }

                return (
                    assemblyNamePart,
                    namespaceName,
                    className
                );
            }

            /// <summary>
            /// Finds the index of the comma separating the type name from the assembly name.
            /// Returns null if no such comma is found at nesting level 0.
            /// Also returns null if bracket mismatch is detected *before* a potential delimiter.
            /// </summary>
            private static int? FindAssemblyDelimiterIndex(string name)
            {
                int bracketLevel = 0;
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (c == '[')
                    {
                        bracketLevel++;
                    }
                    else if (c == ']')
                    {
                        bracketLevel--;
                        if (bracketLevel < 0)
                        {
                            // Unbalanced brackets detected before finding a potential top-level comma.
                            // This implies the type name itself is malformed.
                            return null; // Indicate failure due to malformed type name part.
                        }
                    }
                    else if (c == ',' && bracketLevel == 0)
                    {
                        // This is the first comma encountered outside of any brackets.
                        return i;
                    }
                }

                // If we finish the loop and the bracket level isn't zero, the input is malformed.
                if (bracketLevel != 0)
                {
                    return null; // Indicate failure due to overall unbalanced brackets.
                }

                // No comma found at the top level.
                return null;
            }

        }
    }
}
