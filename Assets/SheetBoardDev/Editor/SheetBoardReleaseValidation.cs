#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;
using SheetBoard;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDKBase;

namespace SheetBoardDevEditor
{
    public static class SheetBoardReleaseValidation
    {
        public static void VerifyBatch()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
                SheetBoardSource source = new GameObject("Regression Source").AddUdonSharpComponent<SheetBoardSource>();
                source.urls = new[] { new VRCUrl("https://gist.githubusercontent.com/test/sheetboard.json") };
                Set(source, "validUrlCount", 1);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Require(source.ApplyJson(Json("new", now, now, "new")), "Valid data was rejected");
                int revision = source.GetRevision();
                Require(source.ApplyJson(Json("old", now - 60, now - 60, "old")), "Old data must count as a successful download");
                Require(source.GetRevision() == revision && source.GetRange("new") != null && source.GetRange("old") == null, "CDN data rolled back");
                foreach (string timestamp in new[] { "0", "-1", "253402300799", "1e300", "\"bad\"" })
                    Require(!source.ApplyJson("{\"format\":\"sheetboard/1\",\"rev\":\"invalid\",\"updatedUnix\":" + timestamp + ",\"ranges\":{}}"), "Invalid timestamp accepted: " + timestamp);
                Require(source.GetRevision() == revision && source.GetRange("new") != null, "Invalid data destroyed the last success");
                Require(source.ApplyJson(Json("new", now, now, "new")), "Recovery failed");
                Set(source, "inFlight", true);
                Status(source);
                Require(source.GetStatusLevel() == 0 && source.GetStatusText() != source.loadingText, "Periodic fetch replaced successful status with loading");
                Set(source, "failed", true);
                Status(source);
                Require(source.GetStatusLevel() == 1 && source.GetStatusText().Contains("再試行中"), "Retry status disappeared during fetch");
                Set(source, "failed", false);
                Set(source, "aliveUnix", (double)(now - 7200));
                Status(source);
                Require(source.GetStatusLevel() == 1 && source.GetStatusText().Contains("止まっています"), "Stale status disappeared during fetch");
                Debug.Log("[SheetBoard] Release regression OK: timestamp limits, CDN rollback, preserved data, in-flight status.");
                SheetBoardDev.SheetBoardPreview.RenderSampleBatch();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        private static string Json(string rev, long updated, long alive, string range)
        {
            return "{\"format\":\"sheetboard/1\",\"rev\":\"" + rev + "\",\"updatedUnix\":" + updated.ToString(CultureInfo.InvariantCulture) +
                ",\"aliveUnix\":" + alive.ToString(CultureInfo.InvariantCulture) + ",\"ranges\":{\"" + range + "\":{\"rows\":[[\"test\"]]}}}";
        }

        private static void Set(SheetBoardSource source, string name, object value)
        {
            typeof(SheetBoardSource).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(source, value);
        }

        private static void Status(SheetBoardSource source)
        {
            typeof(SheetBoardSource).GetMethod("UpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(source, null);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
