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
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SheetBoardSource : UdonSharpBehaviour
    {
        private const float TickInterval = 1f;
        private const float MinimumRefresh = 30f;
        private const float MaximumBackoff = 300f;
        private const float Timeout = 60f;
        private const double MaximumUnix = 253402300799d;
        private const double MinutesPerHour = 60d;
        private const double MinutesPerDay = 1440d;

        public VRCUrl[] urls;
        public string baseUrl = "";
        public float refreshInterval = 60f;
        public float nearRadius = 0f;
        public float staleMinutes = 90f;
        public float manualCooldown = 30f;
        public float startDelayMax = 3f;

        public string urlMissingText = "URLが設定されていません";
        public string loadingText = "読み込み中…";
        public string waitingNearText = "近づくと読み込みます";
        public string failedText = "読み込めません（{code}）  再試行まで{s}秒";
        public string freshText = "更新 {time}（{ago}）";
        public string staleText = "更新 {time}（{ago}）・自動公開が止まっています";
        public string retryText = "更新 {time}（{ago}）・再試行中";
        public string minutesUnit = "分前";
        public string hoursUnit = "時間前";
        public string daysUnit = "日前";

        private DataDictionary ranges;
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

        private void OnEnable()
        {
            float now = Time.realtimeSinceStartup;
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
                }
            }
            if (nextTickAt == 0f || now > nextTickAt + 2f * TickInterval)
            {
                nextTickAt = now + TickInterval;
                SendCustomEventDelayedSeconds(nameof(_Tick), TickInterval);
            }
        }

        public void _Tick()
        {
            float now = Time.realtimeSinceStartup;
            // Delayed Udon events carry no arguments; the deadline suppresses old loops.
            if (now < nextTickAt - 0.5f * TickInterval) return;
            nextTickAt = now + TickInterval;
            SendCustomEventDelayedSeconds(nameof(_Tick), TickInterval);

            if (inFlight && now - startedAt >= Timeout)
            {
                inFlight = false;
                activeUrl = "";
                errorCode = "timeout";
                RecordFailure();
            }
            if (!inFlight && now >= nextFetchAt && validUrlCount > 0 &&
                (manualPending || nearRadius <= 0f || (hasNear && now - lastNearAt <= nearWindow)))
            {
                for (int i = 0; i < urls.Length; i++)
                {
                    if (urlIndex >= urls.Length || urlIndex < 0) urlIndex = 0;
                    VRCUrl url = urls[urlIndex];
                    urlIndex = (urlIndex + 1) % urls.Length;
                    if (url == null || string.IsNullOrEmpty(url.Get())) continue;
                    manualPending = false;
                    inFlight = true;
                    startedAt = now;
                    activeUrl = url.Get();
                    VRCStringDownloader.LoadUrl(url, (IUdonEventReceiver)this);
                    break;
                }
            }
            UpdateStatus();
        }

        public void RequestRefresh()
        {
            float now = Time.realtimeSinceStartup;
            if (inFlight || (hasManual && now - lastManualAt < manualCooldown)) return;
            hasManual = true;
            lastManualAt = now;
            manualPending = true;
            nextFetchAt = now;
        }

        public override void OnStringLoadSuccess(IVRCStringDownload result)
        {
            if (!inFlight || result.Url == null || result.Url.Get() != activeUrl) return;
            inFlight = false;
            activeUrl = "";
            ApplyJson(result.Result);
        }

        public override void OnStringLoadError(IVRCStringDownload result)
        {
            if (!inFlight || result.Url == null || result.Url.Get() != activeUrl) return;
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
            DataToken root;
            DataToken format;
            DataToken rev;
            DataToken updated;
            DataToken rangeToken;
            DataToken alive;
            if (string.IsNullOrEmpty(json) || !VRCJson.TryDeserializeFromJson(json, out root) || root.TokenType != TokenType.DataDictionary) return InvalidJson();
            DataDictionary data = root.DataDictionary;
            if (!data.TryGetValue("format", out format) || format.TokenType != TokenType.String || format.String != "sheetboard/1") return InvalidJson();
            if (!data.TryGetValue("rev", out rev) || rev.TokenType != TokenType.String) return InvalidJson();
            if (!data.TryGetValue("updatedUnix", out updated) || !IsNumber(updated)) return InvalidJson();
            if (!data.TryGetValue("ranges", out rangeToken) || rangeToken.TokenType != TokenType.DataDictionary) return InvalidJson();
            double newUpdated = Number(updated);
            if (!(newUpdated > 0d && newUpdated < MaximumUnix)) return InvalidJson();
            double newAlive = data.TryGetValue("aliveUnix", out alive) && IsNumber(alive) ? Number(alive) : 0d;
            if (!(newAlive > 0d && newAlive < MaximumUnix)) newAlive = 0d;
            aliveUnix = Math.Max(aliveUnix, newAlive);
            if (!hasSuccess || (newUpdated >= updatedUnix && rev.String != currentRev))
            {
                ranges = rangeToken.DataDictionary;
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

        public bool HasData() { return hasSuccess; }
        public int GetRevision() { return revision; }

        public DataDictionary GetRange(string name)
        {
            DataToken value;
            if (ranges == null || name == null || !ranges.TryGetValue(name, out value) || value.TokenType != TokenType.DataDictionary) return null;
            return value.DataDictionary;
        }

        public string GetFirstRangeName()
        {
            if (ranges == null) return "";
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
            if (validUrlCount == 0) { statusText = urlMissingText; return; }
            if (inFlight && !hasSuccess) { statusText = loadingText; return; }
            if (!hasSuccess)
            {
                if (failed)
                {
                    int seconds = Mathf.CeilToInt(Mathf.Max(0f, nextFetchAt - Time.realtimeSinceStartup));
                    statusText = failedText.Replace("{code}", errorCode).Replace("{s}", seconds.ToString());
                }
                else if (nearRadius > 0f && (!hasNear || Time.realtimeSinceStartup - lastNearAt > nearWindow)) statusText = waitingNearText;
                else statusText = loadingText;
                return;
            }
            DateTime now = Utilities.IsValid(Networking.LocalPlayer) ? Networking.GetNetworkDateTime() : DateTime.UtcNow;
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double nowUnix = (now - epoch).TotalSeconds;
            double minutes = Math.Max(0d, nowUnix - updatedUnix) / MinutesPerHour;
            string elapsed = ((int)minutes).ToString() + minutesUnit;
            if (minutes >= MinutesPerDay) elapsed = ((int)(minutes / MinutesPerDay)).ToString() + daysUnit;
            else if (minutes >= MinutesPerHour) elapsed = ((int)(minutes / MinutesPerHour)).ToString() + hoursUnit;
            bool stale = aliveUnix > 0d && (nowUnix - aliveUnix) / MinutesPerHour > staleMinutes;
            statusLevel = failed || stale ? 1 : 0;
            string template = failed ? retryText : (stale ? staleText : freshText);
            DateTime localTime = epoch.AddSeconds(updatedUnix).ToLocalTime();
            string time = localTime.Hour.ToString("00") + ":" + localTime.Minute.ToString("00");
            statusText = template.Replace("{time}", time).Replace("{ago}", elapsed);
        }

        public string GetStatusText() { return statusText; }
        public int GetStatusLevel() { return statusLevel; }
    }
}
