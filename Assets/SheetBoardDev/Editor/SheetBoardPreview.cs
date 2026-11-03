#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using SheetBoard;
using SheetBoardEditor;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Udon;

namespace SheetBoardDev
{
    public static class SheetBoardPreview
    {
        public static void RenderSampleBatch()
        {
            SheetBoardDevMenu.RunBatch(Render);
        }
        internal static void RenderSample()
        {
            Render();
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
            VerifyBusyIndicator(source);
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
                RenderBusyRange(display, source, Path.Combine(temp, "SheetBoardPreview_busy.png"), camera, renderTexture, image);
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
            string[] fields = { "background", "titleText", "statusText", "tableArea", "columnTemplate", "overlayTemplate", "overlayNoWrapTemplate", "cellTemplate", "statusDot", "refreshButton", "refreshLabel" };
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            GameObject sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(SheetBoardInstaller.SourcePrefab);
            if (sourcePrefab == null) throw new InvalidOperationException("Missing Source prefab.");
            GameObject sourceObject = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
            try
            {
                SheetBoardSource wiringSource = sourceObject.GetComponent<SheetBoardSource>();
                if (wiringSource == null) throw new InvalidOperationException("Source prefab has no SheetBoardSource.");
                var sourceUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(wiringSource);
                if (sourceUdon == null) throw new InvalidOperationException("Source prefab has no backing Udon behaviour.");
                var sourceProgramAsset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(wiringSource);
                var sourceProgram = sourceProgramAsset == null ? null : sourceProgramAsset.GetRealProgram();
                if (sourceProgram == null) throw new InvalidOperationException("Source Udon program has not compiled.");
                bool hasAudience = sourceProgram.SymbolTable.HasAddressForSymbol("audience");
                bool hasController = sourceProgram.SymbolTable.HasAddressForSymbol("permissionController");
                if (!hasAudience || !hasController)
                    throw new InvalidOperationException("Source Udon variables are missing: audience=" + hasAudience + ", permissionController=" + hasController + ".");
            }
            finally { UnityEngine.Object.DestroyImmediate(sourceObject); }
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
        private static void VerifyBusyIndicator(SheetBoardSource source)
        {
            bool oldInFlight = (bool)PrivateValue(source, "inFlight");
            bool oldManualPending = (bool)PrivateValue(source, "manualPending");
            bool oldHasManual = (bool)PrivateValue(source, "hasManual");
            int oldValidUrlCount = (int)PrivateValue(source, "validUrlCount");
            try
            {
                source.RequestRefresh();
                if (source.IsBusy()) throw new InvalidOperationException("RequestRefresh without URLs left the source busy.");
                PrivateSet(source, "validUrlCount", 1);
                PrivateSet(source, "inFlight", true);
                PrivateInvoke(source, "UpdateStatus");
                if (!source.IsBusy() || source.GetStatusLevel() != 0 || !source.GetStatusText().EndsWith(source.refreshingSuffix, StringComparison.Ordinal))
                    throw new InvalidOperationException("In-flight refresh did not preserve status level and append its suffix.");
                PrivateSet(source, "inFlight", false);
                PrivateInvoke(source, "UpdateStatus");
                if (source.GetStatusText().EndsWith(source.refreshingSuffix, StringComparison.Ordinal))
                    throw new InvalidOperationException("Idle source retained the refreshing suffix.");
                source.RequestRefresh();
                if (!source.IsBusy() || source.GetRefreshWaitSeconds() == 0f)
                    throw new InvalidOperationException("Accepted manual refresh was not busy or did not start its cooldown.");
                Debug.Log("[SheetBoard] Busy indicator OK");
            }
            finally
            {
                PrivateSet(source, "manualPending", oldManualPending);
                PrivateSet(source, "hasManual", oldHasManual);
                PrivateSet(source, "validUrlCount", oldValidUrlCount);
                PrivateSet(source, "inFlight", oldInFlight);
                PrivateInvoke(source, "UpdateStatus");
            }
        }
        private static object PrivateValue(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing private field: " + name);
            return field.GetValue(target);
        }
        private static void PrivateSet(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing private field: " + name);
            field.SetValue(target, value);
        }
        private static void PrivateInvoke(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Missing private method: " + name);
            method.Invoke(target, arguments);
        }
        private static void RenderBusyRange(SheetBoardDisplay display, SheetBoardSource source, string path, Camera camera, RenderTexture renderTexture, Texture2D image)
        {
            bool oldInFlight = (bool)PrivateValue(source, "inFlight");
            int oldValidUrlCount = (int)PrivateValue(source, "validUrlCount");
            try
            {
                PrivateSet(source, "validUrlCount", 1);
                PrivateSet(source, "inFlight", true);
                PrivateInvoke(source, "UpdateStatus");
                PrivateInvoke(display, "RefreshIndicators", source.GetAccessStatus(), source.IsBusy());
                RenderRange(display, source, "schedule", path, camera, renderTexture, image);
            }
            finally
            {
                PrivateSet(source, "inFlight", oldInFlight);
                PrivateSet(source, "validUrlCount", oldValidUrlCount);
                PrivateInvoke(source, "UpdateStatus");
                PrivateInvoke(display, "RefreshIndicators", source.GetAccessStatus(), source.IsBusy());
            }
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
