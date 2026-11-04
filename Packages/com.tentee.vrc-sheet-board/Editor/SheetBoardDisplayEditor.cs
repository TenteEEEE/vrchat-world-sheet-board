#if UNITY_EDITOR
using System;
using SheetBoard;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace SheetBoardEditor
{
    [CustomEditor(typeof(SheetBoardDisplay))]
    public class SheetBoardDisplayEditor : Editor
    {
        private bool showLayout;
        private bool showColors;
        private bool showTexts;
        private bool showAdvanced;
        private bool showInternal;
        private SheetBoardSource[] sourceCandidates = Array.Empty<SheetBoardSource>();

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
            sourceCandidates = UnityEngine.Object.FindObjectsByType<SheetBoardSource>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();
            SheetBoardDisplay display = (SheetBoardDisplay)target;
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("source"), Label("source"));
            DrawRange(serializedObject.FindProperty("rangeName"));
            if (display.source == null)
            {
                SheetBoardSource only = null;
                int count = 0;
                foreach (SheetBoardSource source in sourceCandidates)
                {
                    if (source.gameObject.scene != display.gameObject.scene) continue;
                    only = source;
                    count++;
                }
                if (count == 1 && GUILayout.Button("同じシーンの Source を自動設定"))
                {
                    serializedObject.ApplyModifiedProperties();
                    EditorApplication.delayCall += () => AssignSource(display, only);
                    GUIUtility.ExitGUI();
                }
                if (count > 1)
                    EditorGUILayout.HelpBox("Source が複数あります。自動選択しないため、上の欄で指定してください。", MessageType.Info);
                if (count == 0)
                    EditorGUILayout.HelpBox("Source が未設定です。Source を作成するか、上の欄で指定してください。", MessageType.Info);
            }
            else if (GUILayout.Button("Source Inspector を開く")) Selection.activeObject = display.source;
            EditorGUILayout.HelpBox("範囲名が空欄の場合、Source の先頭の範囲を表示します。", MessageType.None);
            serializedObject.ApplyModifiedProperties();
            showLayout = EditorGUILayout.Foldout(showLayout, "表示とレイアウト（任意）", true);
            if (showLayout)
            {
                Draw("lightTheme");
                Draw("textScale");
                Draw("autoFitColumns");
                Draw("maxRows");
                Draw("maxCols");
                Draw("showTitle");
                Draw("showGrid");
                Draw("zebra");
                Draw("maxRowHeight");
                using (new EditorGUI.DisabledScope(serializedObject.FindProperty("showGrid").boolValue))
                    Draw("columnGap");
            }
            showColors = EditorGUILayout.Foldout(showColors, "色（任意）", true);
            if (showColors)
            {
                Draw("boardColor");
                Draw("bodyColor");
                Draw("gridColor");
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
            showTexts = EditorGUILayout.Foldout(showTexts, "文言（任意）", true);
            if (showTexts)
            {
                Draw("rangeMissingText");
                Draw("sourceMissingText");
                Draw("refreshIdleText");
            }
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "詳細設定", true);
            if (showAdvanced)
            {
                using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("showGrid").boolValue)) Draw("gridWidth");
                Draw("maxOverlays");
                Draw("nearCheckInterval");
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
                Draw("overlayTemplate");
                Draw("overlayNoWrapTemplate");
                Draw("statusDot");
                Draw("refreshButton");
                Draw("refreshLabel");
            }
            serializedObject.ApplyModifiedProperties();
        }
        private static void AssignSource(SheetBoardDisplay display, SheetBoardSource source)
        {
            if (!SheetBoardInstaller.CheckProgramsCompiled()) return;
            SheetBoardInstaller.RecordProxyUndo(display, "Assign Sheet Board Source");
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
            SheetBoardSource source = sourceProperty.objectReferenceValue as SheetBoardSource;
            string[] ranges = SheetBoardSourceEditor.GetRanges(source);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(property, new GUIContent("範囲名"));
            if (ranges.Length > 0)
            {
                string[] choices = new string[ranges.Length + 1];
                choices[0] = "選択…";
                Array.Copy(ranges, 0, choices, 1, ranges.Length);
                int selected = EditorGUILayout.Popup(0, choices, GUILayout.Width(130));
                if (selected > 0) property.stringValue = ranges[selected - 1];
            }
            EditorGUILayout.EndHorizontal();
            if (ranges.Length > 0)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                int selectedIndex = Array.IndexOf(ranges, property.stringValue);
                string rangeStatus = string.IsNullOrEmpty(property.stringValue)
                    ? "（空欄 = 最初の範囲）"
                    : selectedIndex < 0 ? "未確認の範囲名" : "確認済みの範囲名";
                EditorGUILayout.LabelField(rangeStatus, EditorStyles.miniLabel, GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();
            }
        }
        private static GUIContent Label(string name)
        {
            switch (name)
            {
                case "source": return new GUIContent("データ元");
                case "lightTheme": return new GUIContent("明るいテーマ（シート風）");
                case "boardColor": return new GUIContent("板の色");
                case "gridColor": return new GUIContent("格子の色");
                case "showGrid": return new GUIContent("格子を表示");
                case "gridWidth": return new GUIContent("格子の太さ（px）");
                case "maxOverlays": return new GUIContent("重ね表示の上限");
                case "background": return new GUIContent("背景");
                case "overlayTemplate": return new GUIContent("重ね表示テンプレート");
                case "overlayNoWrapTemplate": return new GUIContent("重ね表示テンプレート（折り返しなし）");
                case "textScale": return new GUIContent("文字の拡大率");
                case "autoFitColumns": return new GUIContent("列幅を自動調整", "文字が列に収まるよう列幅と文字サイズを自動で決める");
                case "maxRows": return new GUIContent("最大行数");
                case "maxCols": return new GUIContent("最大列数");
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
                case "sourceMissingText": return new GUIContent("Source がないとき");
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
