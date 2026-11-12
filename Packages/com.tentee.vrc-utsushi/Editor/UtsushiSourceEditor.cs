#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Utsushi;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;

namespace UtsushiEditor
{
    [CustomEditor(typeof(UtsushiSource))]
    [CanEditMultipleObjects]
    [InitializeOnLoad]
    public class UtsushiSourceEditor : Editor
    {
        static UtsushiSourceEditor()
        {
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.ExitingEditMode) ApplySceneUrls(); };
            Undo.undoRedoPerformed += ApplySceneUrls;
        }
        private static void ApplySceneUrls()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) return;
            foreach (UtsushiSource source in UnityEngine.Object.FindObjectsByType<UtsushiSource>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source.gameObject.scene.IsValid() && source.gameObject.scene.isLoaded) EnsureUrlsApplied(source);
        }
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
        private static readonly HashSet<int> PendingUrlApply = new HashSet<int>();
        private static readonly HashSet<int> PendingChecks = new HashSet<int>();
        private static readonly string[] DefaultHosts = { "*.github.io", "pastebin.com", "gist.githubusercontent.com" };
        private static string[] cachedHosts;
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
        internal static void DrawUrlSection(UtsushiSource source)
        {
            if (source == null) return;
            SerializedObject so = new SerializedObject(source);
            so.Update();
            SerializedProperty url = so.FindProperty("baseUrl");
            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                url.stringValue = EditorGUILayout.DelayedTextField(new GUIContent("掲示板URL", "シートのメニュー「Utsushi → 状態とURLを表示」でコピーできる URL です"), url.stringValue);
            bool changed = EditorGUI.EndChangeCheck();
            so.ApplyModifiedProperties();
            if (EditorApplication.isPlaying) EditorGUILayout.HelpBox("再生中は変更できません", MessageType.Info);
            string currentUrl = NormalizedUrl(source.baseUrl);
            int id = source.GetInstanceID();
            if (changed && !EditorApplication.isPlayingOrWillChangePlaymode && PendingUrlApply.Add(id))
                EditorApplication.delayCall += () => { PendingUrlApply.Remove(id); if (source != null && !EditorApplication.isPlayingOrWillChangePlaymode) EnsureUrlsApplied(source); };
            if (!UrlsMatch(source) && !UtsushiInstaller.ProgramsCompiledQuiet())
                EditorGUILayout.HelpBox(UtsushiInstaller.ProgramFailureMessage(), MessageType.Warning);
            using (new EditorGUI.DisabledScope(Results.TryGetValue(id, out Result pending) && pending.pending))
                if (GUILayout.Button("もう一度確認") && PendingChecks.Add(id)) EditorApplication.delayCall += () =>
                {
                    PendingChecks.Remove(id);
                    if (source != null) StartCheck(source);
                };
            if (Results.TryGetValue(id, out Result result) && result.url == currentUrl)
            {
                EditorGUILayout.HelpBox(result.message, result.type);
            }
            else if (currentUrl.Length > 0 && HasStoredRanges(RangeSessionKey(currentUrl)))
            {
                // After a script reload only the stored range list survives; show the same success line from it.
                string stored = SessionState.GetString(RangeSessionKey(currentUrl), "");
                EditorGUILayout.HelpBox(stored.Length == 0 ? "✔ 読み込めます。まだ範囲がありません。シート側で範囲を追加してください。" : "✔ 読み込めます。範囲: " + stored.Replace("\n", ", "), MessageType.Info);
            }
            bool hasCurrentResult = (Results.TryGetValue(id, out Result previous) && previous.url == currentUrl) || HasStoredRanges(RangeSessionKey(currentUrl));
            if (currentUrl.Length > 0 && !hasCurrentResult)
            {
                if (PendingChecks.Add(id)) EditorApplication.delayCall += () =>
                {
                    PendingChecks.Remove(id);
                    if (source != null && NormalizedUrl(source.baseUrl) == currentUrl &&
                        (!Results.TryGetValue(id, out Result existing) || existing.url != currentUrl)) StartCheck(source);
                };
            }
        }
        internal static string[] GetRanges(UtsushiSource source)
        {
            if (source != null && Results.TryGetValue(source.GetInstanceID(), out Result result) && result.url == NormalizedUrl(source.baseUrl))
                return result.type == MessageType.Error ? Array.Empty<string>() : result.ranges;
            string key = RangeSessionKey(source == null ? "" : NormalizedUrl(source.baseUrl));
            if (HasStoredRanges(key))
            {
                string stored = SessionState.GetString(key, "");
                return stored.Length == 0 ? Array.Empty<string>() : stored.Split('\n');
            }
            return Array.Empty<string>();
        }
        private static string RangeSessionKey(string url) { return "Utsushi.Ranges." + url; }
        private const string NoStoredRanges = "\u0001none";
        // SessionState has no key lookup, so a marker default tells a missing entry from an empty range list.
        private static bool HasStoredRanges(string key) { return SessionState.GetString(key, NoStoredRanges) != NoStoredRanges; }
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target)) return;
            serializedObject.Update();
            UtsushiSource source = (UtsushiSource)target;
            EditorGUILayout.LabelField("基本設定", EditorStyles.boldLabel);
            if (targets.Length == 1) DrawUrlSection(source);
            else EditorGUILayout.HelpBox("複数選択中は表示しません", MessageType.Info);
            serializedObject.Update();
            bool isRestricted = source.audience != UtsushiAudience.Public;
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
                    "このデータ元の、ワールド内での読み込みと表示を制限します。制限する場合は、先に PermissionController を指定してください。データのURLは、知っていればワールドの外からでも読めます。見せてはいけない情報は、掲示板に出す範囲に入れないでください。",
                    MessageType.Info);
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty("permissionController"), new GUIContent("PermissionController"));
                serializedObject.ApplyModifiedProperties();
                serializedObject.Update();
                bool valid = assignedValid;
                using (new EditorGUI.DisabledScope(candidateCount != 1))
                if (GUILayout.Button("シーンから自動設定"))
                {
                    serializedObject.ApplyModifiedProperties();
                    if (candidateCount == 1)
                    {
                        UnityEngine.Object[] selected = targets;
                        EditorApplication.delayCall += () => { foreach (UnityEngine.Object item in selected) if (item != null) AssignController((UtsushiSource)item, onlyCandidate); };
                    }
                    GUIUtility.ExitGUI();
                }
                DrawControllerCandidates(candidateCount);
                SerializedProperty audienceProperty = serializedObject.FindProperty("audience");
                string[] labels = { "全員", "ワールドスタッフ以上", "ワールド管理者のみ" };
                using (new EditorGUI.DisabledScope(!valid))
                {
                    EditorGUI.showMixedValue = audienceProperty.hasMultipleDifferentValues;
                    EditorGUI.BeginChangeCheck();
                    int audience = EditorGUILayout.Popup("閲覧権限", audienceProperty.enumValueIndex, labels);
                    if (EditorGUI.EndChangeCheck()) audienceProperty.enumValueIndex = audience;
                    EditorGUI.showMixedValue = false;
                }
                bool restricted = source.audience != UtsushiAudience.Public;
                if (restricted && !valid && GUILayout.Button("閲覧権限を全員に戻す"))
                {
                    audienceProperty.enumValueIndex = (int)UtsushiAudience.Public;
                    serializedObject.ApplyModifiedProperties();
                    GUIUtility.ExitGUI();
                }
            }
            showUpdate = EditorGUILayout.Foldout(showUpdate, "読み込みの調整（任意）", true);
            if (showUpdate)
            {
                EditorGUI.BeginChangeCheck();
                Draw("refreshInterval");
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    foreach (UnityEngine.Object item in targets) EnsureUrlsApplied((UtsushiSource)item);
                    serializedObject.Update();
                }
                Draw("nearRadius");
            }
            showTexts = EditorGUILayout.Foldout(showTexts, "表示言語と文言（任意）", true);
            if (showTexts)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("language"), new GUIContent("表示言語（自動 = 見る人の VRChat の言語）"));
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
        private static void AssignController(UtsushiSource source, UdonBehaviour candidate)
        {
            if (!UtsushiBuildCheck.IsValidController(source, candidate)) return;
            UtsushiInstaller.RecordProxyUndo(source, "Assign PermissionController"); source.permissionController = candidate; UdonSharpEditorUtility.CopyProxyToUdon(source); EditorUtility.SetDirty(source);
            if (PrefabUtility.IsPartOfPrefabInstance(source)) PrefabUtility.RecordPrefabInstancePropertyModifications(source);
        }
        private void GetControllerCandidateSummary(UtsushiSource source, out bool assignedValid, out int count, out UdonBehaviour only)
        {
            assignedValid = false;
            count = 0;
            only = null;
            foreach (UdonBehaviour candidate in controllerCandidates)
            {
                bool valid = UtsushiBuildCheck.IsValidController(source, candidate);
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
                case "refreshInterval": return new GUIContent("読み込み間隔（秒）");
                case "nearRadius": return new GUIContent("近づいたときだけ読み込む距離（m、0 = 常に読み込む）");
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
        private static string AudienceLabel(UtsushiAudience audience)
        {
            switch (audience)
            {
                case UtsushiAudience.Staff: return "ワールドスタッフ以上";
                case UtsushiAudience.Admin: return "ワールド管理者のみ";
                default: return "全員";
            }
        }
        private static string NormalizedUrl(string url) { return (url ?? "").Trim(); }
        private static int UrlCount(float interval) { return Mathf.Clamp(Mathf.CeilToInt(300f / Mathf.Max(30f, interval)) + 1, 4, 64); }
        private static string UrlAt(string baseUrl, int index) { return baseUrl + (baseUrl.Contains("?") ? "&v=" : "?v=") + index; }
        private static string GetUserAgentVersion()
        {
            UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UtsushiSource).Assembly);
            return package != null && !string.IsNullOrEmpty(package.version) ? package.version : FallbackUserAgentVersion;
        }
        internal static bool UrlsMatch(UtsushiSource source)
        {
            string baseUrl = NormalizedUrl(source.baseUrl);
            if (baseUrl.Length == 0) return source.urls == null || source.urls.Length == 0;
            int count = UrlCount(source.refreshInterval);
            if (source.urls == null || source.urls.Length != count) return false;
            for (int i = 0; i < count; i++) if (source.urls[i] == null || source.urls[i].Get() != UrlAt(baseUrl, i)) return false;
            return true;
        }
        internal static bool EnsureUrlsApplied(UtsushiSource source)
        {
            if (source == null) return false;
            if (UrlsMatch(source)) return true;
            if (!UtsushiInstaller.ProgramsCompiledQuiet()) return false;
            ApplyUrls(source);
            return true;
        }
        private static void ApplyUrls(UtsushiSource source)
        {
            UtsushiInstaller.RecordProxyUndo(source, "Apply Utsushi URLs");
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
        private static void StartCheck(UtsushiSource source)
        {
            Result result = new Result { url = NormalizedUrl(source.baseUrl) };
            Results[source.GetInstanceID()] = result;
            if (!Uri.TryCreate(result.url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            { SessionState.EraseString(RangeSessionKey(result.url)); result.message = "https:// で始まる URL を入力してください。"; result.type = MessageType.Error; return; }
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
            internal Check(Result result, Uri uri) { this.result = result; this.uri = uri; }
            internal void Start()
            {
                try { if (cachedHosts != null) { hosts = cachedHosts; config = false; Send(uri.AbsoluteUri); } else Send("https://api.vrchat.cloud/api/1/config"); EditorApplication.update += Poll; }
                catch (Exception e) { Finish("接続できませんでした。（詳細: " + e.Message + "）", MessageType.Error); }
            }
            private void Send(string url) { request = UnityWebRequest.Get(url); request.redirectLimit = 0; request.timeout = 15;
                // The VRChat API rejects requests without a User-Agent of its own.
                request.SetRequestHeader("User-Agent", "Utsushi-Editor/" + GetUserAgentVersion() + " (https://github.com/TenteEEEE)"); request.SendWebRequest(); }
            private void Poll()
            {
                try { PollRequest(); }
                catch (Exception e) { Finish("接続できませんでした。（詳細: " + e.Message + "）", MessageType.Error); }
            }
            private void PollRequest()
            {
                if (!request.isDone) return;
                if (config)
                {
                    if (request.result == UnityWebRequest.Result.Success && TryHosts(request.downloadHandler.text, out string[] fetched)) hosts = cachedHosts = fetched;
                    else hosts = DefaultHosts;
                    request.Dispose(); config = false;
                    try { Send(uri.AbsoluteUri); } catch (Exception e) { Finish("接続できませんでした。（詳細: " + e.Message + "）", MessageType.Error); }
                    return;
                }
                bool trusted = HostTrusted(uri.Host, hosts);
                string hostWarning = trusted ? "" : "このURLのサイトは、VRChat が読み込みを許可していません。シートのメニュー「状態とURLを表示」に出る URL を、そのまま貼ってください。\n";
                string prefix = "";
                bool warning = !trusted;
                if (uri.Scheme == Uri.UriSchemeHttp) { prefix = "http:// の URL は VRChat で読めないことがあります。https:// の URL を使ってください。\n"; warning = true; }
                if (uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(uri.AbsolutePath, @"(?:^|/)[0-9a-fA-F]{40}(?:/|$)"))
                { prefix = "これは特定の時点の内容を指す URL です。シートを編集しても掲示板は変わりません。シートのメニュー「状態とURLを表示」に出る URL を貼ってください。\n"; warning = true; }
                long status = request.responseCode;
                if (status >= 300 && status < 400) { Finish("この URL は別の場所へ転送されるため、VRChat では読めません。シートのメニュー「状態とURLを表示」に出る URL を貼ってください。（詳細: HTTP " + status + " → " + request.GetResponseHeader("Location") + "）", MessageType.Error); return; }
                if (request.result != UnityWebRequest.Result.Success && status == 0) { Finish("接続できませんでした。ネットワークを確認してください。（詳細: " + request.error + "）", MessageType.Error); return; }
                if (status != 200 || request.result != UnityWebRequest.Result.Success) { Finish("読み込めませんでした。URL が正しいか確認してください。（詳細: HTTP " + status + " " + request.error + "）", MessageType.Error); return; }
                if (!TryRanges(request.downloadHandler.text, out string[] ranges))
                {
                    Finish("Utsushi のデータではありません。掲示板URLを確認してください。", MessageType.Error);
                    return;
                }
                result.ranges = ranges;
                SessionState.SetString(RangeSessionKey(result.url), string.Join("\n", ranges));
                string success = ranges.Length == 0 ? "✔ 読み込めます。まだ範囲がありません。シート側で範囲を追加してください。" : "✔ 読み込めます。範囲: " + string.Join(", ", ranges);
                Finish(prefix + hostWarning + success, warning ? MessageType.Warning : MessageType.Info);
            }
            private void Finish(string message, MessageType type) { if (type == MessageType.Error) SessionState.EraseString(RangeSessionKey(result.url)); result.message = message; result.type = type; result.pending = false; EditorApplication.update -= Poll; UnityEditorInternal.InternalEditorUtility.RepaintAllViews(); if (request != null) { request.Dispose(); request = null; } }
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
                !root.DataDictionary.TryGetValue("format", out DataToken format) || format.TokenType != TokenType.String || format.String != "sheetboard/1" || // Must match the value written by Apps Script.
                !root.DataDictionary.TryGetValue("ranges", out DataToken data) || data.TokenType != TokenType.DataDictionary) return false;
            DataList keys = data.DataDictionary.GetKeys();
            List<string> names = new List<string>();
            for (int i = 0; i < keys.Count; i++) if (keys[i].TokenType == TokenType.String) names.Add(keys[i].String);
            ranges = names.ToArray(); return true;
        }
    }
}
#endif
