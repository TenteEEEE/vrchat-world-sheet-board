using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace Utsushi
{
    public enum UtsushiAudience { Public = 0, Staff = 1, Admin = 2 }
    public enum UtsushiLanguage { Auto = 0, Japanese = 1, English = 2 }

    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class UtsushiSource : UdonSharpBehaviour
    {
        private const float TickInterval = 1f;
        private const float MinimumRefresh = 30f;
        private const float MaximumBackoff = 300f;
        private const float Timeout = 60f;
        private const float RetireSeconds = 2f * Timeout;
        // Paired with the Apps Script 60-minute heartbeat.
        private const float StaleMinutes = 90f;
        private const float ManualCooldown = 5f;
        private const float StartDelayMax = 3f;
        private const double MaximumUnix = 253402300799d;
        private const double SecondsPerMinute = 60d;
        private const double MinutesPerHour = 60d;
        private const double MinutesPerDay = 1440d;
        private const string JapanesePermissionDeniedText = "閲覧権限がありません";
        private const string EnglishPermissionDeniedText = "You don't have permission to view this";
        private const string JapanesePermissionWaitingText = "権限を確認中…";
        private const string EnglishPermissionWaitingText = "Checking permission…";
        private const string JapaneseUrlMissingText = "URLが設定されていません";
        private const string EnglishUrlMissingText = "No URL is set";
        private const string JapaneseLoadingText = "読み込み中…";
        private const string EnglishLoadingText = "Loading…";
        private const string JapaneseWaitingNearText = "近づくと読み込みます";
        private const string EnglishWaitingNearText = "Come closer to load";
        private const string JapaneseFailedText = "読み込めません・{s}秒後に再試行（{code}）";
        private const string EnglishFailedText = "Couldn't load · retrying in {s}s ({code})";
        private const string JapaneseFreshText = "更新 {time}（{ago}）";
        private const string EnglishFreshText = "Updated {time} ({ago})";
        private const string JapaneseStaleText = "更新 {time}（{ago}）・更新が止まっている可能性があります";
        private const string EnglishStaleText = "Updated {time} ({ago}) · may be out of date";
        private const string JapaneseRetryText = "更新 {time}（{ago}）・再試行中";
        private const string EnglishRetryText = "Updated {time} ({ago}) · retrying";
        private const string JapaneseRefreshingSuffix = "・更新中…";
        private const string EnglishRefreshingSuffix = " · updating…";
        private const string JapaneseMinutesUnit = "分前";
        private const string EnglishMinutesUnit = " min ago";
        private const string JapaneseHoursUnit = "時間前";
        private const string EnglishHoursUnit = " h ago";
        private const string JapaneseDaysUnit = "日前";
        private const string EnglishDaysUnit = " d ago";

        public VRCUrl[] urls;
        public string baseUrl = "";
        public float refreshInterval = 60f;
        public float nearRadius = 0f;
        public UtsushiAudience audience = UtsushiAudience.Public;
        public UtsushiLanguage language = UtsushiLanguage.Auto;
        public UdonBehaviour permissionController;
        public string permissionDeniedText = "";
        public string permissionWaitingText = "";

        public string urlMissingText = "";
        public string loadingText = "";
        public string waitingNearText = "";
        public string failedText = "";
        public string freshText = "";
        public string staleText = "";
        public string retryText = "";
        public string refreshingSuffix = "";
        public string minutesUnit = "";
        public string hoursUnit = "";
        public string daysUnit = "";

        private DataDictionary ranges;
        private DataList rangeOrder;
        private string currentRev;
        private double updatedUnix;
        private double aliveUnix;
        private int revision;
        private bool hasSuccess;
        private bool failed;
        private bool inFlight;
        private bool manualPending;
        private bool hasManual;
        private bool hasNear;
        private bool initialized;
        private float lastNearAt;
        private float nearWindow = 2f;
        private float lastManualAt;
        private float startedAt;
        private float nextFetchAt;
        private float nextTickAt;
        private int validUrlCount;
        private int failCount;
        private int urlIndex;
        private string activeUrl = "";
        private string errorCode = "";
        private string statusText = "";
        private int statusLevel = 2;
        private bool permissionKnown;
        private bool permissionAllowed;
        private string[] retiredUrls;
        private float[] retiredAt;
        private int retiredUrlCount;
        private UdonBehaviour cachedPermissionSource;
        private UtsushiLanguage resolvedLanguage = UtsushiLanguage.English;
        private bool languageResolved;

        private void OnEnable()
        {
            ResolveLanguage();
            float now = Time.realtimeSinceStartup;
            EvaluatePermission();
            if (!initialized)
            {
                initialized = true;
                nextFetchAt = now + UnityEngine.Random.Range(0f, Mathf.Max(0f, StartDelayMax));
                if (urls != null && urls.Length > 0)
                {
                    urlIndex = UnityEngine.Random.Range(0, urls.Length);
                    for (int i = 0; i < urls.Length; i++)
                    {
                        if (urls[i] != null && !string.IsNullOrEmpty(urls[i].Get())) validUrlCount++;
                    }
                    retiredUrls = new string[urls.Length];
                    retiredAt = new float[urls.Length];
                }
            }
            if (nextTickAt == 0f || now > nextTickAt + 2f * TickInterval)
            {
                nextTickAt = now + TickInterval;
                SendCustomEventDelayedSeconds(nameof(_Tick), TickInterval);
            }
        }

        public override void OnLanguageChanged(string language)
        {
            ResolveLanguage();
            UpdateStatus();
        }

        private void ResolveLanguage()
        {
            if (language == UtsushiLanguage.Japanese) { resolvedLanguage = UtsushiLanguage.Japanese; languageResolved = true; return; }
            if (language == UtsushiLanguage.English) { resolvedLanguage = UtsushiLanguage.English; languageResolved = true; return; }
            string current = VRCPlayerApi.GetCurrentLanguage();
            resolvedLanguage = !string.IsNullOrEmpty(current) && current.Length >= 2 && (current[0] == 'j' || current[0] == 'J') && (current[1] == 'a' || current[1] == 'A') ? UtsushiLanguage.Japanese : UtsushiLanguage.English;
            languageResolved = true;
        }

        private string ResolveText(string value, string japanese, string english)
        {
            if (!languageResolved) ResolveLanguage();
            if (string.IsNullOrEmpty(value)) return resolvedLanguage == UtsushiLanguage.Japanese ? japanese : english;
            if (value == japanese || value == "更新 {time}（{ago}）・自動公開が止まっています" || value == "Updated {time} ({ago}) · auto-publish has stopped" || value == "You don't have permission to view this." || value == "No URL is set." || value == "Come closer to load.") return resolvedLanguage == UtsushiLanguage.Japanese ? japanese : english;
            return value;
        }

        public UtsushiLanguage GetResolvedLanguage() { ResolveLanguage(); return resolvedLanguage; }

        private string GetErrorDescription()
        {
            bool japanese = GetResolvedLanguage() == UtsushiLanguage.Japanese;
            if (errorCode == "timeout") return japanese ? "時間切れ" : "timed out";
            if (errorCode == "JSON") return japanese ? "データの形式が不正" : "invalid data";
            bool digits = errorCode.Length > 0;
            for (int i = 0; i < errorCode.Length; i++) if (errorCode[i] < '0' || errorCode[i] > '9') digits = false;
            return digits ? "HTTP " + errorCode : errorCode;
        }

        public string GetPermissionDeniedText() { return ResolveText(permissionDeniedText, JapanesePermissionDeniedText, EnglishPermissionDeniedText); }
        public string GetPermissionWaitingText() { return ResolveText(permissionWaitingText, JapanesePermissionWaitingText, EnglishPermissionWaitingText); }
        public string GetRefreshingSuffix() { return ResolveText(refreshingSuffix, JapaneseRefreshingSuffix, EnglishRefreshingSuffix); }

        private void OnDisable()
        {
            if (audience == UtsushiAudience.Public) return;
            RevokeAccess();
            permissionKnown = false;
            permissionAllowed = false;
            cachedPermissionSource = null;
        }

        public void _Tick()
        {
            float now = Time.realtimeSinceStartup;
            // Delayed Udon events carry no arguments; the deadline suppresses old loops.
            if (now < nextTickAt - 0.5f * TickInterval) return;
            nextTickAt = now + TickInterval;
            SendCustomEventDelayedSeconds(nameof(_Tick), TickInterval);

            EvaluatePermission();

            if (inFlight && now - startedAt >= Timeout)
            {
                RetireUrl(activeUrl);
                inFlight = false;
                activeUrl = "";
                errorCode = "timeout";
                RecordFailure();
            }
            if (HasPermission() && !inFlight && now >= nextFetchAt && validUrlCount > 0 &&
                (manualPending || nearRadius <= 0f || (hasNear && now - lastNearAt <= nearWindow)))
            {
                bool started = false;
                for (int i = 0; i < urls.Length; i++)
                {
                    if (urlIndex >= urls.Length || urlIndex < 0) urlIndex = 0;
                    VRCUrl url = urls[urlIndex];
                    urlIndex = (urlIndex + 1) % urls.Length;
                    if (url == null || string.IsNullOrEmpty(url.Get()) || IsRetired(url.Get())) continue;
                    manualPending = false;
                    inFlight = true;
                    startedAt = now;
                    activeUrl = url.Get();
                    VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
                    started = true;
                    break;
                }
                if (!started)
                {
                    manualPending = false;
                    float earliestExpiry = float.MaxValue;
                    for (int i = 0; i < retiredUrlCount; i++)
                    {
                        if (string.IsNullOrEmpty(retiredUrls[i]) || !IsRetired(retiredUrls[i])) continue;
                        float expiry = retiredAt[i] + RetireSeconds;
                        if (expiry < earliestExpiry) earliestExpiry = expiry;
                    }
                    if (earliestExpiry < float.MaxValue) nextFetchAt = earliestExpiry;
                }
            }
            UpdateStatus();
        }

        public void RequestRefresh()
        {
            EvaluatePermission();
            if (!HasPermission() || validUrlCount == 0) return;
            float now = Time.realtimeSinceStartup;
            if (inFlight || (hasManual && now - lastManualAt < ManualCooldown)) return;
            hasManual = true;
            lastManualAt = now;
            manualPending = true;
            nextFetchAt = now;
            UpdateStatus();
        }

        public bool IsBusy() { return HasPermission() && validUrlCount > 0 && (inFlight || manualPending); }

        public float GetRefreshWaitSeconds()
        {
            if (!HasPermission() || validUrlCount == 0 || inFlight) return -1f;
            if (hasManual)
            {
                float remaining = ManualCooldown - (Time.realtimeSinceStartup - lastManualAt);
                if (remaining > 0f) return remaining;
            }
            return 0f;
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            if (result == null) return;
            if (result.Url == null) return;
            EvaluatePermission();
            if (IsRetired(result.Url.Get())) { UnretireUrl(result.Url.Get()); return; }
            if (!inFlight || result.Url.Get() != activeUrl || !HasPermission()) return;
            inFlight = false;
            activeUrl = "";
            ApplyJson(result.Result);
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            if (result == null) return;
            if (result.Url == null) return;
            EvaluatePermission();
            if (IsRetired(result.Url.Get())) { UnretireUrl(result.Url.Get()); return; }
            if (!inFlight || result.Url.Get() != activeUrl || !HasPermission()) return;
            inFlight = false;
            activeUrl = "";
            errorCode = result.ErrorCode.ToString();
            RecordFailure();
        }

        private void RecordFailure()
        {
            failed = true;
            failCount++;
            float delay = Mathf.Min(MinimumRefresh * Mathf.Pow(2f, Mathf.Min(failCount - 1, 4)), MaximumBackoff);
            nextFetchAt = Time.realtimeSinceStartup + delay * UnityEngine.Random.Range(0.8f, 1.2f);
        }

        private bool InvalidJson()
        {
            errorCode = "JSON";
            RecordFailure();
            UpdateStatus();
            return false;
        }

        public bool ApplyJson(string json)
        {
            EvaluatePermission();
            if (!HasPermission()) return false;
            DataToken root;
            DataToken format;
            DataToken rev;
            DataToken updated;
            DataToken rangeToken;
            DataToken alive;
            DataToken orderToken;
            if (string.IsNullOrEmpty(json) || !VRCJson.TryDeserializeFromJson(json, out root) || root.TokenType != TokenType.DataDictionary) return InvalidJson();
            DataDictionary data = root.DataDictionary;
            if (!data.TryGetValue("format", out format) || format.TokenType != TokenType.String || format.String != "sheetboard/1") // Must match the value written by Apps Script.
                return InvalidJson();
            if (!data.TryGetValue("rev", out rev) || rev.TokenType != TokenType.String) return InvalidJson();
            if (!data.TryGetValue("updatedUnix", out updated) || !IsNumber(updated)) return InvalidJson();
            if (!data.TryGetValue("ranges", out rangeToken) || rangeToken.TokenType != TokenType.DataDictionary) return InvalidJson();
            DataList newOrder = null;
            if (data.TryGetValue("order", out orderToken) && orderToken.TokenType == TokenType.DataList)
            {
                bool validOrder = true;
                DataList candidateOrder = orderToken.DataList;
                for (int i = 0; i < candidateOrder.Count; i++)
                {
                    DataToken name = candidateOrder[i];
                    DataToken range;
                    if (name.TokenType != TokenType.String || !rangeToken.DataDictionary.TryGetValue(name.String, out range))
                    {
                        validOrder = false;
                        break;
                    }
                }
                if (validOrder) newOrder = candidateOrder;
            }
            double newUpdated = Number(updated);
            if (!(newUpdated > 0d && newUpdated < MaximumUnix)) return InvalidJson();
            double newAlive = data.TryGetValue("aliveUnix", out alive) && IsNumber(alive) ? Number(alive) : 0d;
            if (!(newAlive > 0d && newAlive < MaximumUnix)) newAlive = 0d;
            aliveUnix = Math.Max(aliveUnix, newAlive);
            if (!hasSuccess || (newUpdated >= updatedUnix && rev.String != currentRev))
            {
                ranges = rangeToken.DataDictionary;
                rangeOrder = newOrder;
                currentRev = rev.String;
                updatedUnix = newUpdated;
                revision++;
            }
            hasSuccess = true;
            failed = false;
            failCount = 0;
            nextFetchAt = Time.realtimeSinceStartup + Mathf.Max(MinimumRefresh, refreshInterval) * UnityEngine.Random.Range(1f, 1.1f);
            UpdateStatus();
            return true;
        }

        private bool IsNumber(DataToken token)
        {
            return token.TokenType == TokenType.Int || token.TokenType == TokenType.Long || token.TokenType == TokenType.Double;
        }

        private double Number(DataToken token)
        {
            if (token.TokenType == TokenType.Int) return token.Int;
            if (token.TokenType == TokenType.Long) return token.Long;
            return token.Double;
        }

        public bool HasData() { return HasPermission() && hasSuccess; }
        public bool HasNoRanges() { return HasPermission() && ranges != null && ranges.Count == 0; }
        public int GetRevision() { return revision; }

        public DataDictionary GetRange(string name)
        {
            if (!HasPermission()) return null;
            DataToken value;
            if (ranges == null || name == null || !ranges.TryGetValue(name, out value) || value.TokenType != TokenType.DataDictionary) return null;
            return value.DataDictionary;
        }

        public string GetFirstRangeName()
        {
            if (!HasPermission()) return "";
            if (ranges == null) return "";
            if (rangeOrder != null && rangeOrder.Count > 0 && rangeOrder[0].TokenType == TokenType.String)
            {
                return rangeOrder[0].String;
            }
            DataList keys = ranges.GetKeys();
            if (keys.Count == 0 || keys[0].TokenType != TokenType.String) return "";
            return keys[0].String;
        }

        public void _ReportNear(float checkInterval)
        {
            hasNear = true;
            lastNearAt = Time.realtimeSinceStartup;
            nearWindow = Mathf.Max(2f, 2f * checkInterval);
        }

        private void UpdateStatus()
        {
            statusLevel = 2;
            if (audience != UtsushiAudience.Public && !permissionKnown) { statusLevel = 3; statusText = GetPermissionWaitingText(); return; }
            if (audience != UtsushiAudience.Public && !permissionAllowed) { statusText = GetPermissionDeniedText(); return; }
            if (validUrlCount == 0) { statusText = ResolveText(urlMissingText, JapaneseUrlMissingText, EnglishUrlMissingText); return; }
            if (inFlight && !hasSuccess) { statusLevel = 3; statusText = ResolveText(loadingText, JapaneseLoadingText, EnglishLoadingText); return; }
            if (!hasSuccess)
            {
                if (failed && !manualPending && Time.realtimeSinceStartup >= nextFetchAt && nearRadius > 0f && (!hasNear || Time.realtimeSinceStartup - lastNearAt > nearWindow))
                {
                    statusLevel = 3; statusText = ResolveText(waitingNearText, JapaneseWaitingNearText, EnglishWaitingNearText);
                }
                else if (failed)
                {
                    int seconds = Mathf.CeilToInt(Mathf.Max(0f, nextFetchAt - Time.realtimeSinceStartup));
                    statusText = ResolveText(failedText, JapaneseFailedText, EnglishFailedText).Replace("{code}", GetErrorDescription()).Replace("{s}", seconds.ToString());
                }
                else if (manualPending) { statusLevel = 3; statusText = ResolveText(loadingText, JapaneseLoadingText, EnglishLoadingText); }
                else if (nearRadius > 0f && (!hasNear || Time.realtimeSinceStartup - lastNearAt > nearWindow)) { statusLevel = 3; statusText = ResolveText(waitingNearText, JapaneseWaitingNearText, EnglishWaitingNearText); }
                else { statusLevel = 3; statusText = ResolveText(loadingText, JapaneseLoadingText, EnglishLoadingText); }
                return;
            }
            DateTime now = Utilities.IsValid(Networking.LocalPlayer) ? Networking.GetNetworkDateTime() : DateTime.UtcNow;
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double nowUnix = (now - epoch).TotalSeconds;
            double minutes = Math.Max(0d, nowUnix - updatedUnix) / SecondsPerMinute;
            string elapsed = ((int)minutes).ToString() + ResolveText(minutesUnit, JapaneseMinutesUnit, EnglishMinutesUnit);
            if (minutes < 1d) elapsed = GetResolvedLanguage() == UtsushiLanguage.Japanese ? "たった今" : "just now";
            if (minutes >= MinutesPerDay) elapsed = ((int)(minutes / MinutesPerDay)).ToString() + ResolveText(daysUnit, JapaneseDaysUnit, EnglishDaysUnit);
            else if (minutes >= MinutesPerHour) elapsed = ((int)(minutes / MinutesPerHour)).ToString() + ResolveText(hoursUnit, JapaneseHoursUnit, EnglishHoursUnit);
            bool stale = aliveUnix > 0d && (nowUnix - aliveUnix) / SecondsPerMinute > StaleMinutes;
            statusLevel = failed || stale ? 1 : 0;
            string template = failed ? ResolveText(retryText, JapaneseRetryText, EnglishRetryText) : (stale ? ResolveText(staleText, JapaneseStaleText, EnglishStaleText) : ResolveText(freshText, JapaneseFreshText, EnglishFreshText));
            DateTime localTime = epoch.AddSeconds(updatedUnix).ToLocalTime();
            string time = localTime.Hour.ToString("00") + ":" + localTime.Minute.ToString("00");
            statusText = template.Replace("{time}", time).Replace("{ago}", elapsed);
            if (IsBusy()) statusText += ResolveText(refreshingSuffix, JapaneseRefreshingSuffix, EnglishRefreshingSuffix);
        }

        public string GetStatusText() { return statusText; }
        // 0 normal, 1 warning, 2 error, 3 preparing.
        public int GetStatusLevel() { return statusLevel; }
        public int GetAccessStatus() { return HasPermission() ? 0 : (permissionKnown ? 2 : 1); }

        private bool HasPermission()
        {
            return audience == UtsushiAudience.Public || (permissionKnown && permissionAllowed);
        }

        public bool _PermissionValueAllowed(int permissionValue)
        {
            if (audience == UtsushiAudience.Public) return true;
            if (permissionValue == 0) return true;
            return audience == UtsushiAudience.Staff && permissionValue == 1;
        }

        private void EvaluatePermission()
        {
            if (audience == UtsushiAudience.Public) { permissionKnown = true; permissionAllowed = true; return; }
            bool known = false;
            bool allowed = false;
            VRCPlayerApi player = Networking.LocalPlayer;
            if (permissionController != null && permissionController.enabled && permissionController.gameObject.activeInHierarchy && Utilities.IsValid(player))
            {
                UdonBehaviour found = cachedPermissionSource;
                bool searchPermissionSource = found == null;
                if (!searchPermissionSource && (!found.enabled || !found.gameObject.activeInHierarchy)) searchPermissionSource = true;
                if (!searchPermissionSource)
                {
                    UdonBehaviour linkedController = (UdonBehaviour)found.GetProgramVariable("Controller");
                    if (linkedController != permissionController) searchPermissionSource = true;
                }
                if (searchPermissionSource)
                {
                    found = null;
                    GameObject[] objects = Networking.GetPlayerObjects(player);
                    if (objects != null)
                    {
                        for (int i = 0; i < objects.Length; i++)
                        {
                            if (!Utilities.IsValid(objects[i])) continue;
                            UdonBehaviour[] behaviours = objects[i].GetComponents<UdonBehaviour>();
                            for (int j = 0; j < behaviours.Length; j++)
                            {
                                UdonBehaviour behaviour = behaviours[j];
                                if (behaviour == null || !behaviour.enabled || !behaviour.gameObject.activeInHierarchy) continue;
                                if (behaviour.GetProgramVariableType("Controller") == null || behaviour.GetProgramVariableType("CurrentPermission") == null || behaviour.GetProgramVariableType("IsInitialized") == null) continue;
                                UdonBehaviour linkedController = (UdonBehaviour)behaviour.GetProgramVariable("Controller");
                                if (linkedController != permissionController) continue;
                                found = behaviour;
                                break;
                            }
                            if (found != null) break;
                        }
                    }
                }
                cachedPermissionSource = found;
                if (found != null && (bool)found.GetProgramVariable("IsInitialized"))
                {
                    int value = (int)found.GetProgramVariable("CurrentPermission");
                    known = true;
                    allowed = _PermissionValueAllowed(value);
                }
            }
            if (permissionKnown && permissionAllowed && (!known || !allowed)) RevokeAccess();
            permissionKnown = known;
            permissionAllowed = allowed;
        }

        private void RevokeAccess()
        {
            if (inFlight) RetireUrl(activeUrl);
            inFlight = false; activeUrl = ""; manualPending = false; hasSuccess = false;
            ranges = null; rangeOrder = null; currentRev = ""; updatedUnix = 0d; aliveUnix = 0d; failed = false;
            failCount = 0; nextFetchAt = Time.realtimeSinceStartup + 1f;
            revision++;
        }

        private bool IsRetired(string url)
        {
            if (retiredUrls == null) return false;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < retiredUrlCount; i++)
            {
                if (string.IsNullOrEmpty(retiredUrls[i])) continue;
                if (now - retiredAt[i] >= RetireSeconds)
                {
                    retiredUrls[i] = "";
                    retiredAt[i] = 0f;
                    continue;
                }
                if (retiredUrls[i] == url) return true;
            }
            return false;
        }

        private void RetireUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || retiredUrls == null || IsRetired(url)) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < retiredUrlCount; i++)
            {
                if (string.IsNullOrEmpty(retiredUrls[i]))
                {
                    retiredUrls[i] = url;
                    retiredAt[i] = now;
                    return;
                }
            }
            if (retiredUrlCount < retiredUrls.Length)
            {
                retiredUrls[retiredUrlCount] = url;
                retiredAt[retiredUrlCount] = now;
                retiredUrlCount++;
            }
        }

        private void UnretireUrl(string url)
        {
            if (retiredUrls == null) return;
            for (int i = 0; i < retiredUrlCount; i++)
            {
                if (retiredUrls[i] == url)
                {
                    retiredUrls[i] = "";
                    retiredAt[i] = 0f;
                    return;
                }
            }
        }
    }
}
