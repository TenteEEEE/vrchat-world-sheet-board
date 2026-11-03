#if UNITY_EDITOR
using System;
using SheetBoardEditor;
using UnityEditor;
using UnityEngine;

namespace SheetBoardDev
{
    public static class SheetBoardDevMenu
    {
        [MenuItem("Tools/TenteEEEE/Sheet Board (Dev)/Build Prefabs")]
        public static void BuildPrefabs() { SheetBoardInstaller.BuildPrefabs(); }

        [MenuItem("Tools/TenteEEEE/Sheet Board (Dev)/Export UnityPackage")]
        public static void ExportUnityPackage() { SheetBoardInstaller.ExportUnityPackage(); }

        [MenuItem("Tools/TenteEEEE/Sheet Board (Dev)/Build Prefabs", true)]
        [MenuItem("Tools/TenteEEEE/Sheet Board (Dev)/Export UnityPackage", true)]
        private static bool ValidateMenu() { return !EditorApplication.isPlayingOrWillChangePlaymode; }

        public static void BuildPrefabsBatch() { RunBatch(BuildPrefabs); }
        public static void ExportUnityPackageBatch() { RunBatch(ExportUnityPackage); }

        internal static void RunBatch(Action action)
        {
            int code = 0;
            try { action(); }
            catch (Exception e) { code = 1; Debug.LogException(e); }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
#endif
