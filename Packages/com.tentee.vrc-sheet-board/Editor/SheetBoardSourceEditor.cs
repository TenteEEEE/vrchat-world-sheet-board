#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SheetBoard;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using VRC.SDK3.Data;
using VRC.SDKBase;

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
        private const string UserAgentVersion = "0.1.0";
        private static readonly Dictionary<int, Result> Results = new Dictionary<int, Result>();
        private static readonly string[] DefaultHosts = { "*.github.io", "pastebin.com", "gist.githubusercontent.com" };
        internal static string[] GetRanges(SheetBoardSource source)
        {
            if (source != null && Results.TryGetValue(source.GetInstanceID(), out Result result) && result.url == NormalizedUrl(source.baseUrl)) return result.ranges;
            return Array.Empty<string>();
        }
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();
            SerializedProperty iterator = serializedObject.GetIterator();
            bool children = true;
            while (iterator.NextVisible(children))
            {
                children = false;
                if (iterator.name == "urls") continue;
                using (new EditorGUI.DisabledScope(iterator.name == "m_Script"))
                    EditorGUILayout.PropertyField(iterator, Label(iterator.name), true);
            }
            serializedObject.ApplyModifiedProperties();
            SheetBoardSource source = (SheetBoardSource)target;
            if (!UrlsMatch(source)) EditorGUILayout.HelpBox("URL が未適用か、生成済み URL と一致しません。", MessageType.Warning);
            if (GUILayout.Button("URL を適用")) ApplyUrls(source);
            using (new EditorGUI.DisabledScope(Results.TryGetValue(source.GetInstanceID(), out Result current) && current.pending))
                if (GUILayout.Button("接続を確認")) StartCheck(source);
            if (Results.TryGetValue(source.GetInstanceID(), out Result result))
            {
                if (result.url != NormalizedUrl(source.baseUrl)) EditorGUILayout.HelpBox("URL が変更されました。接続を再確認してください。", MessageType.Warning);
                else EditorGUILayout.HelpBox(result.message, result.type);
                if (result.pending) Repaint();
            }
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
                case "urlMissingText": return new GUIContent("URL 未設定の文言");
                case "loadingText": return new GUIContent("読み込み中の文言");
                case "waitingNearText": return new GUIContent("近接待ちの文言");
                case "failedText": return new GUIContent("取得失敗の文言");
                case "freshText": return new GUIContent("更新済みの文言");
                case "staleText": return new GUIContent("古いデータの文言");
                case "retryText": return new GUIContent("再試行中の文言");
                case "minutesUnit": return new GUIContent("単位（分前）");
                case "hoursUnit": return new GUIContent("単位（時間前）");
                case "daysUnit": return new GUIContent("単位（日前）");
                default: return new GUIContent(name);
            }
        }
        private static string NormalizedUrl(string url) { return (url ?? "").Trim(); }
        private static int UrlCount(float interval) { return Mathf.Clamp(Mathf.CeilToInt(300f / Mathf.Max(30f, interval)) + 1, 4, 64); }
        private static string UrlAt(string baseUrl, int index) { return baseUrl + (baseUrl.Contains("?") ? "&v=" : "?v=") + index; }
        private static bool UrlsMatch(SheetBoardSource source)
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
            Undo.RecordObject(source, "Apply Sheet Board URLs");
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
                request.SetRequestHeader("User-Agent", "SheetBoard-Editor/" + UserAgentVersion + " (https://github.com/TenteEEEE)"); request.SendWebRequest(); }
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
                if (!TryRanges(request.downloadHandler.text, out string[] ranges)) { Finish(notes + "\n本文は sheetboard/1 の JSON ではありません。", MessageType.Error); return; }
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
