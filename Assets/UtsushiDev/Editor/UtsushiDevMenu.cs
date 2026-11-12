#if UNITY_EDITOR
using System;
using UtsushiEditor;
using UnityEditor;
using UnityEngine;

namespace UtsushiDev
{
    public static class UtsushiDevMenu
    {
        [MenuItem("Tools/TenteEEEE/Utsushi (Dev)/Build Prefabs")]
        public static void BuildPrefabs() { UtsushiInstaller.BuildPrefabs(); }

        [MenuItem("Tools/TenteEEEE/Utsushi (Dev)/Export UnityPackage")]
        public static void ExportUnityPackage() { UtsushiInstaller.ExportUnityPackage(); }

        [MenuItem("Tools/TenteEEEE/Utsushi (Dev)/Build Prefabs", true)]
        [MenuItem("Tools/TenteEEEE/Utsushi (Dev)/Export UnityPackage", true)]
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
