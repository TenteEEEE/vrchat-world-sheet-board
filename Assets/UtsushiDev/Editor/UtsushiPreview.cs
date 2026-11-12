#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Utsushi;
using UtsushiEditor;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.Udon;
using VRC.SDK3.Data;

namespace UtsushiDev
{
    public static class UtsushiPreview
    {
        private static readonly List<string> failedClipLabels = new List<string>();
        private static readonly List<string> failedChipLabels = new List<string>();
        private static readonly List<string> failedGlyphLabels = new List<string>();
        private const float OverlayPadding = 6f;

        private static string NormalizePaletteCheck(string value)
        {
            value = value.Trim().Replace("\u3000", "");
            return value;
        }

        private static bool IsPaletteBlankCheck(string value)
        {
            if (value.Length == 0) return true;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c != '-' && c != '\u2010' && c != '\u2011' && c != '\u2013' && c != '\u2014' && c != '\u2015' && c != '\u30FC' && c != '\uFF0D' && c != '\u30FB' && c != '/' && c != '\uFF0F' && !char.IsWhiteSpace(c)) return false;
            }
            return true;
        }

        public static void RenderSampleBatch()
        {
            UtsushiDevMenu.RunBatch(Render);
        }
        internal static void RenderSample()
        {
            Render();
        }
        private static void Render()
        {
            failedClipLabels.Clear();
            failedChipLabels.Clear();
            failedGlyphLabels.Clear();
            VerifyWiring();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(UtsushiInstaller.DisplayPrefab);
            if (prefab == null) throw new InvalidOperationException("Build the Utsushi Display prefab first.");
            GameObject board = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            UtsushiDisplay display = board.GetComponent<UtsushiDisplay>();
            if (display == null) throw new InvalidOperationException("Display prefab has no UtsushiDisplay.");
            GameObject sourceObject = new GameObject("Preview Source");
            UtsushiSource source = sourceObject.AddUdonSharpComponent<UtsushiSource>();
            source.language = UtsushiLanguage.Japanese;
            display.source = source;
            string json = File.ReadAllText("Assets/UtsushiDev/Samples/sample.json", Encoding.UTF8);
            if (!source.ApplyJson(json)) throw new InvalidOperationException("Invalid sample JSON.");
            // A broken download must be rejected without dropping the data already shown.
            if (source.ApplyJson("{broken") || source.ApplyJson("{\"format\":\"other\"}") || source.GetRange("schedule") == null || source.GetRevision() != 1)
                throw new InvalidOperationException("Source did not keep the last good data after invalid JSON.");
            if (!source.ApplyJson(json) || source.GetRevision() != 1)
                throw new InvalidOperationException("Re-applying the same rev must not bump the revision.");
            VerifyBusyIndicator(source);
            VerifyStatusIndicators(source, display);
            display.rangeName = "";
            Canvas.ForceUpdateCanvases();
            display.RenderNow();
            VerifyLanguage(source, display);
            VerifyRuntimeRegressions(display);
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
                string[] files = { "UtsushiPreview.png", "UtsushiPreview_notice.png", "UtsushiPreview_missing.png" };
                // Unity wipes Temp/ on exit, so batch output goes to Logs/ instead.
                string temp = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
                Directory.CreateDirectory(temp);
                for (int i = 0; i < names.Length; i++)
                {
                    RenderRange(display, source, names[i], Path.Combine(temp, files[i]), camera, renderTexture, image);
                }
                RenderBusyRange(display, source, Path.Combine(temp, "UtsushiPreview_busy.png"), camera, renderTexture, image);
                string fidelityPath = Path.Combine(Path.GetDirectoryName("Assets/UtsushiDev/Samples/sample.json"), "sample_fidelity.json");
                string fidelityJson = File.ReadAllText(fidelityPath, Encoding.UTF8);
                string[] ranges = { "event", "wraptest", "fitcase", "chips", "manychips" };
                string[] prefabPaths = { UtsushiInstaller.DisplayPrefab, UtsushiInstaller.DisplayDarkPrefab };
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
                        display = board.GetComponent<UtsushiDisplay>();
                        if (display == null) throw new InvalidOperationException("Display prefab has no UtsushiDisplay: " + prefabPaths[t]);
                        sourceObject = new GameObject("Preview Source");
                        source = sourceObject.AddUdonSharpComponent<UtsushiSource>();
                        source.language = UtsushiLanguage.Japanese;
                        display.source = source;
                        if (!source.ApplyJson(fidelityJson) || source.GetRange(ranges[r]) == null)
                            throw new InvalidOperationException("Invalid fidelity sample or missing range: " + ranges[r]);
                        string file = "UtsushiPreview_" + ranges[r] + "_" + themes[t] + ".png";
                        RenderRange(display, source, ranges[r], Path.Combine(temp, file), camera, renderTexture, image);
                    }
                }
                string stressJson = CreateStressJson();
                string[] stressRanges = { "stress_grid", "stress_column", "escape" };
                for (int r = 0; r < stressRanges.Length; r++)
                {
                    for (int t = 0; t < prefabPaths.Length; t++)
                    {
                        UnityEngine.Object.DestroyImmediate(board);
                        UnityEngine.Object.DestroyImmediate(sourceObject);
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPaths[t]);
                        board = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                        display = board.GetComponent<UtsushiDisplay>();
                        sourceObject = new GameObject("Preview Source");
                        source = sourceObject.AddUdonSharpComponent<UtsushiSource>();
                        source.language = UtsushiLanguage.Japanese;
                        display.source = source;
                        if (!source.ApplyJson(stressJson) || source.GetRange(stressRanges[r]) == null)
                            throw new InvalidOperationException("Invalid stress sample or missing range: " + stressRanges[r]);
                        string file = "UtsushiPreview_" + stressRanges[r] + "_" + themes[t] + ".png";
                        RenderRange(display, source, stressRanges[r], Path.Combine(temp, file), camera, renderTexture, image);
                    }
                }
                if (failedClipLabels.Count > 0 || failedChipLabels.Count > 0 || failedGlyphLabels.Count > 0)
                {
                    string failures = "";
                    for (int i = 0; i < failedClipLabels.Count; i++)
                    {
                        if (failures.Length > 0) failures += ", ";
                        failures += "clip=" + failedClipLabels[i];
                    }
                    for (int i = 0; i < failedChipLabels.Count; i++)
                    {
                        if (failures.Length > 0) failures += ", ";
                        failures += "chip=" + failedChipLabels[i];
                    }
                    for (int i = 0; i < failedGlyphLabels.Count; i++)
                    {
                        if (failures.Length > 0) failures += ", ";
                        failures += "glyph=" + failedGlyphLabels[i];
                    }
                    throw new InvalidOperationException("[Utsushi] preview checks failed for: " + failures);
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
        private static uint NextStress(uint state)
        {
            return unchecked(state * 1664525u + 1013904223u);
        }
        private static string CreateStressJson()
        {
            StringBuilder json = new StringBuilder(900000);
            json.Append("{\"format\":\"sheetboard/1\",\"rev\":\"51b0a4e1\",\"updated\":\"2026-11-08T12:00:00+09:00\",\"updatedUnix\":1794106800,\"ranges\":{");
            json.Append("\"stress_grid\":{\"title\":\"Stress grid\",\"header\":true,\"rows\":[");
            uint seed = 0x51B0A4u;
            for (int r = 0; r < 60; r++)
            {
                if (r > 0) json.Append(',');
                json.Append('[');
                for (int c = 0; c < 18; c++)
                {
                    if (c > 0) json.Append(',');
                    seed = NextStress(seed);
                    int pick = (int)(seed % 10u);
                    if (pick < 6 || (r >= 52 && (r - 52) * 18 + c < 30))
                    {
                        seed = NextStress(seed);
                        int length = 1 + (int)(seed % 40u);
                        StringBuilder cell = new StringBuilder(length);
                        for (int k = 0; k < length; k++)
                        {
                            seed = NextStress(seed);
                            cell.Append((seed & 1u) == 0u ? (char)('A' + seed % 26u) : "日本語表組試験"[(int)(seed % 6u)]);
                        }
                        json.Append('"').Append(cell).Append('"');
                    }
                    else json.Append("\"\"");
                }
                json.Append(']');
            }
            json.Append("],\"wrap\":[");
            for (int r = 0; r < 60; r++)
            {
                if (r > 0) json.Append(',');
                json.Append('[');
                for (int c = 0; c < 18; c++) { if (c > 0) json.Append(','); json.Append('1'); }
                json.Append(']');
            }
            json.Append("],\"merges\":[");
            for (int i = 0; i < 50; i++)
            {
                if (i > 0) json.Append(',');
                int row = (i % 25) * 2;
                if (i < 25) json.Append('[').Append(row).Append(",0,2,1]");
                else json.Append('[').Append(row).Append(",2,1,2]");
            }
            json.Append("],\"chip\":[");
            for (int r = 0; r < 60; r++)
            {
                if (r > 0) json.Append(',');
                json.Append('[');
                for (int c = 0; c < 18; c++)
                {
                    if (c > 0) json.Append(',');
                    int n = r < 52 ? 30 : (r - 52) * 18 + c;
                    json.Append(n < 30 ? (n % 3 == 0 ? "\"a\"" : "\"c\"") : "\"\"");
                }
                json.Append(']');
            }
            json.Append("]},\"stress_column\":{\"title\":\"Stress column\",\"header\":false,\"rows\":[");
            for (int r = 0; r < 900; r++)
            {
                if (r > 0) json.Append(',');
                json.Append("[\"");
                for (int c = 0; c < 20 + r % 11; c++) json.Append("日本語表組試験"[c % 6]);
                json.Append("\",\"").Append(r + 1).Append("\"]");
            }
            json.Append("]},\"escape\":{\"title\":\"Escape\",\"header\":false,\"rows\":[[\"a<b>c\",\"<\"],[\"<color=red>x</color>\",\"1 < 2 > 0\"],[\"</noparse>\",\"ESCAPE\"],[\"a<b>c\",\"<\"]]}}}");
            return json.ToString();
        }
        private static void VerifyWiring()
        {
            string[] paths = { UtsushiInstaller.DisplayPrefab, UtsushiInstaller.DisplayDarkPrefab };
            string[] fields = { "background", "titleText", "statusText", "tableArea", "columnTemplate", "overlayTemplate", "overlayNoWrapTemplate", "cellTemplate", "chipTemplate", "arrowTemplate", "statusDot", "refreshButton", "refreshLabel" };
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            GameObject sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(UtsushiInstaller.SourcePrefab);
            if (sourcePrefab == null) throw new InvalidOperationException("Missing Source prefab.");
            GameObject sourceObject = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
            try
            {
                UtsushiSource wiringSource = sourceObject.GetComponent<UtsushiSource>();
                if (wiringSource == null) throw new InvalidOperationException("Source prefab has no UtsushiSource.");
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
                UtsushiDisplay display = board.GetComponent<UtsushiDisplay>();
                if (display == null) throw new InvalidOperationException("Display prefab has no UtsushiDisplay: " + paths[i]);
                var udon = UdonSharpEditorUtility.GetBackingUdonBehaviour(display);
                if (udon == null) throw new InvalidOperationException("Display has no backing Udon behaviour: " + paths[i]);
                foreach (string field in fields)
                    if (!(UdonValue(udon, field) is UnityEngine.Object value) || value == null)
                        throw new InvalidOperationException("Missing Udon variable " + field + ": " + paths[i]);
                Material expectedTmp = AssetDatabase.LoadAssetAtPath<Material>(UtsushiInstaller.RootDir + "Generated/Utsushi TMP Material.mat");
                foreach (TMP_Text text in prefab.GetComponentsInChildren<TMP_Text>(true))
                    if (expectedTmp == null || text.fontSharedMaterial != expectedTmp)
                        throw new InvalidOperationException("TMP does not use the generated shared material: " + text.name + ", " + paths[i]);
                if (!(UdonValue(udon, "lightTheme") is bool theme) || theme != (i == 0))
                    throw new InvalidOperationException("Incorrect Udon lightTheme: " + paths[i]);
                if (!(UdonValue(udon, "textScale") is float scale) || Mathf.Abs(scale - 1.4f) > 0.0001f)
                    throw new InvalidOperationException("Incorrect Udon textScale: " + paths[i]);
                display.RenderNow();
                if (display.titleText.text != "")
                    throw new InvalidOperationException("Title must be empty before data is received: " + paths[i]);
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            UtsushiInstaller.InstallSampleMenu();
            UtsushiDisplay installed = UnityEngine.Object.FindFirstObjectByType<UtsushiDisplay>();
            if (installed == null) throw new InvalidOperationException("Install Sample did not create a display.");
            if (Mathf.Abs(installed.transform.position.y - 1.3f) > 0.0001f || Quaternion.Angle(installed.transform.rotation, Quaternion.identity) > 0.01f)
                throw new InvalidOperationException("Install Sample placed the board at " + installed.transform.position + " / " + installed.transform.eulerAngles + "; expected y=1.3 and no rotation.");
            var installedUdon = UdonSharpEditorUtility.GetBackingUdonBehaviour(installed);
            if (installedUdon == null || !(UdonValue(installedUdon, "source") is UnityEngine.Object source) || source == null)
                throw new InvalidOperationException("Install Sample did not wire the Udon source.");
            Debug.Log("[Utsushi] Wiring OK");
        }
        private static object UdonValue(UdonBehaviour udon, string name)
        {
            return udon.publicVariables.TryGetVariableValue(name, out object value) ? value : null;
        }
        private static void VerifyBusyIndicator(UtsushiSource source)
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
                if (!source.IsBusy() || source.GetStatusLevel() != 0 || !source.GetStatusText().EndsWith(source.GetRefreshingSuffix(), StringComparison.Ordinal))
                    throw new InvalidOperationException("In-flight refresh did not preserve status level and append its suffix.");
                PrivateSet(source, "inFlight", false);
                PrivateInvoke(source, "UpdateStatus");
                if (source.GetStatusText().EndsWith(source.GetRefreshingSuffix(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Idle source retained the refreshing suffix.");
                source.RequestRefresh();
                if (!source.IsBusy() || source.GetRefreshWaitSeconds() == 0f)
                    throw new InvalidOperationException("Accepted manual refresh was not busy or did not start its cooldown.");
                Debug.Log("[Utsushi] Busy indicator OK");
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
        private static void VerifyStatusIndicators(UtsushiSource source, UtsushiDisplay display)
        {
            bool oldHasSuccess = (bool)PrivateValue(source, "hasSuccess");
            bool oldFailed = (bool)PrivateValue(source, "failed");
            bool oldInFlight = (bool)PrivateValue(source, "inFlight");
            bool oldManualPending = (bool)PrivateValue(source, "manualPending");
            int oldValidUrlCount = (int)PrivateValue(source, "validUrlCount");
            UtsushiSource oldDisplaySource = display.source;
            bool failed = false;
            try
            {
                display.source = source;
                PrivateSet(source, "hasSuccess", false);
                PrivateSet(source, "failed", false);
                PrivateSet(source, "inFlight", false);
                PrivateSet(source, "manualPending", false);
                PrivateSet(source, "validUrlCount", 1);
                PrivateInvoke(source, "UpdateStatus");
                if (source.GetStatusLevel() != 3)
                {
                    Debug.LogError("[Utsushi] status violation: initial state must be preparing");
                    failed = true;
                }
                PrivateInvoke(display, "RefreshIndicators", 0, false);
                if (display.statusDot == null || display.statusDot.color != display.busyColor || display.statusDot.color.a != 1f)
                {
                    Debug.LogError("[Utsushi] status violation: preparing indicator must use opaque busyColor");
                    failed = true;
                }
                PrivateSet(source, "failed", true);
                PrivateInvoke(source, "UpdateStatus");
                if (source.GetStatusLevel() != 2)
                {
                    Debug.LogError("[Utsushi] status violation: failed initial load must be an error");
                    failed = true;
                }
                PrivateInvoke(display, "RefreshIndicators", 0, false);
                if (display.statusDot == null || display.statusDot.color != display.errorColor)
                {
                    Debug.LogError("[Utsushi] status violation: failed initial load must use errorColor");
                    failed = true;
                }
                if (failed) throw new InvalidOperationException("Utsushi status Preview checks failed.");
                Debug.Log("[Utsushi] Status indicators OK");
            }
            finally
            {
                display.source = oldDisplaySource;
                PrivateSet(source, "hasSuccess", oldHasSuccess);
                PrivateSet(source, "failed", oldFailed);
                PrivateSet(source, "inFlight", oldInFlight);
                PrivateSet(source, "manualPending", oldManualPending);
                PrivateSet(source, "validUrlCount", oldValidUrlCount);
                PrivateInvoke(source, "UpdateStatus");
                if (display.source != null) PrivateInvoke(display, "RefreshIndicators", display.source.GetAccessStatus(), display.source.IsBusy());
            }
        }
        private static object PrivateValue(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Missing private field: " + name);
            return field.GetValue(target);
        }
        private static void VerifyRuntimeRegressions(UtsushiDisplay display)
        {
            UtsushiSource previous = display.source;
            string previousRange = display.rangeName;
            GameObject fixture = new GameObject("Runtime regression source");
            try
            {
                UtsushiSource test = fixture.AddUdonSharpComponent<UtsushiSource>();
                test.language = UtsushiLanguage.Japanese;
                PrivateInvoke(test, "ResolveLanguage");
                PrivateSet(test, "validUrlCount", 1);
                display.source = test;
                display.rangeName = "";
                PrivateInvoke(display, "ResolveLanguage");
                PrivateSet(test, "failed", true);
                PrivateSet(test, "errorCode", "timeout");
                PrivateSet(test, "nextFetchAt", Time.realtimeSinceStartup + 30f);
                PrivateInvoke(test, "UpdateStatus");
                if (!test.GetStatusText().Contains("時間切れ")) throw new InvalidOperationException("Timeout wording failed.");
                test.nearRadius = 10f;
                PrivateSet(test, "nextFetchAt", 0f);
                PrivateInvoke(test, "UpdateStatus");
                if (test.GetStatusLevel() != 3 || test.GetStatusText() != "近づくと読み込みます") throw new InvalidOperationException("Proximity retry status failed.");
                test.nearRadius = 0f;
                if (!test.ApplyJson("{\"format\":\"sheetboard/1\",\"rev\":\"empty\",\"updatedUnix\":1,\"ranges\":{}}")) throw new InvalidOperationException("Empty ranges fixture rejected.");
                display.RenderNow();
                PrivateInvoke(display, "RefreshIndicators", 0, false);
                if (display.statusText.text != "表示する範囲がまだありません" || display.statusDot.color != display.errorColor) throw new InvalidOperationException("Empty ranges status failed.");
                StringBuilder rows = new StringBuilder();
                for (int i = 0; i < 400; i++) { if (i > 0) rows.Append(','); rows.Append("[\"x\",\"\",\"\"]"); }
                string large = "{\"format\":\"sheetboard/1\",\"rev\":\"large\",\"updatedUnix\":2,\"ranges\":{\"large\":{\"rows\":[" + rows + "]}}}";
                if (!test.ApplyJson(large)) throw new InvalidOperationException("Large table fixture rejected.");
                display.RenderNow();
                PrivateInvoke(display, "RefreshIndicators", 0, false);
                if (!display.statusText.text.Contains("表が大きく文字が小さくなっています") || display.statusDot.color != display.staleColor) throw new InvalidOperationException("Large table warning failed.");
                float[] heights = (float[])PrivateValue(display, "workHs");
                for (int i = 0; i < heights.Length; i++) if (heights[i] < 1f) throw new InvalidOperationException("Zero-height row.");
                string chip = "{\"format\":\"sheetboard/1\",\"rev\":\"chip\",\"updatedUnix\":3,\"ranges\":{\"chip\":{\"cols\":[1,1,1],\"rows\":[[\"A very long sentence that must not cross into the dropdown\",\"\",\"\"]],\"chip\":[[\"\",\"a\",\"\"]]}}}";
                if (!test.ApplyJson(chip)) throw new InvalidOperationException("Chip fixture rejected.");
                display.RenderNow();
                bool[] candidates = (bool[])PrivateValue(display, "workSpillCandidates");
                bool[] overlays = (bool[])PrivateValue(display, "workOverlayUsed");
                if (candidates[0] || overlays[0]) throw new InvalidOperationException("Text overflowed into an empty dropdown.");
            }
            finally
            {
                display.source = previous;
                display.rangeName = previousRange;
                PrivateInvoke(display, "ResolveLanguage");
                display.RenderNow();
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }
        private static bool IsKnownOverflow(UtsushiDisplay display, int row, int col)
        {
            MethodInfo method = display.GetType().GetMethod("IsKnownOverflow", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (method == null) throw new InvalidOperationException("Missing method: IsKnownOverflow");
            return (bool)method.Invoke(display, new object[] { row, col });
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
        private static void VerifyLanguage(UtsushiSource source, UtsushiDisplay display)
        {
            bool failed = false;
            UtsushiLanguage oldSourceLanguage = source.language;
            UtsushiSource oldDisplaySource = display.source;
            string oldUrlMissingText = source.urlMissingText;
            string oldPermissionDeniedText = source.permissionDeniedText;
            string oldRangeMissingText = display.rangeMissingText;
            string oldSourceMissingText = display.sourceMissingText;
            string oldRefreshIdleText = display.refreshIdleText;
            string oldRangeName = display.rangeName;
            source.language = UtsushiLanguage.English;
            source.urlMissingText = "URLが設定されていません";
            PrivateInvoke(source, "ResolveLanguage");
            source.permissionDeniedText = "";
            if (source.GetPermissionDeniedText() != "You don't have permission to view this") { Debug.LogError("[Utsushi] language violation: empty Source override"); failed = true; }
            if (source.GetRefreshingSuffix() != " · updating…") { Debug.LogError("[Utsushi] language violation: English refreshing suffix"); failed = true; }
            PrivateInvoke(source, "UpdateStatus");
            if (source.GetStatusText() != "No URL is set") { Debug.LogError("[Utsushi] language violation: English Source status"); failed = true; }
            source.urlMissingText = "Custom";
            PrivateInvoke(source, "UpdateStatus");
            if (source.GetStatusText() != "Custom") { Debug.LogError("[Utsushi] language violation: custom Source override"); failed = true; }
            source.permissionDeniedText = "閲覧権限がありません";
            if (source.GetPermissionDeniedText() != "You don't have permission to view this") { Debug.LogError("[Utsushi] language violation: legacy Source override"); failed = true; }
            display.rangeMissingText = "範囲「{name}」が見つかりません";
            display.sourceMissingText = "Sourceが設定されていません";
            display.refreshIdleText = "更新";
            display.source = null;
            PrivateSet(display, "resolvedLanguage", UtsushiLanguage.English);
            PrivateSet(display, "languageResolved", true);
            PrivateInvoke(display, "RefreshIndicators", 2, false);
            if (display.statusText == null || display.statusText.text != "No data source is set") { Debug.LogError("[Utsushi] language violation: English missing Source display"); failed = true; }
            display.sourceMissingText = "Custom";
            PrivateInvoke(display, "RefreshIndicators", 2, false);
            if (display.statusText == null || display.statusText.text != "Custom") { Debug.LogError("[Utsushi] language violation: custom Display override"); failed = true; }
            PrivateInvoke(display, "UpdateRefreshButton", 2, false);
            if (display.refreshLabel == null || display.refreshLabel.text != "Refresh") { Debug.LogError("[Utsushi] language violation: English refresh button"); failed = true; }
            // Auto on both sides needs VRCPlayerApi.GetCurrentLanguage, which throws in batchmode, so only the follow-the-Source paths are checked.
            display.source = source;
            display.refreshIdleText = "";
            source.language = UtsushiLanguage.Japanese;
            PrivateInvoke(display, "ResolveLanguage");
            PrivateInvoke(display, "UpdateRefreshButton", 2, false);
            if (display.refreshLabel == null || display.refreshLabel.text != "更新") { Debug.LogError("[Utsushi] language violation: Auto Display did not follow Japanese Source"); failed = true; }
            source.language = UtsushiLanguage.English;
            PrivateInvoke(display, "ResolveLanguage");
            PrivateInvoke(display, "UpdateRefreshButton", 2, false);
            if (display.refreshLabel == null || display.refreshLabel.text != "Refresh") { Debug.LogError("[Utsushi] language violation: Auto Display did not follow English Source"); failed = true; }
            PrivateInvoke(display, "ResolveLanguage");
            PrivateInvoke(display, "UpdateRefreshButton", 2, false);
            display.refreshIdleText = "更新";
            PrivateInvoke(display, "ResolveLanguage");
            display.source = source;
            display.rangeName = "language_missing";
            display.RenderNow();
            if (display.titleText == null || display.titleText.text != "Range \"language_missing\" was not found") { Debug.LogError("[Utsushi] language violation: English missing range display"); failed = true; }
            source.language = oldSourceLanguage;
            source.urlMissingText = oldUrlMissingText;
            source.permissionDeniedText = oldPermissionDeniedText;
            display.source = oldDisplaySource;
            display.rangeMissingText = oldRangeMissingText;
            display.sourceMissingText = oldSourceMissingText;
            display.refreshIdleText = oldRefreshIdleText;
            display.rangeName = oldRangeName;
            PrivateInvoke(source, "ResolveLanguage");
            PrivateInvoke(source, "UpdateStatus");
            PrivateInvoke(display, "ResolveLanguage");
            display.RenderNow();
            if (failed) throw new InvalidOperationException("Utsushi language Preview checks failed.");
        }
        private static void RenderBusyRange(UtsushiDisplay display, UtsushiSource source, string path, Camera camera, RenderTexture renderTexture, Texture2D image)
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
        private static bool AssertNoClipping(UtsushiDisplay display, string label)
        {
            TMP_Text[] texts = display.tableArea.GetComponentsInChildren<TMP_Text>(false);
            Vector3[] tableCorners = new Vector3[4];
            display.tableArea.GetWorldCorners(tableCorners);
            Vector2 tableMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 tableMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = display.tableArea.InverseTransformPoint(tableCorners[i]);
                tableMin = Vector2.Min(tableMin, local);
                tableMax = Vector2.Max(tableMax, local);
            }
            TMP_Text[] columns = PrivateValue(display, "segments") as TMP_Text[];
            int[] segmentFirstRows = PrivateValue(display, "segmentFirstRow") as int[];
            int glyphs = 0;
            int violations = 0;
            int exempt = 0;
            for (int t = 0; t < texts.Length; t++)
            {
                TMP_Text text = texts[t];
                if (text == null || !text.gameObject.activeInHierarchy) continue;
                int segmentIndex = SegmentIndex(display, text);
                int columnIndex = ColumnIndex(display, text);
                int overlayRow = -1;
                int overlayCol = -1;
                bool isColumn = columnIndex >= 0;
                if (!isColumn && !OverlayCell(display, text, out overlayRow, out overlayCol))
                {
                    overlayRow = -1;
                    overlayCol = -1;
                }
                else if (isColumn)
                {
                    overlayRow = -1;
                    overlayCol = columnIndex;
                }
                text.ForceMeshUpdate();
                TMP_CharacterInfo[] characters = text.textInfo.characterInfo;
                int characterCount = Math.Min(text.textInfo.characterCount, characters.Length);
                RectMask2D nearestMask = null;
                Transform parent = text.transform;
                while (parent != null && nearestMask == null)
                {
                    nearestMask = parent.GetComponent<RectMask2D>();
                    parent = parent.parent;
                }
                Vector2 maskMin = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                Vector2 maskMax = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                if (nearestMask != null)
                {
                    Vector3[] maskCorners = new Vector3[4];
                    nearestMask.GetComponent<RectTransform>().GetWorldCorners(maskCorners);
                    maskMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                    maskMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 local = display.tableArea.InverseTransformPoint(maskCorners[i]);
                        maskMin = Vector2.Min(maskMin, local);
                        maskMax = Vector2.Max(maskMax, local);
                    }
                }
                Vector3[] glyphCorners = new Vector3[4];
                for (int k = 0; k < characterCount; k++)
                {
                    TMP_CharacterInfo character = characters[k];
                    if (!character.isVisible) continue;
                    if (isColumn && character.lineNumber == 0) continue;
                    glyphs++;
                    glyphCorners[0] = character.bottomLeft;
                    glyphCorners[1] = character.topLeft;
                    glyphCorners[2] = character.topRight;
                    glyphCorners[3] = character.bottomRight;
                    float left = float.PositiveInfinity;
                    float right = float.NegativeInfinity;
                    float bottom = float.PositiveInfinity;
                    float top = float.NegativeInfinity;
                    for (int q = 0; q < 4; q++)
                    {
                        Vector3 local = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(glyphCorners[q]));
                        left = Mathf.Min(left, local.x);
                        right = Mathf.Max(right, local.x);
                        bottom = Mathf.Min(bottom, local.y);
                        top = Mathf.Max(top, local.y);
                    }
                    float horizontal = Mathf.Max(tableMin.x - left, right - tableMax.x, maskMin.x - left, right - maskMax.x);
                    float vertical = Mathf.Max(tableMin.y - bottom, top - tableMax.y, maskMin.y - bottom, top - maskMax.y);
                    int row = isColumn ? segmentFirstRows[segmentIndex] + character.lineNumber - 1 : overlayRow;
                    int col = isColumn ? columnIndex : overlayCol;
                    int workRows = (int)PrivateValue(display, "workRows");
                    int workCols = (int)PrivateValue(display, "workCols");
                    bool hasCell = row >= 0 && row < workRows && col >= 0 && col < workCols;
                    bool knownOverflow = hasCell && IsKnownOverflow(display, row, col) && horizontal > 1f;
                    if (knownOverflow)
                    {
                        exempt++;
                        horizontal = 0f;
                    }
                    float horizontalTolerance = (character.style & FontStyles.Italic) != 0 ? Mathf.Max(1f, 0.25f * text.fontSize) : 1f;
                    float overflow = Mathf.Max(horizontal, vertical);
                    if (horizontal > horizontalTolerance || vertical > 1f)
                    {
                        violations++;
                        if (violations <= 10)
                        {
                            string location = col >= 0 ? ", col=" + (col + 1) : "";
                            if (hasCell) location += ", row=" + (row + 1);
                            string cell = hasCell ? CellAt(display, row, col) : "";
                            string axis = horizontal > horizontalTolerance && vertical > 1f ? "横/縦" : (horizontal > horizontalTolerance ? "横" : "縦");
                            string characterRect = "(" + left.ToString("0.0") + "," + bottom.ToString("0.0") + ")- (" + right.ToString("0.0") + "," + top.ToString("0.0") + ")";
                            string maskRect = "(" + maskMin.x.ToString("0.0") + "," + maskMin.y.ToString("0.0") + ")-(" + maskMax.x.ToString("0.0") + "," + maskMax.y.ToString("0.0") + ")";
                            string tableRect = "(" + tableMin.x.ToString("0.0") + "," + tableMin.y.ToString("0.0") + ")-(" + tableMax.x.ToString("0.0") + "," + tableMax.y.ToString("0.0") + ")";
                            Debug.LogError("[Utsushi] clip violation: " + label + ", theme=" + (display.lightTheme ? "light" : "dark") + location + ", cell=\"" + cell + "\", tmp=\"" + text.gameObject.name + "\", character=\"" + character.character + "\", line=" + character.lineNumber + ", axis=" + axis + ", overflow=" + overflow.ToString("0.0") + "px, glyph=" + characterRect + ", mask=" + maskRect + ", table=" + tableRect);
                        }
                    }
                }
            }
            if (exempt > 0) Debug.Log("[Utsushi] " + label + ": exempt " + exempt + " glyphs (known overflow)");
            if (violations == 0) Debug.Log("[Utsushi] clip check OK: " + label + " (" + glyphs + " glyphs)");
            return violations > 0;
        }

        private static int SegmentIndex(UtsushiDisplay display, TMP_Text text)
        {
            TMP_Text[] columns = PrivateValue(display, "segments") as TMP_Text[];
            int count = (int)PrivateValue(display, "segmentCount");
            if (columns == null) return -1;
            for (int i = 0; i < count && i < columns.Length; i++)
                if (columns[i] == text) return i;
            return -1;
        }
        private static int ColumnIndex(UtsushiDisplay display, TMP_Text text)
        {
            int index = SegmentIndex(display, text);
            int[] columns = PrivateValue(display, "segmentColumn") as int[];
            return index >= 0 && columns != null && index < columns.Length ? columns[index] : -1;
        }

        private static bool OverlayCell(UtsushiDisplay display, TMP_Text text, out int row, out int col)
        {
            row = -1;
            col = -1;
            TMP_Text[] pools = PrivateValue(display, "overlays") as TMP_Text[];
            TMP_Text[] noWrapPools = PrivateValue(display, "overlaysNoWrap") as TMP_Text[];
            int count = (int)PrivateValue(display, "overlayCount");
            int noWrapCount = (int)PrivateValue(display, "overlayNoWrapCount");
            bool found = false;
            for (int i = 0; pools != null && i < count && i < pools.Length; i++) if (pools[i] == text) found = true;
            for (int i = 0; !found && noWrapPools != null && i < noWrapCount && i < noWrapPools.Length; i++) if (noWrapPools[i] == text) found = true;
            if (!found) return false;
            Transform current = text.transform;
            while (current.parent != null && current.parent != display.tableArea) current = current.parent;
            if (current.parent != display.tableArea) return false;
            RectTransform frame = current as RectTransform;
            if (frame == null) return false;
            float[] xs = PrivateValue(display, "workXs") as float[];
            float[] ys = PrivateValue(display, "workYs") as float[];
            if (xs == null || ys == null) return false;
            float x = frame.anchoredPosition.x - OverlayPadding;
            float y = -frame.anchoredPosition.y;
            float bestX = float.PositiveInfinity;
            float bestY = float.PositiveInfinity;
            for (int i = 0; i < xs.Length; i++)
            {
                float distance = Mathf.Abs(xs[i] - x);
                if (distance < bestX)
                {
                    bestX = distance;
                    col = i;
                }
            }
            for (int i = 0; i < ys.Length; i++)
            {
                float distance = Mathf.Abs(ys[i] - y);
                if (distance < bestY)
                {
                    bestY = distance;
                    row = i;
                }
            }
            if (bestX > 1f) col = -1;
            if (bestY > 1f) row = -1;
            return row >= 0 || col >= 0;
        }

        private static string CellAt(UtsushiDisplay display, int row, int col)
        {
            string[] raw = PrivateValue(display, "workRaw") as string[];
            int cols = (int)PrivateValue(display, "workCols");
            int index = row * cols + col;
            return raw != null && index >= 0 && index < raw.Length ? raw[index] : "";
        }

        private static void LocalBounds(RectTransform rect, RectTransform root, out float left, out float right, out float bottom, out float top)
        {
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            left = float.PositiveInfinity;
            right = float.NegativeInfinity;
            bottom = float.PositiveInfinity;
            top = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector3 point = root.InverseTransformPoint(corners[i]);
                left = Mathf.Min(left, point.x);
                right = Mathf.Max(right, point.x);
                bottom = Mathf.Min(bottom, point.y);
                top = Mathf.Max(top, point.y);
            }
        }

        private static bool AssertChipLayout(UtsushiDisplay display, UtsushiSource source, string rangeName, string label)
        {
            bool failed = false;
            int rows = (int)PrivateValue(display, "workRows");
            int cols = (int)PrivateValue(display, "workCols");
            int[] kinds = PrivateValue(display, "workChipKind") as int[];
            bool[] covered = PrivateValue(display, "workCovered") as bool[];
            bool[] workHasChipBg = PrivateValue(display, "workHasChipBg") as bool[];
            string[] rawValues = PrivateValue(display, "workRaw") as string[];
            Color[] chipBackgrounds = PrivateValue(display, "workChipBg") as Color[];
            bool[] hasAutoPalette = PrivateValue(display, "workHasAutoChipColor") as bool[];
            Color[] autoPaletteColors = PrivateValue(display, "workAutoChipColors") as Color[];
            int[] spanRows = PrivateValue(display, "workSpanRows") as int[];
            int[] spanCols = PrivateValue(display, "workSpanCols") as int[];
            float[] xs = PrivateValue(display, "workXs") as float[];
            float[] ws = PrivateValue(display, "workWs") as float[];
            float[] ys = PrivateValue(display, "workYs") as float[];
            float[] hs = PrivateValue(display, "workHs") as float[];
            Image[] chipImages = PrivateValue(display, "chips") as Image[];
            Image[] arrowImages = PrivateValue(display, "arrows") as Image[];
            TMP_Text[] columns = PrivateValue(display, "segments") as TMP_Text[];
            int segmentCount = (int)PrivateValue(display, "segmentCount");
            RectTransform chipLayer = PrivateValue(display, "chipContainer") as RectTransform;
            RectTransform arrowLayer = PrivateValue(display, "arrowContainer") as RectTransform;
            int expectedChips = 0;
            int expectedArrows = 0;
            DataDictionary data = source.GetRange(rangeName);
            DataToken chipToken;
            if (data != null && data.TryGetValue("chip", out chipToken) && chipToken.TokenType == TokenType.DataList)
            {
                DataList chipRows = chipToken.DataList;
                for (int r = 0; r < chipRows.Count; r++)
                {
                    if (chipRows[r].TokenType != TokenType.DataList) continue;
                    DataList row = chipRows[r].DataList;
                    for (int c = 0; c < row.Count; c++)
                    {
                        if (row[c].TokenType != TokenType.String) continue;
                        string value = row[c].String;
                        if (value == "a") expectedArrows++;
                        else if (value == "c" || value.StartsWith("c|")) { expectedChips++; expectedArrows++; }
                    }
                }
            }
            int actualChips = 0;
            int actualArrows = 0;
            if (chipImages != null) for (int i = 0; i < chipImages.Length; i++) if (chipImages[i] != null && chipImages[i].gameObject.activeInHierarchy) actualChips++;
            if (arrowImages != null) for (int i = 0; i < arrowImages.Length; i++) if (arrowImages[i] != null && arrowImages[i].gameObject.activeInHierarchy) actualArrows++;
            if (actualChips != expectedChips || actualArrows != expectedArrows)
            {
                failed = true;
                Debug.LogError("[Utsushi] chip count violation: " + label + ", chips=" + actualChips + "/" + expectedChips + ", arrows=" + actualArrows + "/" + expectedArrows);
            }
            if (chipLayer == null || arrowLayer == null || chipLayer.GetSiblingIndex() >= arrowLayer.GetSiblingIndex()) failed = true;
            Image[] cells = PrivateValue(display, "cells") as Image[];
            if (cells != null)
                for (int i = 0; i < cells.Length; i++)
                    if (cells[i] != null && cells[i].gameObject.activeInHierarchy && chipLayer != null && cells[i].transform.GetSiblingIndex() >= chipLayer.GetSiblingIndex()) failed = true;
            if (columns != null && arrowLayer != null)
                for (int i = 0; i < segmentCount && i < columns.Length; i++)
                    if (columns[i] != null && columns[i].gameObject.activeInHierarchy && columns[i].transform.parent.GetSiblingIndex() <= arrowLayer.GetSiblingIndex()) failed = true;
            int pool = 0;
            float maxVCenterDelta = 0f;
            string[] paletteNames = new string[rows * cols];
            Color[] paletteColors = new Color[rows * cols];
            int paletteNameCount = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int cell = r * cols + c;
                    int kind = kinds == null ? 0 : kinds[cell];
                    if (kind == 0 || (covered != null && covered[cell])) continue;
                    int rowSpan = spanRows == null ? 1 : Mathf.Max(1, spanRows[cell]);
                    int colSpan = spanCols == null ? 1 : Mathf.Max(1, spanCols[cell]);
                    float cellWidth = xs[c + colSpan - 1] + ws[c + colSpan - 1] - xs[c];
                    float cellHeight = ys[r + rowSpan - 1] + hs[r + rowSpan - 1] - ys[r];
                    float inset = display.showGrid ? Mathf.Max(0f, display.gridWidth) : 0f;
                    float left = display.tableArea.rect.xMin + xs[c] + inset;
                    float top = display.tableArea.rect.yMax - ys[r] - inset;
                    float right = left + cellWidth - inset * 2f;
                    float bottom = top - cellHeight + inset * 2f;
                    Image chip = chipImages != null && pool < chipImages.Length ? chipImages[pool] : null;
                    Image arrow = arrowImages != null && pool < arrowImages.Length ? arrowImages[pool] : null;
                    if (kind == 2)
                    {
                        float font = (float)PrivateValue(display, "workBasePx");
                        float[] points = PrivateValue(display, "workPt") as float[];
                        float basePt = (float)PrivateValue(display, "workBasePt");
                        if (points != null && points[cell] > 0f && basePt > 0f) font *= Mathf.Clamp(points[cell] / basePt, 0.6f, 1.6f);
                        float chipWidth = right - left - 6f;
                        float chipHeight = Mathf.Min((top - bottom) - 6f, Mathf.Round(font * 1.55f));
                        if (chipWidth < 4f || chipHeight < 4f) failed = true;
                        if (chip == null || !chip.gameObject.activeInHierarchy || arrow == null || !arrow.gameObject.activeInHierarchy) failed = true;
                        else
                        {
                            Color expectedFill = workHasChipBg != null && workHasChipBg[cell]
                                ? chipBackgrounds[cell]
                                : (hasAutoPalette != null && hasAutoPalette[cell] ? autoPaletteColors[cell] : display.chipColor);
                            if (chip.color != expectedFill)
                            {
                                failed = true;
                                Debug.LogError("[Utsushi] chip palette fill violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1));
                            }
                            if (rangeName == "chips" && (workHasChipBg == null || !workHasChipBg[cell]))
                            {
                                string normalized = NormalizePaletteCheck(rawValues == null ? "" : rawValues[cell]);
                                if (!IsPaletteBlankCheck(normalized))
                                {
                                    bool alreadySeen = false;
                                    for (int n = 0; n < paletteNameCount; n++)
                                    {
                                        if (paletteNames[n] == normalized)
                                        {
                                            alreadySeen = true;
                                            if (paletteColors[n] != chip.color)
                                            {
                                                failed = true;
                                                Debug.LogError("[Utsushi] repeated chip name color violation: " + label + ", name=" + normalized);
                                            }
                                        }
                                        if (paletteNames[n] != normalized && display.chipPalette != null && paletteNameCount < display.chipPalette.Length && paletteColors[n] == chip.color)
                                        {
                                            failed = true;
                                            Debug.LogError("[Utsushi] distinct chip name color violation: " + label + ", name=" + normalized);
                                        }
                                    }
                                    if (!alreadySeen)
                                    {
                                        paletteNames[paletteNameCount] = normalized;
                                        paletteColors[paletteNameCount] = chip.color;
                                        paletteNameCount++;
                                    }
                                }
                                else if (chip.color != display.chipColor)
                                {
                                    failed = true;
                                    Debug.LogError("[Utsushi] blank chip palette violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1));
                                }
                            }
                            float chipLeft, chipRight, chipBottom, chipTop;
                            LocalBounds(chip.rectTransform, display.tableArea, out chipLeft, out chipRight, out chipBottom, out chipTop);
                            if (chipLeft < left - 0.5f || chipRight > right + 0.5f || chipBottom < bottom - 0.5f || chipTop > top + 0.5f) failed = true;
                            float arrowLeft, arrowRight, arrowBottom, arrowTop;
                            LocalBounds(arrow.rectTransform, display.tableArea, out arrowLeft, out arrowRight, out arrowBottom, out arrowTop);
                            if (arrowLeft < chipLeft - 0.5f || arrowRight > chipRight + 0.5f || arrowBottom < chipBottom - 0.5f || arrowTop > chipTop + 0.5f) failed = true;
                            float visibleBottom = float.PositiveInfinity;
                            float visibleTop = float.NegativeInfinity;
                            bool hasVisibleGlyph = false;
                            TMP_Text[] texts = display.tableArea.GetComponentsInChildren<TMP_Text>(false);
                            for (int t = 0; t < texts.Length; t++)
                            {
                                TMP_Text text = texts[t];
                                int textColumn = ColumnIndex(display, text);
                                int overlayRow = -1;
                                int overlayCol = -1;
                                bool isColumn = textColumn >= 0;
                                if (isColumn) overlayCol = textColumn;
                                else OverlayCell(display, text, out overlayRow, out overlayCol);
                                if (isColumn ? overlayCol != c : (overlayRow != r || overlayCol > c)) continue;
                                text.ForceMeshUpdate();
                                TMP_CharacterInfo[] chars = text.textInfo.characterInfo;
                                for (int k = 0; k < text.textInfo.characterCount && k < chars.Length; k++)
                                {
                                    TMP_CharacterInfo character = chars[k];
                                    int[] segmentFirst = PrivateValue(display, "segmentFirstRow") as int[];
                                    int segmentIndex = isColumn ? SegmentIndex(display, text) : -1;
                                    int textRow = isColumn ? segmentFirst[segmentIndex] + character.lineNumber - 1 : overlayRow;
                                    if (!character.isVisible || (isColumn && textRow != r)) continue;
                                    Vector3 a = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.bottomLeft));
                                    Vector3 b = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.bottomRight));
                                    Vector3 glyphTop = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.topLeft));
                                    visibleBottom = Mathf.Min(visibleBottom, a.y);
                                    visibleTop = Mathf.Max(visibleTop, glyphTop.y);
                                    hasVisibleGlyph = true;
                                    if (Mathf.Min(a.x, b.x) < arrowRight && Mathf.Max(a.x, b.x) > arrowLeft) failed = true;
                                }
                            }
                            if (rangeName == "chips" && hasVisibleGlyph)
                            {
                                float glyphCenter = (visibleBottom + visibleTop) * 0.5f;
                                float chipCenter = (chipBottom + chipTop) * 0.5f;
                                float delta = Mathf.Abs(glyphCenter - chipCenter);
                                maxVCenterDelta = Mathf.Max(maxVCenterDelta, delta);
                                float tolerance = Mathf.Max(2f, 0.15f * font);
                                if (delta > tolerance)
                                {
                                    failed = true;
                                    Debug.LogError("[Utsushi] chip vcenter violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1) + ", delta=" + delta.ToString("0.0") + "px, tolerance=" + tolerance.ToString("0.0") + "px");
                                }
                            }
                        }
                        pool++;
                    }
                    else
                    {
                        if (arrow == null || !arrow.gameObject.activeInHierarchy) failed = true;
                        else
                        {
                            float arrowLeft, arrowRight, arrowBottom, arrowTop;
                            LocalBounds(arrow.rectTransform, display.tableArea, out arrowLeft, out arrowRight, out arrowBottom, out arrowTop);
                            if (arrowLeft < left - 0.5f || arrowRight > right + 0.5f || arrowBottom < bottom - 0.5f || arrowTop > top + 0.5f) failed = true;
                        }
                        pool++;
                    }
                }
            }
            if (rangeName == "chips") Debug.Log("[Utsushi] chip vcenter max delta: " + maxVCenterDelta.ToString("0.0") + " px");
            if (failed) Debug.LogError("[Utsushi] chip layout violation: " + label);
            else Debug.Log("[Utsushi] chip check OK: " + label);
            return failed;
        }

        private static bool AssertColumnVCenter(UtsushiDisplay display, string label)
        {
            int rows = (int)PrivateValue(display, "workRows");
            int cols = (int)PrivateValue(display, "workCols");
            string[] raw = PrivateValue(display, "workRaw") as string[];
            bool[] covered = PrivateValue(display, "workCovered") as bool[];
            bool[] overlayUsed = PrivateValue(display, "workOverlayUsed") as bool[];
            int[] spanRows = PrivateValue(display, "workSpanRows") as int[];
            int[] spanCols = PrivateValue(display, "workSpanCols") as int[];
            float[] points = PrivateValue(display, "workPt") as float[];
            float[] ys = PrivateValue(display, "workYs") as float[];
            float[] hs = PrivateValue(display, "workHs") as float[];
            TMP_Text[] columns = PrivateValue(display, "segments") as TMP_Text[];
            int[] segmentColumns = PrivateValue(display, "segmentColumn") as int[];
            int[] segmentFirstRows = PrivateValue(display, "segmentFirstRow") as int[];
            int[] segmentLastRows = PrivateValue(display, "segmentLastRow") as int[];
            int segmentCount = (int)PrivateValue(display, "segmentCount");
            float basePt = (float)PrivateValue(display, "workBasePt");
            float basePx = (float)PrivateValue(display, "workBasePx");
            bool failed = false;
            float maxDelta = 0f;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int cell = r * cols + c;
                    if (raw == null || raw[cell] == "" || (covered != null && covered[cell]) || (overlayUsed != null && overlayUsed[cell])) continue;
                    if ((spanRows != null && spanRows[cell] > 1) || (spanCols != null && spanCols[cell] > 1) || IsKnownOverflow(display, r, c)) continue;
                    TMP_Text text = null;
                    int segmentIndex = -1;
                    for (int s = 0; columns != null && s < segmentCount && s < columns.Length; s++)
                    {
                        if (segmentColumns[s] == c && r >= segmentFirstRows[s] && r <= segmentLastRows[s]) { text = columns[s]; segmentIndex = s; break; }
                    }
                    if (text == null || !text.gameObject.activeInHierarchy) continue;
                    text.ForceMeshUpdate();
                    TMP_CharacterInfo[] chars = text.textInfo.characterInfo;
                    float visibleBottom = float.PositiveInfinity;
                    float visibleTop = float.NegativeInfinity;
                    bool hasVisibleGlyph = false;
                    for (int k = 0; k < text.textInfo.characterCount && k < chars.Length; k++)
                    {
                        TMP_CharacterInfo character = chars[k];
                        if (!character.isVisible || segmentFirstRows[segmentIndex] + character.lineNumber - 1 != r) continue;
                        Vector3 bottom = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.bottomLeft));
                        Vector3 top = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.topLeft));
                        visibleBottom = Mathf.Min(visibleBottom, bottom.y);
                        visibleTop = Mathf.Max(visibleTop, top.y);
                        hasVisibleGlyph = true;
                    }
                    if (!hasVisibleGlyph) continue;
                    float cellCenter = display.tableArea.rect.yMax - ys[r] - hs[r] * 0.5f;
                    float delta = Mathf.Abs((visibleBottom + visibleTop) * 0.5f - cellCenter);
                    maxDelta = Mathf.Max(maxDelta, delta);
                    float font = basePx;
                    if (points != null && points[cell] > 0f && basePt > 0f) font *= Mathf.Clamp(points[cell] / basePt, 0.6f, 1.6f);
                    float tolerance = Mathf.Max(2f, 0.15f * font);
                    if (delta > tolerance)
                    {
                        failed = true;
                        Debug.LogError("[Utsushi] column vcenter violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1) + ", delta=" + delta.ToString("0.0") + "px, tolerance=" + tolerance.ToString("0.0") + "px");
                    }
                }
            }
            Debug.Log("[Utsushi] column vcenter max delta: " + maxDelta.ToString("0.0") + " px: " + label);
            if (!failed) Debug.Log("[Utsushi] column vcenter check OK: " + label);
            return failed;
        }

        private static bool IsPreviewWhitespace(char value)
        {
            return value == ' ' || (value >= '\t' && value <= '\r') || value == '\u0085' || value == '\u00A0' || value == '\u1680' || (value >= '\u2000' && value <= '\u200A') || value == '\u2028' || value == '\u2029' || value == '\u202F' || value == '\u205F' || value == '\u3000';
        }
        private static int ExpectedGlyphs(string value)
        {
            if (value == null) return 0;
            int count = 0;
            int limit = Math.Min(value.Length, 4000);
            if (limit > 0 && limit < value.Length && char.IsHighSurrogate(value[limit - 1]) && char.IsLowSurrogate(value[limit])) limit--;
            for (int i = 0; i < limit; i++)
            {
                char c = value[i];
                if (IsPreviewWhitespace(c)) continue;
                count++;
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            }
            if (value.Length > 4000) count++;
            return count;
        }
        private static string PreviewValue(DataToken token)
        {
            if (token.TokenType == TokenType.String) return token.String == null ? "" : token.String;
            if (token.TokenType == TokenType.Int) return token.Int.ToString();
            if (token.TokenType == TokenType.Long) return token.Long.ToString();
            if (token.TokenType == TokenType.Double) return token.Double.ToString();
            return "";
        }
        private static int ExpectedGlyphsFromData(DataDictionary range)
        {
            DataToken rowsToken;
            if (range == null || !range.TryGetValue("rows", out rowsToken) || rowsToken.TokenType != TokenType.DataList) return 0;
            DataList rows = rowsToken.DataList;
            int rowCount = rows.Count;
            int columnCount = 0;
            for (int r = 0; r < rowCount; r++)
            {
                if (rows[r].TokenType == TokenType.DataList) columnCount = Math.Max(columnCount, rows[r].DataList.Count);
            }
            if (rowCount == 0 || columnCount == 0) return 0;
            string[] raw = new string[rowCount * columnCount];
            bool[] covered = new bool[raw.Length];
            int[] spanRows = new int[raw.Length];
            for (int r = 0; r < rowCount; r++)
            {
                if (rows[r].TokenType != TokenType.DataList) continue;
                DataList row = rows[r].DataList;
                for (int c = 0; c < row.Count; c++) raw[r * columnCount + c] = PreviewValue(row[c]);
            }
            DataToken mergesToken;
            if (range.TryGetValue("merges", out mergesToken) && mergesToken.TokenType == TokenType.DataList)
            {
                DataList merges = mergesToken.DataList;
                for (int i = 0; i < merges.Count; i++)
                {
                    if (merges[i].TokenType != TokenType.DataList || merges[i].DataList.Count < 4) continue;
                    DataList merge = merges[i].DataList;
                    int r = (int)PreviewNumber(merge[0]);
                    int c = (int)PreviewNumber(merge[1]);
                    int rh = (int)PreviewNumber(merge[2]);
                    int cw = (int)PreviewNumber(merge[3]);
                    if (r < 0 || c < 0 || r >= rowCount || c >= columnCount || rh < 1 || cw < 1) continue;
                    rh = Math.Min(rh, rowCount - r);
                    cw = Math.Min(cw, columnCount - c);
                    if (rh == 1 && cw == 1) continue;
                    bool overlaps = false;
                    for (int rr = r; rr < r + rh; rr++)
                    {
                        for (int cc = c; cc < c + cw; cc++)
                        {
                            int cell = rr * columnCount + cc;
                            if (covered[cell] || spanRows[cell] > 0) overlaps = true;
                        }
                    }
                    if (overlaps) continue;
                    spanRows[r * columnCount + c] = rh;
                    for (int rr = r; rr < r + rh; rr++)
                    {
                        for (int cc = c; cc < c + cw; cc++)
                        {
                            if (rr != r || cc != c) covered[rr * columnCount + cc] = true;
                        }
                    }
                }
            }
            int expected = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (!covered[i]) expected += ExpectedGlyphs(raw[i]);
            }
            return expected;
        }
        private static double PreviewNumber(DataToken token)
        {
            if (token.TokenType == TokenType.Int) return token.Int;
            if (token.TokenType == TokenType.Long) return token.Long;
            if (token.TokenType == TokenType.Double) return token.Double;
            return 0d;
        }
        private static bool AssertGlyphCount(UtsushiDisplay display, string label)
        {
            TMP_Text[] segments = PrivateValue(display, "segments") as TMP_Text[];
            TMP_Text[] overlays = PrivateValue(display, "overlays") as TMP_Text[];
            TMP_Text[] noWrap = PrivateValue(display, "overlaysNoWrap") as TMP_Text[];
            int segmentCount = (int)PrivateValue(display, "segmentCount");
            int overlayCount = (int)PrivateValue(display, "overlayCount");
            int noWrapCount = (int)PrivateValue(display, "overlayNoWrapCount");
            string[] raw = PrivateValue(display, "workRaw") as string[];
            bool[] covered = PrivateValue(display, "workCovered") as bool[];
            int rows = (int)PrivateValue(display, "workRows");
            int cols = (int)PrivateValue(display, "workCols");
            DataDictionary range = display.source == null ? null : display.source.GetRange(display.rangeName);
            int expected = ExpectedGlyphsFromData(range);
            int cells = 0;
            for (int i = 0; raw != null && i < rows * cols && i < raw.Length; i++)
            {
                if (covered != null && covered[i]) continue;
                cells++;
            }
            int visible = 0;
            int vertices = 0;
            int activeTexts = 0;
            for (int group = 0; group < 3; group++)
            {
                TMP_Text[] pool = group == 0 ? segments : (group == 1 ? overlays : noWrap);
                int used = group == 0 ? segmentCount : (group == 1 ? overlayCount : noWrapCount);
                for (int i = 0; pool != null && i < used && i < pool.Length; i++)
                {
                    TMP_Text text = pool[i];
                    if (text == null || !text.gameObject.activeInHierarchy) continue;
                    activeTexts++;
                    text.ForceMeshUpdate();
                    for (int k = 0; k < text.textInfo.characterCount && k < text.textInfo.characterInfo.Length; k++)
                        if (text.textInfo.characterInfo[k].isVisible) visible++;
                    if (group == 0 && text.textInfo.characterCount > 0) visible--;
                    for (int m = 0; m < text.textInfo.meshInfo.Length; m++) vertices += text.textInfo.meshInfo[m].vertexCount;
                }
            }
            visible = Mathf.Max(0, visible);
            Image[] images = display.tableArea.GetComponentsInChildren<Image>(false);
            int activeImages = 0;
            for (int i = 0; i < images.Length; i++) if (images[i] != null && images[i].gameObject.activeInHierarchy) { activeImages++; vertices += 4; }
            Image[] chipImages = PrivateValue(display, "chips") as Image[];
            int activeChips = 0;
            for (int i = 0; chipImages != null && i < chipImages.Length; i++) if (chipImages[i] != null && chipImages[i].gameObject.activeInHierarchy) activeChips++;
            int gameObjects = (int)PrivateValue(display, "cellCreatedCount") + (int)PrivateValue(display, "overlayCreatedCount") + (int)PrivateValue(display, "overlayNoWrapCreatedCount") + (int)PrivateValue(display, "segmentCreatedCount") + (int)PrivateValue(display, "chipCreatedCount") * 2;
            bool failed = visible != expected;
            Debug.Log("[Utsushi] stats " + label + " segments=" + segmentCount + " overlays=" + (overlayCount + noWrapCount) + " cells=" + cells + " chips=" + activeChips + " vertices=" + vertices + " gameObjects=" + gameObjects);
            if (failed) Debug.LogError("[Utsushi] glyph count violation: " + label + " visible=" + visible + " expected(from data)=" + expected + " tmps=" + activeTexts);
            if (label.StartsWith("escape/", StringComparison.Ordinal))
            {
                int[] segmentColumns = PrivateValue(display, "segmentColumn") as int[];
                int[] segmentFirst = PrivateValue(display, "segmentFirstRow") as int[];
                for (int cell = 0; raw != null && cell < rows * cols && cell < raw.Length; cell++)
                {
                    StringBuilder actual = new StringBuilder();
                    int row = cell / cols;
                    int col = cell % cols;
                    for (int group = 0; group < 3; group++)
                    {
                        TMP_Text[] pool = group == 0 ? segments : (group == 1 ? overlays : noWrap);
                        int used = group == 0 ? segmentCount : (group == 1 ? overlayCount : noWrapCount);
                        for (int i = 0; pool != null && i < used && i < pool.Length; i++)
                        {
                            TMP_Text text = pool[i];
                            if (text == null || !text.gameObject.activeInHierarchy) continue;
                            bool match = false;
                            int first = 0;
                            if (group == 0 && segmentColumns[i] == col && row >= segmentFirst[i] && row <= ((int[])PrivateValue(display, "segmentLastRow"))[i])
                            {
                                match = true;
                                first = segmentFirst[i];
                            }
                            else if (group != 0)
                            {
                                int overlayRow, overlayCol;
                                match = OverlayCell(display, text, out overlayRow, out overlayCol) && overlayRow == row && overlayCol == col;
                            }
                            if (!match) continue;
                            text.ForceMeshUpdate();
                            for (int k = 0; k < text.textInfo.characterCount && k < text.textInfo.characterInfo.Length; k++)
                            {
                                TMP_CharacterInfo character = text.textInfo.characterInfo[k];
                                if (group == 0 && (character.lineNumber == 0 || first + character.lineNumber - 1 != row)) continue;
                                char value = character.character;
                                if (value == '\u200B' || value == '\u25A1') failed = true;
                                if (!IsPreviewWhitespace(value) && value != '\u200B') actual.Append(value);
                            }
                        }
                    }
                    StringBuilder wanted = new StringBuilder();
                    for (int k = 0; k < raw[cell].Length; k++) if (!IsPreviewWhitespace(raw[cell][k])) wanted.Append(raw[cell][k]);
                    if (actual.ToString() != wanted.ToString())
                    {
                        failed = true;
                        Debug.LogError("[Utsushi] escape violation: " + label + " row=" + (row + 1) + " col=" + (col + 1) + " actual=\"" + actual + "\" expected=\"" + wanted + "\"");
                    }
                }
                if (failed) Debug.LogError("[Utsushi] escape violation: " + label + " contains an unexpected glyph");
            }
            return failed;
        }

        private static void RenderRange(UtsushiDisplay display, UtsushiSource source, string range, string path, Camera camera, RenderTexture renderTexture, Texture2D image)
        {
            display.rangeName = range;
            Canvas.ForceUpdateCanvases();
            display.RenderNow();
            string label = range + "/" + (display.lightTheme ? "light" : "dark");
            if (range.StartsWith("stress_", StringComparison.Ordinal))
            {
                Debug.Log("[Utsushi] layout checks skipped (stress): " + label);
            }
            else
            {
                if (AssertNoClipping(display, label) && !failedClipLabels.Contains(label)) failedClipLabels.Add(label);
                if (AssertChipLayout(display, source, range, label) && !failedChipLabels.Contains(label)) failedChipLabels.Add(label);
                if (AssertColumnVCenter(display, label))
                {
                    if (!failedChipLabels.Contains(label)) failedChipLabels.Add(label);
                }
            }
            if (AssertGlyphCount(display, label) && !failedGlyphLabels.Contains(label)) failedGlyphLabels.Add(label);
            // _Watch drives the status line at runtime; it schedules Udon events, so mirror it here.
            display.statusText.text = source.GetStatusText();
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = renderTexture;
            image.ReadPixels(new Rect(0, 0, 1600, 1000), 0, 0);
            image.Apply();
            File.WriteAllBytes(path, image.EncodeToPNG());
            Debug.Log("[Utsushi] Preview: " + path);
        }

    }
}
#endif
