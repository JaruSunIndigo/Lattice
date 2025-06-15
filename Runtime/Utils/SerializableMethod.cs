using System;
using System.Reflection;
using JetBrains.Annotations;
using Unity.Entities.UniversalDelegates;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Serialization;

namespace Lattice.Utils
{
    /// <summary>A unity serializable version of MethodInfo.</summary>
    [Serializable]
    public class SerializableMethodInfo
    {
        public string GetMethodName() => MethodName;
        
        [FormerlySerializedAs("serializedType")]
        [SerializeField]
        private string AssemblyQualifiedTypeName;

        [FormerlySerializedAs("methodName")]
        [SerializeField]
        private string MethodName;

        [SerializeField]
        private BindingFlags Binding;

        [NonSerialized]
        private MethodInfo info;
        

        public SerializableMethodInfo(MethodInfo info, BindingFlags flags)
        {
            Assert.IsNotNull(info, "MethodInfo cannot be null");
            this.info = info;
            Binding = flags;
            MethodName = this.info.Name;
            AssemblyQualifiedTypeName = this.info.DeclaringType!.AssemblyQualifiedName;
        }

        public string Name() => MethodName;

        public MethodInfo Resolve()
        {
            if (info != null)
            {
                return info;
            }

            if (string.IsNullOrEmpty(AssemblyQualifiedTypeName) || string.IsNullOrEmpty(MethodName))
            {
                throw new Exception($"Invalid MethodInfo definition. Missing name or assembly. [{this}]");
            }

            Type declaringType = Type.GetType(AssemblyQualifiedTypeName);

            if (declaringType == null)
            {
                throw new Exception(
                    $"Could not resolve C# function [{MethodName}], type not found: [{AssemblyQualifiedTypeName}]");
            }

            info = declaringType.GetMethod(MethodName, Binding);

            if (info == null)
            {
                throw new Exception($"Could not find C# function [{MethodName}] on type [{AssemblyQualifiedTypeName}]");
            }

            return info;
        }

        /// <summary>Returns false, if the method cannot be loaded from the assembly.</summary>
        public bool Exists()
        {
            try
            {
                MethodInfo resolve = Resolve();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool IsValid()
        {
            return !string.IsNullOrEmpty(MethodName) && !string.IsNullOrEmpty(AssemblyQualifiedTypeName);
        }

        public override string ToString()
        {
            return $"{MethodName}, {AssemblyQualifiedTypeName}"; // todo: rendering bindings
        }

        protected bool Equals(SerializableMethodInfo other)
        {
            return AssemblyQualifiedTypeName == other.AssemblyQualifiedTypeName && MethodName == other.MethodName &&
                   Binding == other.Binding;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }
            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            if (obj.GetType() != this.GetType())
            {
                return false;
            }
            return Equals((SerializableMethodInfo)obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return HashCode.Combine(AssemblyQualifiedTypeName, MethodName, (int)Binding);
        }

        public static bool operator ==(SerializableMethodInfo left, SerializableMethodInfo right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(SerializableMethodInfo left, SerializableMethodInfo right)
        {
            return !Equals(left, right);
        }
    }
}
