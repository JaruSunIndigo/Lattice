#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;

namespace Lattice.Editor.Utils
{
    public static class SourceUtility
    {
        /// <summary>Opens the script editor at the declaration of the input <paramref name="method"/>.</summary>
        public static void OpenAtMethod(MethodInfo method)
        {
            (string filePath, int lineNumber) = GetMethodSourceInfo(method);
            // Open the file at the method location.
            InternalEditorUtility.OpenFileAtLineExternal(filePath, lineNumber);
        }

        /// <summary>Returns a Console Window-ready string that links to the input <paramref name="method"/>.</summary>
        public static string GetMethodSourceInfoFormattedAsLink(MethodInfo method)
        {
            (string filePath, int lineNumber) = GetMethodSourceInfo(method);
            return $"<color={(EditorGUIUtility.isProSkin ? "#40a0ff" : "#0000FF")}><link=\"href='{filePath}' line='{lineNumber}'\">{filePath.Replace('\\', '/')}:{lineNumber}</link></color>";
        }
        
        /// <summary>Returns the file path and line number of the declaration of the input <paramref name="method"/>.</summary>
        public static (string filePath, int lineNumber) GetMethodSourceInfo(MethodInfo method)
        {
            // Instantiate and call UnityEditor.MonoCecilHelper.TryGetCecilFileOpenInfo, which returns a UnityEditor.FileOpenInfo.
            Type monoCecilHelperType = Type.GetType("UnityEditor.MonoCecilHelper,UnityEditor")!;
            MethodInfo tryGetCecilFileOpenInfo = monoCecilHelperType.GetMethod("TryGetCecilFileOpenInfo", BindingFlags.Public | BindingFlags.Instance)!;
            object fileOpenInfo = tryGetCecilFileOpenInfo.Invoke(
                Activator.CreateInstance(monoCecilHelperType),
                new object[] { method.DeclaringType, method }
            );

            // Extract the file path and line number from the result.
            Type fileOpenInfoType = Type.GetType("UnityEditor.FileOpenInfo,UnityEditor")!;
            PropertyInfo filePathProperty = fileOpenInfoType.GetProperty("FilePath", BindingFlags.Public | BindingFlags.Instance)!;
            PropertyInfo lineNumberProperty = fileOpenInfoType.GetProperty("LineNumber", BindingFlags.Public | BindingFlags.Instance)!;
            var filePath = (string)filePathProperty.GetValue(fileOpenInfo);
            var lineNumber = (int)lineNumberProperty.GetValue(fileOpenInfo);
            return (filePath, lineNumber);
        }
    }
}
#endif