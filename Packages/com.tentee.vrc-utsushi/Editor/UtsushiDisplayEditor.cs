#if UNITY_EDITOR
using System;
using Utsushi;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UtsushiEditor
{
    [CustomEditor(typeof(UtsushiDisplay))]
    [CanEditMultipleObjects]
    public class UtsushiDisplayEditor : Editor
    {
        private bool showLayout;
        private bool showColors;
        private bool showTexts;
        private bool showAdvanced;
        private bool showInternal;
        private UtsushiSource[] sourceCandidates = Array.Empty<UtsushiSource>();

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += RefreshSourceCandidates;
            RefreshSourceCandidates();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= RefreshSourceCandidates;
        }

        private void RefreshSourceCandidates()
        {
            sourceCandidates = UnityEngine.Object.FindObjectsByType<UtsushiSource>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();
            UtsushiDisplay display = (UtsushiDisplay)target;
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            if (targets.Length > 1) EditorGUILayout.HelpBox("複数選択中は表示しません", MessageType.Info);
            else if (display.source != null)
            {
                UtsushiSourceEditor.DrawUrlSection(display.source);
                int shared = 0;
                foreach (UtsushiDisplay other in UnityEngine.Object.FindObjectsByType<UtsushiDisplay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (other.source == display.source && other.gameObject.scene.IsValid() && other.gameObject.scene.isLoaded) shared++;
                if (shared > 1) EditorGUILayout.LabelField("このデータ元を使う掲示板（" + shared + " 枚）すべてに反映されます", EditorStyles.miniLabel);
            }
            EditorGUILayout.PropertyField(serializedObject.FindProperty("source"), Label("source"));
            if (targets.Length == 1) DrawRange(serializedObject.FindProperty("rangeName"));
            else EditorGUILayout.PropertyField(serializedObject.FindProperty("rangeName"), new GUIContent("表示する範囲"));
            if (targets.Length > 1)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(UtsushiInstaller.SourcePrefab);
                bool canCreate = PrefabStageUtility.GetCurrentPrefabStage() == null;
                foreach (UtsushiDisplay selected in targets) if (!selected.gameObject.scene.IsValid()) canCreate = false;
                if (canCreate && prefab == null) EditorGUILayout.HelpBox("Utsushi の部品が見つかりません。パッケージを入れ直してください。", MessageType.Warning);
                using (new EditorGUI.DisabledScope(prefab == null))
                if (canCreate && GUILayout.Button("データ元を作ってつなぐ"))
                {
                    serializedObject.ApplyModifiedProperties();
                    if (UtsushiInstaller.CheckProgramsCompiled()) foreach (UtsushiDisplay selected in targets)
                    {
                        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, selected.gameObject.scene);
                        Undo.RegisterCreatedObjectUndo(instance, "Create Utsushi Source");
                        AssignSource(selected, instance.GetComponent<UtsushiSource>());
                    }
                    GUIUtility.ExitGUI();
                }
            }
            if (display.source == null && targets.Length == 1)
            {
                UtsushiSource only = null;
                int count = 0;
                foreach (UtsushiSource source in sourceCandidates)
                {
                    if (source.gameObject.scene != display.gameObject.scene) continue;
                    only = source;
                    count++;
                }
                if (count == 1 && GUILayout.Button("同じシーンのデータ元につなぐ"))
                {
                    serializedObject.ApplyModifiedProperties();
                    EditorApplication.delayCall += () => AssignSource(display, only);
                    GUIUtility.ExitGUI();
                }
                if (count > 1) EditorGUILayout.HelpBox("データ元が複数あります。上の欄で選んでください。", MessageType.Warning);
                GameObject sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(UtsushiInstaller.SourcePrefab);
                bool canCreate = display.gameObject.scene.IsValid() && PrefabStageUtility.GetCurrentPrefabStage() == null;
                if (count == 0 && canCreate && sourcePrefab == null)
                    EditorGUILayout.HelpBox("Utsushi の部品が見つかりません。パッケージを入れ直してください。", MessageType.Warning);
                using (new EditorGUI.DisabledScope(sourcePrefab == null))
                if (count == 0 && canCreate && GUILayout.Button("データ元を作ってつなぐ"))
                {
                    serializedObject.ApplyModifiedProperties();
                    if (UtsushiInstaller.CheckProgramsCompiled())
                    {
                        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab, display.gameObject.scene);
                        Undo.RegisterCreatedObjectUndo(instance, "Create Utsushi Source");
                        AssignSource(display, instance.GetComponent<UtsushiSource>());
                    }
                    GUIUtility.ExitGUI();
                }
            }
            else if (targets.Length == 1 && display.source != null && GUILayout.Button("データ元の詳細設定を開く", EditorStyles.miniButton)) Selection.activeObject = display.source;
            serializedObject.ApplyModifiedProperties();
            DrawBoardSize(display);
            SerializedProperty themeProperty = serializedObject.FindProperty("lightTheme");
            int theme = themeProperty.boolValue ? 0 : 1;
            EditorGUI.showMixedValue = themeProperty.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();
            theme = EditorGUILayout.Popup("テーマ", theme, new[] { "ライト", "ダーク" });
            bool themeChanged = EditorGUI.EndChangeCheck();
            EditorGUI.showMixedValue = false;
            if (themeChanged)
            {
                serializedObject.ApplyModifiedProperties();
                foreach (UtsushiDisplay selected in targets)
                {
                UtsushiInstaller.RecordProxyUndo(selected, "Change Utsushi Theme");
                // The theme also recolours child graphics; record them so Undo and prefab overrides keep the change.
                UnityEngine.Object[] themed = ThemedObjects(selected);
                if (themed.Length > 0) Undo.RecordObjects(themed, "Change Utsushi Theme");
                UtsushiInstaller.ApplyTheme(selected, theme == 0);
                UdonSharpEditorUtility.CopyProxyToUdon(selected);
                EditorUtility.SetDirty(selected);
                if (PrefabUtility.IsPartOfPrefabInstance(selected)) PrefabUtility.RecordPrefabInstancePropertyModifications(selected);
                foreach (UnityEngine.Object item in themed) RecordSizeChange(item);
                }
                serializedObject.Update();
            }
            showLayout = EditorGUILayout.Foldout(showLayout, "表示とレイアウト（任意）", true);
            if (showLayout)
            {
                Draw("textScale");
                Draw("autoFitColumns");
                Draw("showTitle");
                Draw("showGrid");
                Draw("zebra");
            }
            showColors = EditorGUILayout.Foldout(showColors, "色のカスタマイズ（任意。未設定ならテーマの色）", true);
            if (showColors)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("chipAutoPalette"), new GUIContent("チップを名前ごとに色分け", "シートから色が読めないチップに、同じ名前なら同じ色を自動で付けます"));
                Draw("boardColor");
                Draw("bodyColor");
                Draw("gridColor");
                EditorGUILayout.PropertyField(serializedObject.FindProperty("chipColor"), new GUIContent("チップの色"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("chipArrowColor"), new GUIContent("▼ の色"));
                Draw("headerBackgroundColor");
                using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("zebra").boolValue))
                    Draw("zebraColor");
                Draw("normalColor");
                Draw("staleColor");
                Draw("errorColor");
                Draw("busyColor");
                Draw("refreshButtonColor");
                Draw("refreshLabelColor");
                Draw("refreshDisabledColor");
                Draw("refreshDisabledLabelColor");
            }
            showTexts = EditorGUILayout.Foldout(showTexts, "表示言語と文言（任意）", true);
            if (showTexts)
            {
                Draw("rangeMissingText");
                Draw("sourceMissingText");
                Draw("refreshIdleText");
            }
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "詳細設定", true);
            if (showAdvanced)
            {
                Draw("maxRowHeight");
                using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("showGrid").boolValue)) Draw("gridWidth");
                Draw("nearCheckInterval");
                using (new EditorGUI.DisabledScope(serializedObject.FindProperty("showGrid").boolValue)) Draw("columnGap");
            }
            showInternal = EditorGUILayout.Foldout(showInternal, "内部参照（通常は変更不要）", true);
            if (showInternal)
            {
                EditorGUILayout.HelpBox("通常の編集では変更不要です。", MessageType.Info);
                Draw("background");
                Draw("titleText");
                Draw("statusText");
                Draw("tableArea");
                Draw("columnTemplate");
                Draw("cellTemplate");
                Draw("chipTemplate");
                Draw("arrowTemplate");
                Draw("overlayTemplate");
                Draw("overlayNoWrapTemplate");
                Draw("statusDot");
                Draw("refreshButton");
                Draw("refreshLabel");
            }
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawBoardSize(UtsushiDisplay display)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("掲示板のサイズ", EditorStyles.boldLabel);
            Canvas canvas = display.tableArea != null
                ? display.tableArea.GetComponentInParent<Canvas>(true)
                : display.GetComponentInChildren<Canvas>(true);
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
            {
                EditorGUILayout.HelpBox("サイズ調整には、ワールド空間の Canvas が必要です。", MessageType.Info);
                return;
            }

            RectTransform rect = (RectTransform)canvas.transform;
            Vector2 size = rect.rect.size;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                Vector3 scale = rect.lossyScale;
                float sx = Mathf.Abs(scale.x), sy = Mathf.Abs(scale.y);
                using (new EditorGUI.DisabledScope(sx < 0.000001f || sy < 0.000001f))
                {
                EditorGUI.BeginChangeCheck();
                float width = EditorGUILayout.DelayedFloatField(new GUIContent("幅（m）", "最小 " + (640f * sx).ToString("0.###") + " m。背景・表の領域・当たり判定も一緒に調整します。"), size.x * sx);
                float height = EditorGUILayout.DelayedFloatField(new GUIContent("高さ（m）", "最小 " + (320f * sy).ToString("0.###") + " m。縦長の表は高さを大きくしてください。"), size.y * sy);
                bool changed = EditorGUI.EndChangeCheck();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("横長（標準）")) { width = 1600f * sx; height = 1000f * sy; changed = true; }
                if (GUILayout.Button("縦長")) { width = 1000f * sx; height = 1800f * sy; changed = true; }
                if (GUILayout.Button("正方形")) { width = 1200f * sx; height = 1200f * sy; changed = true; }
                EditorGUILayout.EndHorizontal();
                if (changed && !float.IsNaN(width) && !float.IsInfinity(width)
                    && !float.IsNaN(height) && !float.IsInfinity(height))
                    foreach (UtsushiDisplay selected in targets)
                    {
                        Canvas selectedCanvas = selected.tableArea != null ? selected.tableArea.GetComponentInParent<Canvas>(true) : selected.GetComponentInChildren<Canvas>(true);
                        if (selectedCanvas == null || selectedCanvas.renderMode != RenderMode.WorldSpace) continue;
                        Vector3 selectedScale = selectedCanvas.transform.lossyScale;
                        float x = Mathf.Abs(selectedScale.x), y = Mathf.Abs(selectedScale.y);
                        if (x < 0.000001f || y < 0.000001f) continue;
                        ApplyBoardSize(selectedCanvas, new Vector2(Mathf.Max(640f, width / x), Mathf.Max(320f, height / y)));
                    }
                }
            }
            EditorGUILayout.HelpBox("形を変えるときはここで指定します。全体を大きく・小さくするときは、オブジェクトのスケールを変えてください。表全体が板に収まるので、行が多い表は高さを増やすか、範囲を分けて複数の掲示板に出してください。", MessageType.None);
        }

        internal static void ApplyBoardSize(Canvas canvas, Vector2 size)
        {
            RectTransform rect = (RectTransform)canvas.transform;
            BoxCollider collider = canvas.GetComponent<BoxCollider>();
            const string undoName = "Resize Utsushi";
            Undo.RecordObject(rect, undoName);
            if (collider != null) Undo.RecordObject(collider, undoName);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size.x);
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, size.y);
            if (collider != null)
            {
                collider.size = new Vector3(size.x, size.y, collider.size.z);
                collider.center = new Vector3((0.5f - rect.pivot.x) * size.x, (0.5f - rect.pivot.y) * size.y, collider.center.z);
                RecordSizeChange(collider);
            }
            RecordSizeChange(rect);
            SceneView.RepaintAll();
        }

        private static UnityEngine.Object[] ThemedObjects(UtsushiDisplay display)
        {
            System.Collections.Generic.List<UnityEngine.Object> items = new System.Collections.Generic.List<UnityEngine.Object>();
            if (display.background != null) items.Add(display.background);
            if (display.titleText != null) items.Add(display.titleText);
            if (display.statusText != null) items.Add(display.statusText);
            if (display.statusDot != null) items.Add(display.statusDot);
            if (display.refreshLabel != null) items.Add(display.refreshLabel);
            if (display.refreshButton != null)
            {
                UnityEngine.UI.Image image = display.refreshButton.GetComponent<UnityEngine.UI.Image>();
                if (image != null) items.Add(image);
            }
            return items.ToArray();
        }

        private static void RecordSizeChange(UnityEngine.Object component)
        {
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component))
                PrefabUtility.RecordPrefabInstancePropertyModifications(component);
        }

        private static void AssignSource(UtsushiDisplay display, UtsushiSource source)
        {
            if (!UtsushiInstaller.CheckProgramsCompiled()) return;
            UtsushiInstaller.RecordProxyUndo(display, "Assign Utsushi Source");
            display.source = source;
            UdonSharpEditorUtility.CopyProxyToUdon(display);
            EditorUtility.SetDirty(display);
            if (PrefabUtility.IsPartOfPrefabInstance(display)) PrefabUtility.RecordPrefabInstancePropertyModifications(display);
        }
        private void Draw(string name)
        {
            SerializedProperty property = serializedObject.FindProperty(name);
            if (property == null) return;
            if (name == "textScale") EditorGUILayout.Slider(property, 0.5f, 3f, Label(name));
            else EditorGUILayout.PropertyField(property, Label(name), true);
        }
        private void DrawRange(SerializedProperty property)
        {
            SerializedProperty sourceProperty = serializedObject.FindProperty("source");
            UtsushiSource source = sourceProperty.objectReferenceValue as UtsushiSource;
            string[] ranges = UtsushiSourceEditor.GetRanges(source);
            if (ranges.Length > 0)
            {
                int missing = Array.IndexOf(ranges, property.stringValue) < 0 && !string.IsNullOrEmpty(property.stringValue) ? 1 : 0;
                string[] choices = new string[ranges.Length + 1 + missing];
                choices[0] = "（自動: 一番上の範囲）";
                Array.Copy(ranges, 0, choices, 1, ranges.Length);
                if (missing != 0) choices[choices.Length - 1] = property.stringValue + "（シートにありません）";
                int selected = string.IsNullOrEmpty(property.stringValue) ? 0 : Array.IndexOf(ranges, property.stringValue) + 1;
                if (missing != 0 && selected == 0) selected = choices.Length - 1;
                selected = EditorGUILayout.Popup(new GUIContent("表示する範囲", "シートの設定タブ A 列「名前」です"), selected, choices);
                property.stringValue = selected == 0 ? "" : (missing != 0 && selected == choices.Length - 1 ? property.stringValue : ranges[selected - 1]);
                if (missing != 0) EditorGUILayout.HelpBox("「" + property.stringValue + "」はシートに見つかりません。シートの設定タブ A 列「名前」と同じ文字にしてください。", MessageType.Warning);
            }
            else EditorGUILayout.PropertyField(property, new GUIContent("表示する範囲", "シートの設定タブ A 列「名前」です。空欄なら一番上の範囲"));
        }
        private static GUIContent Label(string name)
        {
            switch (name)
            {
                case "source": return new GUIContent("データ元");
                case "boardColor": return new GUIContent("板の色");
                case "gridColor": return new GUIContent("格子の色");
                case "showGrid": return new GUIContent("格子を表示");
                case "gridWidth": return new GUIContent("格子の太さ（px）");
                case "background": return new GUIContent("背景");
                case "overlayTemplate": return new GUIContent("重ね表示テンプレート");
                case "overlayNoWrapTemplate": return new GUIContent("重ね表示テンプレート（折り返しなし）");
                case "textScale": return new GUIContent("文字の拡大率");
                case "autoFitColumns": return new GUIContent("列幅を自動調整", "文字が列に収まるよう列幅と文字サイズを自動で決める");
                case "showTitle": return new GUIContent("タイトルを表示");
                case "zebra": return new GUIContent("交互の行背景");
                case "nearCheckInterval": return new GUIContent("近接確認間隔（秒）");
                case "maxRowHeight": return new GUIContent("行の最大高さ");
                case "columnGap": return new GUIContent("列間の余白（格子なしの場合のみ）");
                case "titleText": return new GUIContent("タイトル表示");
                case "statusText": return new GUIContent("状態表示");
                case "tableArea": return new GUIContent("表の領域");
                case "columnTemplate": return new GUIContent("列テンプレート");
                case "cellTemplate": return new GUIContent("セル背景テンプレート");
                case "statusDot": return new GUIContent("状態ランプ");
                case "refreshButton": return new GUIContent("更新ボタン");
                case "refreshLabel": return new GUIContent("更新ボタンの文字");
                case "refreshIdleText": return new GUIContent("更新ボタンの文言");
                case "rangeMissingText": return new GUIContent("範囲が見つからないとき");
                case "sourceMissingText": return new GUIContent("データ元がないとき");
                case "bodyColor": return new GUIContent("本文の色");
                case "headerBackgroundColor": return new GUIContent("見出し背景の色");
                case "zebraColor": return new GUIContent("交互の行背景色");
                case "normalColor": return new GUIContent("正常の色");
                case "staleColor": return new GUIContent("古い状態の色");
                case "errorColor": return new GUIContent("エラーの色");
                case "busyColor": return new GUIContent("更新中のランプ色");
                case "refreshButtonColor": return new GUIContent("更新ボタンの色");
                case "refreshLabelColor": return new GUIContent("更新ボタンの文字色");
                case "refreshDisabledColor": return new GUIContent("押せない間のボタン色");
                case "refreshDisabledLabelColor": return new GUIContent("押せない間の文字色");
                default: return new GUIContent(name);
            }
        }
    }
}
#endif
