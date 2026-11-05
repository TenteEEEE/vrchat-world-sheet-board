#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using SheetBoard;
using SheetBoardEditor;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.Udon;
using VRC.SDK3.Data;

namespace SheetBoardDev
{
    public static class SheetBoardPreview
    {
        private static readonly List<string> failedClipLabels = new List<string>();
        private static readonly List<string> failedChipLabels = new List<string>();
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
            SheetBoardDevMenu.RunBatch(Render);
        }
        internal static void RenderSample()
        {
            Render();
        }
        private static void Render()
        {
            failedClipLabels.Clear();
            failedChipLabels.Clear();
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
                string[] ranges = { "event", "wraptest", "fitcase", "chips" };
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
                if (failedClipLabels.Count > 0 || failedChipLabels.Count > 0)
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
                    throw new InvalidOperationException("[SheetBoard] preview checks failed for: " + failures);
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
            string[] fields = { "background", "titleText", "statusText", "tableArea", "columnTemplate", "overlayTemplate", "overlayNoWrapTemplate", "cellTemplate", "chipTemplate", "arrowTemplate", "statusDot", "refreshButton", "refreshLabel" };
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
                Material expectedTmp = AssetDatabase.LoadAssetAtPath<Material>(SheetBoardInstaller.RootDir + "Generated/SheetBoard TMP Material.mat");
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
        private static bool IsKnownOverflow(SheetBoardDisplay display, int row, int col)
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
        private static bool AssertNoClipping(SheetBoardDisplay display, string label)
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
            TMP_Text[] columns = PrivateValue(display, "columns") as TMP_Text[];
            int glyphs = 0;
            int violations = 0;
            int exempt = 0;
            for (int t = 0; t < texts.Length; t++)
            {
                TMP_Text text = texts[t];
                if (text == null || !text.gameObject.activeInHierarchy) continue;
                int columnIndex = ColumnIndex(columns, text);
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
                    int row = isColumn ? character.lineNumber - 1 : overlayRow;
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
                            Debug.LogError("[SheetBoard] clip violation: " + label + ", theme=" + (display.lightTheme ? "light" : "dark") + location + ", cell=\"" + cell + "\", tmp=\"" + text.gameObject.name + "\", character=\"" + character.character + "\", line=" + character.lineNumber + ", axis=" + axis + ", overflow=" + overflow.ToString("0.0") + "px, glyph=" + characterRect + ", mask=" + maskRect + ", table=" + tableRect);
                        }
                    }
                }
            }
            if (exempt > 0) Debug.Log("[SheetBoard] " + label + ": exempt " + exempt + " glyphs (known overflow)");
            if (violations == 0) Debug.Log("[SheetBoard] clip check OK: " + label + " (" + glyphs + " glyphs)");
            return violations > 0;
        }

        private static int ColumnIndex(TMP_Text[] columns, TMP_Text text)
        {
            if (columns == null) return -1;
            for (int i = 0; i < columns.Length; i++)
                if (columns[i] == text) return i;
            return -1;
        }

        private static bool OverlayCell(SheetBoardDisplay display, TMP_Text text, out int row, out int col)
        {
            row = -1;
            col = -1;
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

        private static string CellAt(SheetBoardDisplay display, int row, int col)
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

        private static bool AssertChipLayout(SheetBoardDisplay display, SheetBoardSource source, string rangeName, string label)
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
            TMP_Text[] columns = PrivateValue(display, "columns") as TMP_Text[];
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
                Debug.LogError("[SheetBoard] chip count violation: " + label + ", chips=" + actualChips + "/" + expectedChips + ", arrows=" + actualArrows + "/" + expectedArrows);
            }
            if (chipLayer == null || arrowLayer == null || chipLayer.GetSiblingIndex() >= arrowLayer.GetSiblingIndex()) failed = true;
            Image[] cells = PrivateValue(display, "cells") as Image[];
            if (cells != null)
                for (int i = 0; i < cells.Length; i++)
                    if (cells[i] != null && cells[i].gameObject.activeInHierarchy && chipLayer != null && cells[i].transform.GetSiblingIndex() >= chipLayer.GetSiblingIndex()) failed = true;
            if (columns != null && arrowLayer != null)
                for (int i = 0; i < columns.Length; i++)
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
                                Debug.LogError("[SheetBoard] chip palette fill violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1));
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
                                                Debug.LogError("[SheetBoard] repeated chip name color violation: " + label + ", name=" + normalized);
                                            }
                                        }
                                        if (paletteNames[n] != normalized && display.chipPalette != null && paletteNameCount < display.chipPalette.Length && paletteColors[n] == chip.color)
                                        {
                                            failed = true;
                                            Debug.LogError("[SheetBoard] distinct chip name color violation: " + label + ", name=" + normalized);
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
                                    Debug.LogError("[SheetBoard] blank chip palette violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1));
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
                                int textColumn = ColumnIndex(columns, text);
                                int overlayRow = -1;
                                int overlayCol = -1;
                                bool isColumn = textColumn >= 0;
                                if (isColumn) overlayCol = textColumn;
                                else OverlayCell(display, text, out overlayRow, out overlayCol);
                                if (overlayCol != c || (!isColumn && overlayRow != r)) continue;
                                text.ForceMeshUpdate();
                                TMP_CharacterInfo[] chars = text.textInfo.characterInfo;
                                for (int k = 0; k < text.textInfo.characterCount && k < chars.Length; k++)
                                {
                                    TMP_CharacterInfo character = chars[k];
                                    if (!character.isVisible || (isColumn && character.lineNumber - 1 != r)) continue;
                                    Vector3 a = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.bottomLeft));
                                    Vector3 b = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.bottomRight));
                                    Vector3 glyphTop = display.tableArea.InverseTransformPoint(text.transform.TransformPoint(character.topLeft));
                                    visibleBottom = Mathf.Min(visibleBottom, a.y);
                                    visibleTop = Mathf.Max(visibleTop, glyphTop.y);
                                    hasVisibleGlyph = true;
                                    if (Mathf.Min(a.x, b.x) < arrowRight && Mathf.Max(a.x, b.x) > arrowLeft && !IsKnownOverflow(display, r, c)) failed = true;
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
                                    Debug.LogError("[SheetBoard] chip vcenter violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1) + ", delta=" + delta.ToString("0.0") + "px, tolerance=" + tolerance.ToString("0.0") + "px");
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
            if (rangeName == "chips") Debug.Log("[SheetBoard] chip vcenter max delta: " + maxVCenterDelta.ToString("0.0") + " px");
            if (failed) Debug.LogError("[SheetBoard] chip layout violation: " + label);
            else Debug.Log("[SheetBoard] chip check OK: " + label);
            return failed;
        }

        private static bool AssertColumnVCenter(SheetBoardDisplay display, string label)
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
            TMP_Text[] columns = PrivateValue(display, "columns") as TMP_Text[];
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
                    TMP_Text text = columns != null && c < columns.Length ? columns[c] : null;
                    if (text == null || !text.gameObject.activeInHierarchy) continue;
                    text.ForceMeshUpdate();
                    TMP_CharacterInfo[] chars = text.textInfo.characterInfo;
                    float visibleBottom = float.PositiveInfinity;
                    float visibleTop = float.NegativeInfinity;
                    bool hasVisibleGlyph = false;
                    for (int k = 0; k < text.textInfo.characterCount && k < chars.Length; k++)
                    {
                        TMP_CharacterInfo character = chars[k];
                        if (!character.isVisible || character.lineNumber - 1 != r) continue;
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
                        Debug.LogError("[SheetBoard] column vcenter violation: " + label + ", row=" + (r + 1) + ", col=" + (c + 1) + ", delta=" + delta.ToString("0.0") + "px, tolerance=" + tolerance.ToString("0.0") + "px");
                    }
                }
            }
            Debug.Log("[SheetBoard] column vcenter max delta: " + maxDelta.ToString("0.0") + " px: " + label);
            if (!failed) Debug.Log("[SheetBoard] column vcenter check OK: " + label);
            return failed;
        }

        private static void RenderRange(SheetBoardDisplay display, SheetBoardSource source, string range, string path, Camera camera, RenderTexture renderTexture, Texture2D image)
        {
            display.rangeName = range;
            Canvas.ForceUpdateCanvases();
            display.RenderNow();
            string label = range + "/" + (display.lightTheme ? "light" : "dark");
            if (AssertNoClipping(display, label) && !failedClipLabels.Contains(label)) failedClipLabels.Add(label);
            if (AssertChipLayout(display, source, range, label) && !failedChipLabels.Contains(label)) failedChipLabels.Add(label);
            if (AssertColumnVCenter(display, label))
            {
                if (!failedChipLabels.Contains(label)) failedChipLabels.Add(label);
            }
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
