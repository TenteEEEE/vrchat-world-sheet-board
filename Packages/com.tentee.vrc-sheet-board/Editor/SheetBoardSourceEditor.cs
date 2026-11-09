#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SheetBoard;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;

namespace SheetBoardEditor
{
    [CustomEditor(typeof(SheetBoardSource))]
    public class SheetBoardSourceEditor : Editor
    {
        internal sealed class Result
        {
            internal string url;
            internal bool pending;
            internal string message = "確認中…";
            internal MessageType type = MessageType.Info;
            internal string[] ranges = Array.Empty<string>();
        }
        private const string FallbackUserAgentVersion = "0.1.0";
        private static readonly Dictionary<int, Result> Results = new Dictionary<int, Result>();
        private static readonly string[] DefaultHosts = { "*.github.io", "pastebin.com", "gist.githubusercontent.com" };
        private bool showUpdate;
        private bool showPermission;
        private bool showTexts;
        private UdonBehaviour[] controllerCandidates = Array.Empty<UdonBehaviour>();

        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += RefreshControllerCandidates;
            RefreshControllerCandidates();
        }

        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= RefreshControllerCandidates;
        }

        private void RefreshControllerCandidates()
        {
            controllerCandidates = UnityEngine.Object.FindObjectsByType<UdonBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }
        internal static string[] GetRanges(SheetBoardSource source)
        {
            if (source != null && Results.TryGetValue(source.GetInstanceID(), out Result result) &&
                result.url == NormalizedUrl(source.baseUrl) && result.type != MessageType.Error)
                return result.ranges;
            return Array.Empty<string>();
        }
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();
            SheetBoardSource source = (SheetBoardSource)target;
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("baseUrl"), new GUIContent("ベース URL"));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty("language"), new GUIContent("表示言語（自動 = 見る人の VRChat の言語）"));
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
            EditorGUILayout.HelpBox("通常はURLを貼り付けて適用し、接続を確認するだけで始められます。", MessageType.Info);
            if (!UrlsMatch(source)) EditorGUILayout.HelpBox("URL は未適用、または生成済み URL と一致しません。", MessageType.Warning);
            if (GUILayout.Button("URL を適用"))
            {
                serializedObject.ApplyModifiedProperties();
                EditorApplication.delayCall += () => ApplyUrls(source);
                GUIUtility.ExitGUI();
            }
            using (new EditorGUI.DisabledScope(Results.TryGetValue(source.GetInstanceID(), out Result pending) && pending.pending))
                if (GUILayout.Button("接続を確認"))
                {
                    serializedObject.ApplyModifiedProperties();
                    EditorApplication.delayCall += () => StartCheck(source);
                    GUIUtility.ExitGUI();
                }
            if (Results.TryGetValue(source.GetInstanceID(), out Result result))
            {
                if (result.url != NormalizedUrl(source.baseUrl)) EditorGUILayout.HelpBox("URL が変わりました。接続を再確認してください。", MessageType.Warning);
                else EditorGUILayout.HelpBox(result.message, result.type);
                if (result.pending) Repaint();
            }
            bool isRestricted = source.audience != SheetBoardAudience.Public;
            GetControllerCandidateSummary(source, out bool assignedValid, out int candidateCount, out UdonBehaviour onlyCandidate);
            if (isRestricted && !assignedValid)
                EditorGUILayout.HelpBox(
                    "現在の制限付き設定に有効な PermissionController がありません。参照を修正するか、閲覧権限を全員に戻してください。",
                    MessageType.Error);
            string audienceSummary = AudienceLabel(source.audience);
            showPermission = EditorGUILayout.Foldout(showPermission, "ワールド内の閲覧権限（現在: " + audienceSummary + "）", true);
            if (showPermission)
            {
                EditorGUILayout.HelpBox(
                    "このSourceの、ワールド内での読み込みと表示を制限します。制限する場合は、先にPermissionControllerを指定してください。データのURLは、知っていればワールドの外からでも読めます。見せてはいけない情報は、掲示板に出す範囲に入れないでください。",
                    MessageType.Info);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("permissionController"), new GUIContent("PermissionController"));
                serializedObject.ApplyModifiedProperties();
                serializedObject.Update();
                bool valid = assignedValid;
                if (GUILayout.Button("シーンから自動設定"))
                {
                    serializedObject.ApplyModifiedProperties();
                    if (candidateCount == 1) EditorApplication.delayCall += () => AssignController(source, onlyCandidate);
                    GUIUtility.ExitGUI();
                }
                DrawControllerCandidates(candidateCount);
                SerializedProperty audienceProperty = serializedObject.FindProperty("audience");
                string[] labels = { "全員", "管理者・協力者", "管理者のみ" };
                using (new EditorGUI.DisabledScope(!valid))
                    audienceProperty.enumValueIndex = EditorGUILayout.Popup(
                        "閲覧権限", audienceProperty.enumValueIndex, labels);
                bool restricted = source.audience != SheetBoardAudience.Public;
                if (restricted && !valid && GUILayout.Button("閲覧権限を全員に戻す"))
                {
                    audienceProperty.enumValueIndex = (int)SheetBoardAudience.Public;
                    serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }
            }
            showUpdate = EditorGUILayout.Foldout(showUpdate, "更新動作（任意）", true);
            if (showUpdate)
            {
                Draw("refreshInterval");
                Draw("nearRadius");
                Draw("staleMinutes");
                Draw("manualCooldown");
                Draw("startDelayMax");
            }
            showTexts = EditorGUILayout.Foldout(showTexts, "文言の上書き（空欄なら表示言語の既定文言）", true);
            if (showTexts)
            {
                EditorGUILayout.HelpBox("{code} = エラー、{s} = 秒、{time} = 更新時刻、{ago} = 経過時間", MessageType.Info);
                Draw("permissionDeniedText");
                Draw("permissionWaitingText");
                Draw("urlMissingText");
                Draw("loadingText");
                Draw("waitingNearText");
                Draw("failedText");
                Draw("freshText");
                Draw("staleText");
                Draw("retryText");
                Draw("refreshingSuffix");
                Draw("minutesUnit");
                Draw("hoursUnit");
                Draw("daysUnit");
            }
            serializedObject.ApplyModifiedProperties();
        }
        private void Draw(string name)
        {
            SerializedProperty property = serializedObject.FindProperty(name);
            if (property != null) EditorGUILayout.PropertyField(property, Label(name), true);
        }
        private static void AssignController(SheetBoardSource source, UdonBehaviour candidate)
        {
            if (!SheetBoardBuildCheck.IsValidController(source, candidate)) return;
            SheetBoardInstaller.RecordProxyUndo(source, "Assign PermissionController"); source.permissionController = candidate; UdonSharpEditorUtility.CopyProxyToUdon(source); EditorUtility.SetDirty(source);
            if (PrefabUtility.IsPartOfPrefabInstance(source)) PrefabUtility.RecordPrefabInstancePropertyModifications(source);
        }
        private void GetControllerCandidateSummary(SheetBoardSource source, out bool assignedValid, out int count, out UdonBehaviour only)
        {
            assignedValid = false;
            count = 0;
            only = null;
            foreach (UdonBehaviour candidate in controllerCandidates)
            {
                bool valid = SheetBoardBuildCheck.IsValidController(source, candidate);
                if (candidate == source.permissionController) assignedValid = valid;
                if (!valid) continue;
                count++;
                only = candidate;
            }
        }
        private static void DrawControllerCandidates(int count)
        {
            if (count > 1) EditorGUILayout.HelpBox("対応する候補が複数あります。自動選択しません。Inspector の参照欄から選択してください。", MessageType.Warning);
            else if (count == 0) EditorGUILayout.HelpBox("このシーンに対応する PermissionController がありません。", MessageType.Info);
        }
        private static GUIContent Label(string name)
        {
            switch (name)
            {
                case "baseUrl": return new GUIContent("ベース URL");
                case "refreshInterval": return new GUIContent("更新間隔（秒）");
                case "nearRadius": return new GUIContent("近接半径（m）");
                case "staleMinutes": return new GUIContent("古いデータとする時間（分）");
                case "manualCooldown": return new GUIContent("手動更新の待機時間（秒）");
                case "startDelayMax": return new GUIContent("初回取得の最大遅延（秒）");
                case "refreshingSuffix": return new GUIContent("更新中の表示（末尾に追加）");
                case "permissionDeniedText": return new GUIContent("閲覧権限なし");
                case "permissionWaitingText": return new GUIContent("権限確認中");
                case "urlMissingText": return new GUIContent("URL 未設定");
                case "loadingText": return new GUIContent("読み込み中");
                case "waitingNearText": return new GUIContent("近接待ち");
                case "failedText": return new GUIContent("読み込み失敗");
                case "freshText": return new GUIContent("最新データ");
                case "staleText": return new GUIContent("古いデータ");
                case "retryText": return new GUIContent("再試行中");
                case "minutesUnit": return new GUIContent("分の単位");
                case "hoursUnit": return new GUIContent("時間の単位");
                case "daysUnit": return new GUIContent("日の単位");
                default: return new GUIContent(name);
            }
        }
        private static string AudienceLabel(SheetBoardAudience audience)
        {
            switch (audience)
            {
                case SheetBoardAudience.Staff: return "管理者・協力者";
                case SheetBoardAudience.Admin: return "管理者のみ";
                default: return "全員";
            }
        }
        private static string NormalizedUrl(string url) { return (url ?? "").Trim(); }
        private static int UrlCount(float interval) { return Mathf.Clamp(Mathf.CeilToInt(300f / Mathf.Max(30f, interval)) + 1, 4, 64); }
        private static string UrlAt(string baseUrl, int index) { return baseUrl + (baseUrl.Contains("?") ? "&v=" : "?v=") + index; }
        private static string GetUserAgentVersion()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SheetBoardSource).Assembly);
            return package != null && !string.IsNullOrEmpty(package.version) ? package.version : FallbackUserAgentVersion;
        }
        internal static bool UrlsMatch(SheetBoardSource source)
        {
            string baseUrl = NormalizedUrl(source.baseUrl);
            if (baseUrl.Length == 0) return source.urls == null || source.urls.Length == 0;
            int count = UrlCount(source.refreshInterval);
            if (source.urls == null || source.urls.Length != count) return false;
            for (int i = 0; i < count; i++) if (source.urls[i] == null || source.urls[i].Get() != UrlAt(baseUrl, i)) return false;
            return true;
        }
        private static void ApplyUrls(SheetBoardSource source)
        {
            if (!SheetBoardInstaller.CheckProgramsCompiled()) return;
            SheetBoardInstaller.RecordProxyUndo(source, "Apply Sheet Board URLs");
            string baseUrl = NormalizedUrl(source.baseUrl);
            source.baseUrl = baseUrl;
            int count = baseUrl.Length == 0 ? 0 : UrlCount(source.refreshInterval);
            VRCUrl[] urls = new VRCUrl[count];
            for (int i = 0; i < count; i++) urls[i] = new VRCUrl(UrlAt(baseUrl, i));
            source.urls = urls;
            UdonSharpEditorUtility.CopyProxyToUdon(source);
            EditorUtility.SetDirty(source);
            if (PrefabUtility.IsPartOfPrefabInstance(source)) PrefabUtility.RecordPrefabInstancePropertyModifications(source);
        }
        private static void StartCheck(SheetBoardSource source)
        {
            Result result = new Result { url = NormalizedUrl(source.baseUrl) };
            Results[source.GetInstanceID()] = result;
            if (!Uri.TryCreate(result.url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            { result.message = "有効な HTTP(S) URL を入力してください。"; result.type = MessageType.Error; return; }
            result.pending = true;
            new Check(result, uri).Start();
        }
        private sealed class Check
        {
            private readonly Result result;
            private readonly Uri uri;
            private UnityWebRequest request;
            private bool config = true;
            private string[] hosts = DefaultHosts;
            private bool fallback;
            internal Check(Result result, Uri uri) { this.result = result; this.uri = uri; }
            internal void Start()
            {
                try { Send("https://api.vrchat.cloud/api/1/config"); EditorApplication.update += Poll; }
                catch (Exception e) { Finish("接続できません: " + e.Message, MessageType.Error); }
            }
            private void Send(string url) { request = UnityWebRequest.Get(url); request.redirectLimit = 0; request.timeout = 15;
                // The VRChat API rejects requests without a User-Agent of its own.
                request.SetRequestHeader("User-Agent", "SheetBoard-Editor/" + GetUserAgentVersion() + " (https://github.com/TenteEEEE)"); request.SendWebRequest(); }
            private void Poll()
            {
                try { PollRequest(); }
                catch (Exception e) { Finish("接続できません: " + e.Message, MessageType.Error); }
            }
            private void PollRequest()
            {
                if (!request.isDone) return;
                if (config)
                {
                    if (request.result == UnityWebRequest.Result.Success && TryHosts(request.downloadHandler.text, out string[] fetched)) hosts = fetched;
                    else fallback = true;
                    request.Dispose(); config = false;
                    try { Send(uri.AbsoluteUri); } catch (Exception e) { Finish("接続できません: " + e.Message, MessageType.Error); }
                    return;
                }
                string notes = HostTrusted(uri.Host, hosts) ? "信頼ホストです。" : "信頼ホスト一覧にありません。";
                if (fallback) notes += "（内蔵一覧を使用）";
                bool warning = !HostTrusted(uri.Host, hosts);
                if (uri.Scheme == Uri.UriSchemeHttp) { notes += "\nHTTP は HTTPS へのリダイレクトで失敗する可能性があります。"; warning = true; }
                if (uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(uri.AbsolutePath, @"(?:^|/)[0-9a-fA-F]{40}(?:/|$)"))
                { notes += "\n固定版の URL です。更新されません"; warning = true; }
                long status = request.responseCode;
                if (status >= 300 && status < 400) { Finish(notes + "\nこの URL はリダイレクトするので VRChat では読めません\nLocation: " + request.GetResponseHeader("Location"), MessageType.Error); return; }
                if (status != 200 || request.result != UnityWebRequest.Result.Success) { Finish(notes + "\nHTTP " + status + ": " + request.error, MessageType.Error); return; }
                if (!TryRanges(request.downloadHandler.text, out string[] ranges))
                {
                    Finish(notes + "\n本文は sheetboard/1 のJSONではありません。", MessageType.Error);
                    return;
                }
                result.ranges = ranges;
                Finish(notes + "\nHTTP 200 / sheetboard/1\n範囲: " + (ranges.Length == 0 ? "（なし）" : string.Join(", ", ranges)), warning ? MessageType.Warning : MessageType.Info);
            }
            private void Finish(string message, MessageType type) { result.message = message; result.type = type; result.pending = false; EditorApplication.update -= Poll; if (request != null) { request.Dispose(); request = null; } }
        }
        private static bool HostTrusted(string host, string[] hosts)
        {
            host = host.ToLowerInvariant();
            foreach (string entry in hosts)
            {
                string pattern = entry.ToLowerInvariant();
                if (pattern.StartsWith("*.")) { string suffix = pattern.Substring(1); if (host.EndsWith(suffix, StringComparison.Ordinal) && host.Length > suffix.Length) return true; }
                else if (host == pattern) return true;
            }
            return false;
        }
        private static bool TryHosts(string json, out string[] hosts)
        {
            hosts = null;
            if (!VRCJson.TryDeserializeFromJson(json, out DataToken root) || root.TokenType != TokenType.DataDictionary ||
                !root.DataDictionary.TryGetValue("stringHostUrlList", out DataToken list) || list.TokenType != TokenType.DataList) return false;
            List<string> values = new List<string>();
            for (int i = 0; i < list.DataList.Count; i++) if (list.DataList[i].TokenType == TokenType.String) values.Add(list.DataList[i].String);
            if (values.Count == 0) return false;
            hosts = values.ToArray(); return true;
        }
        private static bool TryRanges(string json, out string[] ranges)
        {
            ranges = Array.Empty<string>();
            if (!VRCJson.TryDeserializeFromJson(json, out DataToken root) || root.TokenType != TokenType.DataDictionary ||
                !root.DataDictionary.TryGetValue("format", out DataToken format) || format.TokenType != TokenType.String || format.String != "sheetboard/1" ||
                !root.DataDictionary.TryGetValue("ranges", out DataToken data) || data.TokenType != TokenType.DataDictionary) return false;
            DataList keys = data.DataDictionary.GetKeys();
            List<string> names = new List<string>();
            for (int i = 0; i < keys.Count; i++) if (keys[i].TokenType == TokenType.String) names.Add(keys[i].String);
            ranges = names.ToArray(); return true;
        }
    }
}
#endif
