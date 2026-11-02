#if UNITY_EDITOR
using System;
using SheetBoard;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SheetBoardEditor
{
    [CustomEditor(typeof(SheetBoardDisplay))]
    public class SheetBoardDisplayEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();
            SerializedProperty iterator = serializedObject.GetIterator();
            bool children = true;
            while (iterator.NextVisible(children))
            {
                children = false;
                if (iterator.name == "rangeName") { DrawRange(iterator); continue; }
                if (iterator.name == "textScale") { EditorGUILayout.Slider(iterator, 0.5f, 3f, Label(iterator.name)); continue; }
                using (new EditorGUI.DisabledScope(iterator.name == "m_Script")) EditorGUILayout.PropertyField(iterator, Label(iterator.name), true);
            }
            serializedObject.ApplyModifiedProperties();
            SheetBoardDisplay display = (SheetBoardDisplay)target;
            if (display.source == null)
            {
                SheetBoardSource only = null;
                int count = 0;
                foreach (SheetBoardSource source in UnityEngine.Object.FindObjectsByType<SheetBoardSource>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (source.gameObject.scene == display.gameObject.scene) { only = source; count++; }
                if (count == 1 && GUILayout.Button("自動で設定"))
                {
                    if (!SheetBoardInstaller.CheckProgramsCompiled()) return;
                    Undo.RecordObject(display, "Assign Sheet Board Source");
                    display.source = only;
                    UdonSharpEditorUtility.CopyProxyToUdon(display);
                    EditorUtility.SetDirty(display);
                    if (PrefabUtility.IsPartOfPrefabInstance(display)) PrefabUtility.RecordPrefabInstancePropertyModifications(display);
                }
                if (count > 1) EditorGUILayout.HelpBox("Source が複数あります。データ元を手動で指定してください。", MessageType.Info);
            }
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
            if (string.IsNullOrEmpty(property.stringValue)) EditorGUILayout.LabelField("（空欄 = 最初の範囲）", EditorStyles.miniLabel);
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
                case "bodyColor": return new GUIContent("本文の色");
                case "headerBackgroundColor": return new GUIContent("見出し背景の色");
                case "zebraColor": return new GUIContent("交互の行背景色");
                case "normalColor": return new GUIContent("正常の色");
                case "staleColor": return new GUIContent("古い状態の色");
                case "errorColor": return new GUIContent("エラーの色");
                case "rangeMissingText": return new GUIContent("範囲がない場合の文言");
                case "sourceMissingText": return new GUIContent("データ元がない場合の文言");
                default: return new GUIContent(name);
            }
        }
    }
}
#endif
