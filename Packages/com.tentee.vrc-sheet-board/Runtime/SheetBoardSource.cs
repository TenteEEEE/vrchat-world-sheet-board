using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

namespace SheetBoard
{
    public enum SheetBoardAudience { Public = 0, Staff = 1, Admin = 2 }

    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SheetBoardSource : UdonSharpBehaviour
    {
        private const float TickInterval = 1f;
        private const float MinimumRefresh = 30f;
        private const float MaximumBackoff = 300f;
        private const float Timeout = 60f;
        private const float RetireSeconds = 2f * Timeout;
        private const double MaximumUnix = 253402300799d;
        private const double SecondsPerMinute = 60d;
        private const double MinutesPerHour = 60d;
        private const double MinutesPerDay = 1440d;

        public VRCUrl[] urls;
        public string baseUrl = "";
        public float refreshInterval = 60f;
        public float nearRadius = 0f;
        public float staleMinutes = 90f;
        public float manualCooldown = 5f;
        public float startDelayMax = 3f;
        public SheetBoardAudience audience = SheetBoardAudience.Public;
        public UdonBehaviour permissionController;
        public string permissionDeniedText = "閲覧権限がありません";
        public string permissionWaitingText = "権限を確認中…";

        public string urlMissingText = "URLが設定されていません";
        public string loadingText = "読み込み中…";
        public string waitingNearText = "近づくと読み込みます";
        public string failedText = "読み込めません・{s}秒後に再試行（{code}）";
        public string freshText = "更新 {time}（{ago}）";
        public string staleText = "更新 {time}（{ago}）・自動公開が止まっています";
        public string retryText = "更新 {time}（{ago}）・再試行中";
        public string refreshingSuffix = "・更新中…";
        public string minutesUnit = "分前";
        public string hoursUnit = "時間前";
        public string daysUnit = "日前";

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

        private void OnEnable()
        {
            float now = Time.realtimeSinceStartup;
            EvaluatePermission();
            if (!initialized)
            {
                initialized = true;
                nextFetchAt = now + UnityEngine.Random.Range(0f, Mathf.Max(0f, startDelayMax));
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

        private void OnDisable()
        {
            if (audience == SheetBoardAudience.Public) return;
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
            if (inFlight || (hasManual && now - lastManualAt < manualCooldown)) return;
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
                float remaining = manualCooldown - (Time.realtimeSinceStartup - lastManualAt);
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
            if (!data.TryGetValue("format", out format) || format.TokenType != TokenType.String || format.String != "sheetboard/1") return InvalidJson();
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
            if (audience != SheetBoardAudience.Public && !permissionKnown) { statusText = permissionWaitingText; return; }
            if (audience != SheetBoardAudience.Public && !permissionAllowed) { statusText = permissionDeniedText; return; }
            if (validUrlCount == 0) { statusText = urlMissingText; return; }
            if (inFlight && !hasSuccess) { statusText = loadingText; return; }
            if (!hasSuccess)
            {
                if (failed)
                {
                    int seconds = Mathf.CeilToInt(Mathf.Max(0f, nextFetchAt - Time.realtimeSinceStartup));
                    statusText = failedText.Replace("{code}", errorCode).Replace("{s}", seconds.ToString());
                }
                else if (manualPending) statusText = loadingText;
                else if (nearRadius > 0f && (!hasNear || Time.realtimeSinceStartup - lastNearAt > nearWindow)) statusText = waitingNearText;
                else statusText = loadingText;
                return;
            }
            DateTime now = Utilities.IsValid(Networking.LocalPlayer) ? Networking.GetNetworkDateTime() : DateTime.UtcNow;
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double nowUnix = (now - epoch).TotalSeconds;
            double minutes = Math.Max(0d, nowUnix - updatedUnix) / SecondsPerMinute;
            string elapsed = ((int)minutes).ToString() + minutesUnit;
            if (minutes >= MinutesPerDay) elapsed = ((int)(minutes / MinutesPerDay)).ToString() + daysUnit;
            else if (minutes >= MinutesPerHour) elapsed = ((int)(minutes / MinutesPerHour)).ToString() + hoursUnit;
            bool stale = aliveUnix > 0d && (nowUnix - aliveUnix) / SecondsPerMinute > staleMinutes;
            statusLevel = failed || stale ? 1 : 0;
            string template = failed ? retryText : (stale ? staleText : freshText);
            DateTime localTime = epoch.AddSeconds(updatedUnix).ToLocalTime();
            string time = localTime.Hour.ToString("00") + ":" + localTime.Minute.ToString("00");
            statusText = template.Replace("{time}", time).Replace("{ago}", elapsed);
            if (IsBusy()) statusText += refreshingSuffix;
        }

        public string GetStatusText() { return statusText; }
        public int GetStatusLevel() { return statusLevel; }
        public int GetAccessStatus() { return HasPermission() ? 0 : (permissionKnown ? 2 : 1); }

        private bool HasPermission()
        {
            return audience == SheetBoardAudience.Public || (permissionKnown && permissionAllowed);
        }

        public bool _PermissionValueAllowed(int permissionValue)
        {
            if (audience == SheetBoardAudience.Public) return true;
            if (permissionValue == 0) return true;
            return audience == SheetBoardAudience.Staff && permissionValue == 1;
        }

        private void EvaluatePermission()
        {
            if (audience == SheetBoardAudience.Public) { permissionKnown = true; permissionAllowed = true; return; }
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
