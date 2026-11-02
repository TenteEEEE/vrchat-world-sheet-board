#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using SheetBoard;
using SheetBoardEditor;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Udon;

namespace SheetBoardDevEditor
{
    public static class SheetBoardPreview
    {
        public static void RenderSampleBatch()
        {
            int code = 0;
            try { Render(); }
            catch (Exception e) { code = 1; Debug.LogException(e); }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
        private static void Render()
        {
            VerifyWiring();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SheetBoardInstaller.DisplayPrefab);
            if (prefab == null) throw new InvalidOperationException("Build the SheetBoard Display prefab first.");
            GameObject board = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            SheetBoardDisplay display = board.GetComponent<SheetBoardDisplay>();
            if (display == null) throw new InvalidOperationException("Display prefab has no SheetBoardDisplay.");
            GameObject sourceObject = new GameObject("Preview Source");
            SheetBoardSource source = sourceObject.AddUdonSharpComponent<SheetBoardSource>();
            display.source = source;
            string json = File.ReadAllText("Assets/SheetBoardDev/Samples/sample.json", Encoding.UTF8);
            if (!source.ApplyJson(json)) throw new InvalidOperationException("Invalid sample JSON.");
            // A broken download must be rejected without dropping the data already shown.
            if (source.ApplyJson("{broken") || source.ApplyJson("{\"format\":\"other\"}") || source.GetRange("schedule") == null || source.GetRevision() != 1)
                throw new InvalidOperationException("Source did not keep the last good data after invalid JSON.");
            if (!source.ApplyJson(json) || source.GetRevision() != 1)
                throw new InvalidOperationException("Re-applying the same rev must not bump the revision.");
            display.rangeName = "";
            Canvas.ForceUpdateCanvases();
            display.RenderNow();
            if (display.titleText.text != "今週のイベント予定")
                throw new InvalidOperationException("Empty rangeName did not render the first range.");
            GameObject cameraObject = new GameObject("Preview Camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 0.5f;
            camera.aspect = 1.6f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(0x0A, 0x0D, 0x12, 0xFF);
            camera.transform.position = board.transform.position + new Vector3(0, 0, -2);
            camera.transform.rotation = Quaternion.identity;
            RenderTexture renderTexture = new RenderTexture(1600, 1000, 24);
            Texture2D image = new Texture2D(1600, 1000, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                string[] names = { "schedule", "notice", "missing" };
                string[] files = { "SheetBoardPreview.png", "SheetBoardPreview_notice.png", "SheetBoardPreview_missing.png" };
                // Unity wipes Temp/ on exit, so batch output goes to Logs/ instead.
                string temp = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
                Directory.CreateDirectory(temp);
                for (int i = 0; i < names.Length; i++)
                {
                    RenderRange(display, source, names[i], Path.Combine(temp, files[i]), camera, renderTexture, image);
                }
                string fidelityPath = Path.Combine(Path.GetDirectoryName("Assets/SheetBoardDev/Samples/sample.json"), "sample_fidelity.json");
                string fidelityJson = File.ReadAllText(fidelityPath, Encoding.UTF8);
                string[] ranges = { "event", "wraptest" };
                string[] prefabPaths = { SheetBoardInstaller.DisplayPrefab, SheetBoardInstaller.DisplayDarkPrefab };
                string[] themes = { "light", "dark" };
                for (int r = 0; r < ranges.Length; r++)
                {
                    for (int t = 0; t < prefabPaths.Length; t++)
                    {
                        UnityEngine.Object.DestroyImmediate(board);
                        UnityEngine.Object.DestroyImmediate(sourceObject);
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPaths[t]);
                        if (prefab == null) throw new InvalidOperationException("Missing display prefab: " + prefabPaths[t]);
                        board = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                        display = board.GetComponent<SheetBoardDisplay>();
                        if (display == null) throw new InvalidOperationException("Display prefab has no SheetBoardDisplay: " + prefabPaths[t]);
                        sourceObject = new GameObject("Preview Source");
                        source = sourceObject.AddUdonSharpComponent<SheetBoardSource>();
                        display.source = source;
                        if (!source.ApplyJson(fidelityJson) || source.GetRange(ranges[r]) == null)
                            throw new InvalidOperationException("Invalid fidelity sample or missing range: " + ranges[r]);
                        string file = "SheetBoardPreview_" + ranges[r] + "_" + themes[t] + ".png";
                        RenderRange(display, source, ranges[r], Path.Combine(temp, file), camera, renderTexture, image);
                    }
                }
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
                UnityEngine.Object.DestroyImmediate(image);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
        private static void VerifyWiring()
        {
            string[] paths = { SheetBoardInstaller.DisplayPrefab, SheetBoardInstaller.DisplayDarkPrefab };
            string[] fields = { "background", "titleText", "statusText", "tableArea", "columnTemplate", "overlayTemplate", "overlayNoWrapTemplate", "cellTemplate", "statusDot" };
            for (int i = 0; i < paths.Length; i++)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                if (prefab == null) throw new InvalidOperationException("Missing display prefab: " + paths[i]);
                GameObject board = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                SheetBoardDisplay display = board.GetComponent<SheetBoardDisplay>();
                if (display == null) throw new InvalidOperationException("Display prefab has no SheetBoardDisplay: " + paths[i]);
                var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(display);
                if (udon == null) throw new InvalidOperationException("Display has no backing Udon behaviour: " + paths[i]);
                foreach (string field in fields)
                    if (!(UdonValue(udon, field) is UnityEngine.Object value) || value == null)
                        throw new InvalidOperationException("Missing Udon variable " + field + ": " + paths[i]);
                if (!(UdonValue(udon, "lightTheme") is bool theme) || theme != (i == 0))
                    throw new InvalidOperationException("Incorrect Udon lightTheme: " + paths[i]);
                if (!(UdonValue(udon, "textScale") is float scale) || Mathf.Abs(scale - 1.4f) > 0.0001f)
                    throw new InvalidOperationException("Incorrect Udon textScale: " + paths[i]);
                GameObject sourceObject = new GameObject("Preview Source");
                display.source = sourceObject.AddUdonSharpComponent<SheetBoardSource>();
                display.RenderNow();
                if (display.titleText.text != "")
                    throw new InvalidOperationException("Title must be empty before data is received: " + paths[i]);
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            SheetBoardInstaller.InstallSampleMenu();
            SheetBoardDisplay installed = UnityEngine.Object.FindFirstObjectByType<SheetBoardDisplay>();
            if (installed == null) throw new InvalidOperationException("Install Sample did not create a display.");
            var installedUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(installed);
            if (installedUdon == null || !(UdonValue(installedUdon, "source") is UnityEngine.Object source) || source == null)
                throw new InvalidOperationException("Install Sample did not wire the Udon source.");
            Debug.Log("[SheetBoard] Wiring OK");
        }
        private static object UdonValue(UdonBehaviour udon, string name)
        {
            return udon.publicVariables.TryGetVariableValue(name, out object value) ? value : null;
        }
        private static void RenderRange(SheetBoardDisplay display, SheetBoardSource source, string range, string path, Camera camera, RenderTexture renderTexture, Texture2D image)
        {
            display.rangeName = range;
            Canvas.ForceUpdateCanvases();
            display.RenderNow();
            // _Watch drives the status line at runtime; it schedules Udon events, so mirror it here.
            display.statusText.text = source.GetStatusText();
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = renderTexture;
            image.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
            Debug.Log("[SheetBoard] Preview: " + path);
        }

    }
}
#endif
