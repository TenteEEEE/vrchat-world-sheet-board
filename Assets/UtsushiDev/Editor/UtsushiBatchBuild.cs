#if UNITY_EDITOR
using System;
using UtsushiEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Utsushi;
using UnityEngine.SceneManagement;
using System.IO;

namespace UtsushiDev
{
    public static class UtsushiBatchBuild
    {
        private static bool utsushiCompileError;

        public static void RebuildAndPreview()
        {
            UtsushiDevMenu.RunBatch(RebuildAndPreviewCore);
        }

        private static void RebuildAndPreviewCore()
        {
            try
            {
                utsushiCompileError = false;
                Application.logMessageReceived += CaptureCompileError;
                try { UtsushiInstaller.BuildPrefabs(); }
                finally { Application.logMessageReceived -= CaptureCompileError; }
                if (utsushiCompileError) throw new InvalidOperationException("Utsushi UdonSharp compile error; refusing to treat previous program assets as current.");
                UtsushiPermissionMatrixCheck.RunPermissionMatrix();
                UtsushiPreview.RenderSample();
                CheckExistingSceneReferences();
            }
            finally { Application.logMessageReceived -= CaptureCompileError; }
        }

        private static void CheckExistingSceneReferences()
        {
            const string scenePath = "Assets/main.unity";
            if (!File.Exists(scenePath))
            {
                Debug.Log("[Utsushi] Existing scene reference check skipped; Assets/main.unity is not available.");
                return;
            }

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            UtsushiDisplay[] displays = UnityEngine.Object.FindObjectsOfType<UtsushiDisplay>(true);
            if (displays.Length == 0) throw new InvalidOperationException("Existing scene reference check failed: no UtsushiDisplay found.");
            int sourceCount = 0;
            foreach (UtsushiDisplay display in displays)
            {
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(display.gameObject) != 0)
                    throw new InvalidOperationException("Existing scene reference check failed: missing script on Display " + display.name + ".");
                if (display.source == null) throw new InvalidOperationException("Existing scene reference check failed: Display " + display.name + " has no Source.");
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(display.source.gameObject) != 0)
                    throw new InvalidOperationException("Existing scene reference check failed: missing script on Source " + display.source.name + ".");
                sourceCount++;
            }
            Debug.Log("[Utsushi] Existing scene references OK: displays=" + displays.Length + ", sources=" + sourceCount + ".");
        }

        private static void CaptureCompileError(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Error && message.Contains(UtsushiInstaller.RootDir + "Runtime/") && message.Contains("UdonSharp")) utsushiCompileError = true;
        }
    }
}
#endif
