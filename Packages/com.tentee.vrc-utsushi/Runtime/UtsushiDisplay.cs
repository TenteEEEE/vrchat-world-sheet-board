using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace Utsushi
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class UtsushiDisplay : UdonSharpBehaviour
    {
        private const string JapaneseRangeMissingText = "範囲「{name}」が見つかりません";
        private const string EnglishRangeMissingText = "Range \"{name}\" was not found";
        private const string JapaneseSourceMissingText = "データ元が設定されていません";
        private const string EnglishSourceMissingText = "No data source is set";
        private const float MinReadableRowHeight = 12f;
        private const string JapaneseRefreshIdleText = "更新";
        private const string EnglishRefreshIdleText = "Refresh";

        [Header("Settings")]
        public UtsushiSource source;
        public string rangeName = "";
        public bool showTitle = true;
        public bool zebra = false;
        public bool lightTheme = true;
        public bool showGrid = true;
        public float gridWidth = 1f;
        public float nearCheckInterval = 1f;
        public float maxRowHeight = 64f;
        public float textScale = 1.4f;
        public bool autoFitColumns = true;
        public float columnGap = 12f;
        public bool chipAutoPalette = true;
        public Color[] chipPalette;
        [Header("Colours")]
        public Color boardColor, bodyColor, gridColor, headerBackgroundColor, zebraColor, chipColor, chipArrowColor, normalColor, staleColor, errorColor, busyColor, refreshButtonColor, refreshLabelColor, refreshDisabledColor, refreshDisabledLabelColor;
        [Header("Texts")]
        public string rangeMissingText = "";
        public string sourceMissingText = "";
        public string refreshIdleText = "";

        [Header("Internal references (installer-wired)")]
        public Image background;
        public TMP_Text titleText, statusText;
        public RectTransform tableArea;
        public TMP_Text columnTemplate, overlayTemplate, overlayNoWrapTemplate;
        public Image cellTemplate, chipTemplate, arrowTemplate, statusDot;
        public Button refreshButton;
        public TMP_Text refreshLabel;

        private const float MinInterval = 0.25f, MaxInterval = 1.5f, Padding = 6f, DefaultTextScale = 1.4f;
        private const float ChipMinContrast = 3f;
        // Conservative safety value below TMP's 16,383-character per-mesh ceiling.
        private const int CellGlyphCeiling = 4000;
        private const int SegmentGlyphBudget = 6000;
        private const float ChipMargin = 3f, ChipHeightEm = 1.55f, ChipPadEm = 0.45f, ArrowWidthEm = 0.62f, ArrowHeightEm = 0.36f, ChipExtraEm = 1.3f, ChipSpriteHeight = 64f;
        private const float MinFontFactor = 0.6f, MaxFontFactor = 1.6f;
        private const int MinMeasuredColumnWidth = 2, MaxMeasuredColumnWidth = 40;
        private const float FitFontWidthFactor = 0.55f, DefaultCellHeightFactor = 0.62f, VerticalOffsetFactor = 0.78f;
        private const float RowFitRatio = 0.70f, MinFontRatio = 0.5f, BoldFactor = 1.06f, SafetyMargin = 1.05f, SpacerEm = 0.6f;
        private const float GlyphCenterFactor = 0.38f;
        private const int MaxFontSizeKinds = 16;
        private const float DefaultBodyRed = 0x20 / 255f, DefaultBodyGreen = 0x21 / 255f, DefaultBodyBlue = 0x24 / 255f;
        private float nextWatchAt;
        private bool watchStarted;
        private bool warnedTemplates;
        private bool busyLampOn;
        private UtsushiSource displayedSource;
        private bool colorsReady;
        private RectTransform[] segmentFrames;
        private TMP_Text[] segments;
        private Image[] cells;
        private Image[] chips, arrows;
        private RectTransform chipContainer, arrowContainer;
        private int chipCount;
        private bool chipPoolUnavailable;
        private bool warnedChipPool;
        private RectTransform[] overlayFrames;
        private TMP_Text[] overlays;
        private RectTransform[] overlayNoWrapFrames;
        private TMP_Text[] overlaysNoWrap;
        private int cellCount;
        private int cellCreatedCount;
        private int overlayCount;
        private int overlayCreatedCount;
        private int overlayNoWrapCount;
        private int overlayNoWrapCreatedCount;
        private int segmentCount;
        private int segmentCreatedCount;
        private int chipCreatedCount;
        private int displayedRevision = -1;
        private RectTransform[] segmentTextRects;
        private int[] segmentColumn;
        private int[] segmentFirstRow;
        private int[] segmentLastRow;
        private RectTransform[] overlayTextRects;
        private RectTransform[] overlayNoWrapTextRects;
        private float workBasePt;
        private float[] workXs, workWs, workYs, workHs;
        private float workHeightAll, workBasePx;
        private int workRows, workCols;
        private bool workHeader;
        private string workBodyHex;
        private DataList workMerges;
        private string[] workRaw;
        private int[] workAscii, workOther, workBits, workAlignment, workSpanRows, workSpanCols, workLongest;
        private float[] workPt;
        private Color[] workFg, workBg, workChipBg, workChipFg;
        private bool[] workHasFg, workHasBg, workHasChipBg, workHasChipFg, workWrapping, workCovered, workOverlayUsed, workSpillCandidates;
        private int[] workChipKind;
        private Color[] workAutoChipColors;
        private bool[] workHasAutoChipColor;
        private Image refreshButtonImage;
        private bool overlayPoolUnavailable;
        private bool overlayNoWrapPoolUnavailable;
        private bool cellPoolUnavailable;
        private bool warnedCellPool;
        private bool warnedOverlayPool;
        private bool warnedOverlayNoWrapPool;
        private bool segmentPoolUnavailable;
        private bool warnedSegmentPool;
        private Image grid;
        private UtsushiLanguage resolvedLanguage = UtsushiLanguage.English;
        private bool languageResolved;
        private bool rangeMissingShown;
        private bool tableTooLarge;

        private void ResolveLanguage()
        {
            if (source != null) { resolvedLanguage = source.GetResolvedLanguage(); languageResolved = true; return; }
            string current = VRCPlayerApi.GetCurrentLanguage();
            resolvedLanguage = !string.IsNullOrEmpty(current) && current.Length >= 2 && (current[0] == 'j' || current[0] == 'J') && (current[1] == 'a' || current[1] == 'A') ? UtsushiLanguage.Japanese : UtsushiLanguage.English;
            languageResolved = true;
        }

        private string ResolveText(string value, string japanese, string english)
        {
            if (!languageResolved) ResolveLanguage();
            if (string.IsNullOrEmpty(value)) return resolvedLanguage == UtsushiLanguage.Japanese ? japanese : english;
            if (value == japanese || value == "Sourceが設定されていません" || value == "No Source is set." || value == "Range \"{name}\" was not found.") return resolvedLanguage == UtsushiLanguage.Japanese ? japanese : english;
            return value;
        }

        private string GetRangeMissingText() { return ResolveText(rangeMissingText, JapaneseRangeMissingText, EnglishRangeMissingText); }
        private string GetSourceMissingText() { return ResolveText(sourceMissingText, JapaneseSourceMissingText, EnglishSourceMissingText); }
        private string GetRefreshIdleText() { return ResolveText(refreshIdleText, JapaneseRefreshIdleText, EnglishRefreshIdleText); }
        private string GetMissingRangeMessage()
        {
            if (source != null && source.HasNoRanges()) return resolvedLanguage == UtsushiLanguage.Japanese ? "表示する範囲がまだありません" : "No range has been published yet";
            string name = string.IsNullOrEmpty(rangeName) ? source.GetFirstRangeName() : rangeName;
            return GetRangeMissingText().Replace("{name}", name);
        }

        private void InitColors()
        {
            if (colorsReady) return;
            colorsReady = true;
            if (boardColor.a <= 0f) boardColor = lightTheme ? Color.white : new Color(0x12 / 255f, 0x16 / 255f, 0x1C / 255f, 1f);
            if (bodyColor.a <= 0f) bodyColor = lightTheme ? new Color(DefaultBodyRed, DefaultBodyGreen, DefaultBodyBlue, 1f) : new Color(0xE8 / 255f, 0xED / 255f, 0xF2 / 255f, 1f);
            if (gridColor.a <= 0f) gridColor = lightTheme ? new Color(0xE2 / 255f, 0xE3 / 255f, 0xE3 / 255f, 1f) : new Color(1f, 1f, 1f, 0.08f);
            if (headerBackgroundColor.a <= 0f) headerBackgroundColor = lightTheme ? new Color(0x0F / 255f, 0x76 / 255f, 0x6E / 255f, 0.08f) : new Color(0x5E / 255f, 0xC4 / 255f, 0xBA / 255f, 0.12f);
            if (zebraColor.a <= 0f) zebraColor = lightTheme ? new Color(0f, 0f, 0f, 0.025f) : new Color(1f, 1f, 1f, 0.015f);
            if (normalColor.a <= 0f) normalColor = lightTheme ? new Color(0x1E / 255f, 0x8E / 255f, 0x3E / 255f, 1f) : new Color(0x81 / 255f, 0xC9 / 255f, 0x95 / 255f, 1f);
            if (staleColor.a <= 0f) staleColor = lightTheme ? new Color(0xB0 / 255f, 0x60 / 255f, 0x00 / 255f, 1f) : new Color(0xFD / 255f, 0xD6 / 255f, 0x63 / 255f, 1f);
            if (errorColor.a <= 0f) errorColor = lightTheme ? new Color(0xD9 / 255f, 0x30 / 255f, 0x25 / 255f, 1f) : new Color(0xF2 / 255f, 0x8B / 255f, 0x82 / 255f, 1f);
            if (busyColor.a <= 0f) busyColor = lightTheme ? new Color(0x1A / 255f, 0x73 / 255f, 0xE8 / 255f, 1f) : new Color(0x8A / 255f, 0xB4 / 255f, 0xF8 / 255f, 1f);
            if (refreshButtonColor.a <= 0f) refreshButtonColor = lightTheme ? new Color(0x0F / 255f, 0x76 / 255f, 0x6E / 255f, 1f) : new Color(0x1F / 255f, 0x6F / 255f, 0x69 / 255f, 1f);
            if (refreshLabelColor.a <= 0f) refreshLabelColor = lightTheme ? new Color(0xFF / 255f, 0xFF / 255f, 0xFF / 255f, 1f) : new Color(0xF1 / 255f, 0xF5 / 255f, 0xF7 / 255f, 1f);
            if (refreshDisabledColor.a <= 0f) refreshDisabledColor = lightTheme ? new Color(0xE3 / 255f, 0xE6 / 255f, 0xE8 / 255f, 1f) : new Color(0x25 / 255f, 0x2B / 255f, 0x33 / 255f, 1f);
            if (refreshDisabledLabelColor.a <= 0f) refreshDisabledLabelColor = lightTheme ? new Color(0x56 / 255f, 0x61 / 255f, 0x6B / 255f, 1f) : new Color(0x8B / 255f, 0x96 / 255f, 0xA1 / 255f, 1f);
            if (chipColor.a <= 0f) chipColor = lightTheme ? new Color32(0xE8, 0xEA, 0xED, 0xFF) : new Color32(0x3C, 0x40, 0x43, 0xFF);
            if (chipPalette == null || chipPalette.Length == 0)
            {
                if (lightTheme) chipPalette = new Color[] { new Color32(0xF8, 0xD7, 0xDA, 0xFF), new Color32(0xFD, 0xE2, 0xC0, 0xFF), new Color32(0xFF, 0xF1, 0xB0, 0xFF), new Color32(0xD6, 0xF0, 0xCF, 0xFF), new Color32(0xCB, 0xED, 0xE7, 0xFF), new Color32(0xD2, 0xE4, 0xFB, 0xFF), new Color32(0xE1, 0xDA, 0xF6, 0xFF), new Color32(0xF6, 0xD6, 0xEA, 0xFF), new Color32(0xE8, 0xE1, 0xD2, 0xFF), new Color32(0xD8, 0xE0, 0xE7, 0xFF) };
                else chipPalette = new Color[] { new Color32(0x7A, 0x2E, 0x3A, 0xFF), new Color32(0x7A, 0x4A, 0x16, 0xFF), new Color32(0x66, 0x57, 0x0F, 0xFF), new Color32(0x2F, 0x5E, 0x32, 0xFF), new Color32(0x1F, 0x5C, 0x56, 0xFF), new Color32(0x24, 0x49, 0x7A, 0xFF), new Color32(0x4A, 0x3B, 0x7A, 0xFF), new Color32(0x6E, 0x2E, 0x5C, 0xFF), new Color32(0x5A, 0x4E, 0x3A, 0xFF), new Color32(0x3C, 0x4A, 0x57, 0xFF) };
            }
            if (chipArrowColor.a <= 0f) chipArrowColor = lightTheme ? new Color32(0x5F, 0x63, 0x68, 0xFF) : new Color32(0x9A, 0xA0, 0xA6, 0xFF);
        }

        private void OnEnable()
        {
            ResolveLanguage();
            if (refreshButton == null) refreshButton = GetComponentInChildren<Button>();
            if (refreshButton != null) refreshButtonImage = refreshButton.GetComponent<Image>();
            if (refreshLabel == null && refreshButton != null) refreshLabel = refreshButton.GetComponentInChildren<TMP_Text>();
            int access = source == null ? 2 : source.GetAccessStatus();
            bool busy = source != null && access == 0 && source.IsBusy();
            UpdateRefreshButton(access, busy);
            if (source != null && access != 0) RenderNow();
            float interval = Mathf.Clamp(nearCheckInterval, MinInterval, MaxInterval);
            float now = Time.realtimeSinceStartup;
            if (watchStarted && now <= nextWatchAt + 2f * interval) return;
            watchStarted = true;
            float delay = Random.Range(0f, interval);
            nextWatchAt = now + delay;
            SendCustomEventDelayedSeconds(nameof(_Watch), delay);
        }

        public override void OnLanguageChanged(string language)
        {
            bool rerenderRangeMissing = rangeMissingShown;
            ResolveLanguage();
            int access = source == null ? 2 : source.GetAccessStatus();
            bool busy = source != null && access == 0 && source.IsBusy();
            RefreshIndicators(access, busy);
            if (rerenderRangeMissing) RenderNow();
        }

        public void _Watch()
        {
            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Clamp(nearCheckInterval, MinInterval, MaxInterval);
            if (now < nextWatchAt - 0.5f * interval) return;
            nextWatchAt = now + interval;
            SendCustomEventDelayedSeconds(nameof(_Watch), interval);
            int access = source == null ? 2 : source.GetAccessStatus();
            bool busy = source != null && access == 0 && source.IsBusy();
            RefreshIndicators(access, busy);
        }

        private void SetStatus(string value, Color color)
        {
            if (statusText != null && statusText.text != value) statusText.text = value;
            if (statusDot != null) statusDot.color = color;
        }

        public void OnRefreshPressed()
        {
            int access = source == null ? 2 : source.GetAccessStatus();
            if (source != null && access == 0) source.RequestRefresh();
            bool busy = source != null && access == 0 && source.IsBusy();
            RefreshIndicators(access, busy);
        }

        private void RefreshIndicators(int access, bool busy)
        {
            InitColors();
            if (source == null) SetStatus(GetSourceMissingText(), errorColor);
            else if (access != 0)
            {
                if (displayedSource != source || displayedRevision != source.GetRevision()) RenderNow();
                SetStatus(access == 2 ? source.GetPermissionDeniedText() : source.GetPermissionWaitingText(), access == 2 ? errorColor : busyColor);
            }
            else
            {
                VRCPlayerApi player = Networking.LocalPlayer;
                float interval = Mathf.Clamp(nearCheckInterval, MinInterval, MaxInterval);
                if (source.nearRadius > 0f && Utilities.IsValid(player) && Vector3.Distance(player.GetPosition(), transform.position) <= source.nearRadius) source._ReportNear(interval);
                if (displayedSource != source || displayedRevision != source.GetRevision()) RenderNow();
                int level = source.GetStatusLevel();
                string message = source.GetStatusText();
                Color color = level == 0 ? normalColor : (level == 1 ? staleColor : (level == 3 ? busyColor : errorColor));
                if (rangeMissingShown && level != 2 && level != 3) { message = GetMissingRangeMessage(); color = errorColor; }
                if (tableTooLarge) { message += resolvedLanguage == UtsushiLanguage.Japanese ? "・表が大きく文字が小さくなっています" : " · table is too large to read comfortably"; if (level != 2 && !rangeMissingShown) color = staleColor; }
                SetStatus(message, color);
            }
            if (busy && !tableTooLarge && !rangeMissingShown)
            {
                busyLampOn = !busyLampOn;
                Color color = busyColor;
                color.a = busyLampOn ? 1f : 0.35f;
                if (statusDot != null) statusDot.color = color;
            }
            else busyLampOn = false;
            UpdateRefreshButton(access, busy);
        }

        private void UpdateRefreshButton(int access, bool busy)
        {
            InitColors();
            if (refreshButtonImage == null && refreshButton != null) refreshButtonImage = refreshButton.GetComponent<Image>();
            bool interactable = source != null && access == 0 && source.GetRefreshWaitSeconds() == 0f;
            if (refreshButton != null) refreshButton.interactable = interactable;
            Color buttonColor = interactable ? refreshButtonColor : refreshDisabledColor;
            if (refreshButtonImage != null && refreshButtonImage.color != buttonColor) refreshButtonImage.color = buttonColor;
            if (refreshLabel != null)
            {
                Color labelColor = interactable ? refreshLabelColor : refreshDisabledLabelColor;
                if (refreshLabel.color != labelColor) refreshLabel.color = labelColor;
                string label = GetRefreshIdleText();
                if (refreshLabel.text != label) refreshLabel.text = label;
            }
        }

        private void InitPools()
        {
            if (!warnedTemplates && (tableArea == null || columnTemplate == null || cellTemplate == null || overlayTemplate == null || overlayNoWrapTemplate == null))
            {
                warnedTemplates = true;
                Debug.LogWarning("UtsushiDisplay: missing table area or template reference.");
            }
            if ((tableArea == null || cellTemplate == null) && !warnedCellPool) { warnedCellPool = true; cellPoolUnavailable = true; Debug.LogWarning("UtsushiDisplay: cell pool unavailable. count=" + cellCreatedCount); }
            if ((tableArea == null || columnTemplate == null) && !warnedSegmentPool) { warnedSegmentPool = true; segmentPoolUnavailable = true; Debug.LogWarning("UtsushiDisplay: segment pool unavailable. count=" + segmentCreatedCount); }
            if ((tableArea == null || overlayTemplate == null) && !warnedOverlayPool) { warnedOverlayPool = true; overlayPoolUnavailable = true; Debug.LogWarning("UtsushiDisplay: overlay pool unavailable. count=" + overlayCreatedCount); }
            if ((tableArea == null || overlayNoWrapTemplate == null) && !warnedOverlayNoWrapPool) { warnedOverlayNoWrapPool = true; overlayNoWrapPoolUnavailable = true; Debug.LogWarning("UtsushiDisplay: overlayNoWrap pool unavailable. count=" + overlayNoWrapCreatedCount); }
            if ((tableArea == null || chipTemplate == null || arrowTemplate == null) && !warnedChipPool) { warnedChipPool = true; chipPoolUnavailable = true; Debug.LogWarning("UtsushiDisplay: chip pool unavailable. count=" + chipCreatedCount); }
            if (cells != null || tableArea == null || columnTemplate == null || cellTemplate == null) return;
            segmentFrames = new RectTransform[0];
            segments = new TMP_Text[0];
            segmentTextRects = new RectTransform[0];
            segmentColumn = new int[0];
            segmentFirstRow = new int[0];
            segmentLastRow = new int[0];
            cells = new Image[0];
            chips = new Image[0];
            arrows = new Image[0];
            overlayFrames = new RectTransform[0];
            overlays = new TMP_Text[0];
            overlayTextRects = new RectTransform[0];
            overlayNoWrapFrames = new RectTransform[0];
            overlaysNoWrap = new TMP_Text[0];
            overlayNoWrapTextRects = new RectTransform[0];
            GameObject underlay = Instantiate(cellTemplate.gameObject, tableArea);
            grid = underlay.GetComponent<Image>();
            underlay.transform.SetAsFirstSibling();
            underlay.SetActive(false);
            chipContainer = chipTemplate == null || chipTemplate.transform.parent == null ? null : (RectTransform)chipTemplate.transform.parent;
            arrowContainer = arrowTemplate == null || arrowTemplate.transform.parent == null ? null : (RectTransform)arrowTemplate.transform.parent;
        }

        private int GrowCapacity(int current, int required)
        {
            int capacity = Mathf.Max(1, current);
            while (capacity < required) capacity *= 2;
            return Mathf.Max(required, capacity);
        }

        private void EnsureImageCapacity(int required)
        {
            if (cells.Length >= required) return;
            int size = GrowCapacity(cells.Length, required);
            Image[] next = new Image[size];
            System.Array.Copy(cells, next, cells.Length);
            cells = next;
        }

        private void EnsureChipCapacity(int required)
        {
            if (chips.Length >= required) return;
            int size = GrowCapacity(chips.Length, required);
            Image[] nextChips = new Image[size];
            Image[] nextArrows = new Image[size];
            System.Array.Copy(chips, nextChips, chips.Length);
            System.Array.Copy(arrows, nextArrows, arrows.Length);
            chips = nextChips;
            arrows = nextArrows;
        }

        private void EnsureRectCapacity(int required, bool noWrap)
        {
            RectTransform[] sourceFrames = noWrap ? overlayNoWrapFrames : overlayFrames;
            if (sourceFrames.Length >= required) return;
            int size = GrowCapacity(sourceFrames.Length, required);
            RectTransform[] nextFrames = new RectTransform[size];
            TMP_Text[] nextTexts = new TMP_Text[size];
            RectTransform[] nextTextRects = new RectTransform[size];
            System.Array.Copy(sourceFrames, nextFrames, sourceFrames.Length);
            if (noWrap)
            {
                System.Array.Copy(overlaysNoWrap, nextTexts, overlaysNoWrap.Length);
                System.Array.Copy(overlayNoWrapTextRects, nextTextRects, overlayNoWrapTextRects.Length);
                overlayNoWrapFrames = nextFrames; overlaysNoWrap = nextTexts; overlayNoWrapTextRects = nextTextRects;
            }
            else
            {
                System.Array.Copy(overlays, nextTexts, overlays.Length);
                System.Array.Copy(overlayTextRects, nextTextRects, overlayTextRects.Length);
                overlayFrames = nextFrames; overlays = nextTexts; overlayTextRects = nextTextRects;
            }
        }

        private void EnsureTextCapacity(int required)
        {
            if (segments.Length >= required) return;
            int size = GrowCapacity(segments.Length, required);
            RectTransform[] nextFrames = new RectTransform[size];
            TMP_Text[] nextTexts = new TMP_Text[size];
            RectTransform[] nextTextRects = new RectTransform[size];
            int[] nextColumns = new int[size];
            int[] nextFirstRows = new int[size];
            int[] nextLastRows = new int[size];
            System.Array.Copy(segmentFrames, nextFrames, segmentFrames.Length);
            System.Array.Copy(segments, nextTexts, segments.Length);
            System.Array.Copy(segmentTextRects, nextTextRects, segmentTextRects.Length);
            System.Array.Copy(segmentColumn, nextColumns, segmentColumn.Length);
            System.Array.Copy(segmentFirstRow, nextFirstRows, segmentFirstRow.Length);
            System.Array.Copy(segmentLastRow, nextLastRows, segmentLastRow.Length);
            segmentFrames = nextFrames; segments = nextTexts; segmentTextRects = nextTextRects;
            segmentColumn = nextColumns; segmentFirstRow = nextFirstRows; segmentLastRow = nextLastRows;
        }

        private Image GetCellRect()
        {
            if (cellPoolUnavailable || cellTemplate == null || tableArea == null)
            {
                if (!warnedCellPool) { warnedCellPool = true; Debug.LogWarning("UtsushiDisplay: cell pool unavailable. count=" + cellCreatedCount); }
                cellPoolUnavailable = true;
                return null;
            }
            EnsureImageCapacity(cellCount + 1);
            if (cells[cellCount] == null)
            {
                GameObject obj = Instantiate(cellTemplate.gameObject, tableArea);
                cells[cellCount] = obj.GetComponent<Image>();
                obj.transform.SetAsFirstSibling();
                grid.transform.SetAsFirstSibling();
                cellCreatedCount++;
            }
            return cells[cellCount++];
        }

        private int GetOverlayIndex(RectTransform[] poolFrames, TMP_Text[] poolTexts, RectTransform[] poolTextRects, TMP_Text template, int count, bool noWrap)
        {
            bool unavailable = noWrap ? overlayNoWrapPoolUnavailable : overlayPoolUnavailable;
            bool warned = noWrap ? warnedOverlayNoWrapPool : warnedOverlayPool;
            if (template == null || tableArea == null || unavailable)
            {
                if (!warned)
                {
                    Debug.LogWarning("UtsushiDisplay: " + (noWrap ? "overlayNoWrap" : "overlay") + " pool unavailable. count=" + (noWrap ? overlayNoWrapCreatedCount : overlayCreatedCount));
                    if (noWrap) warnedOverlayNoWrapPool = true; else warnedOverlayPool = true;
                }
                if (noWrap) overlayNoWrapPoolUnavailable = true; else overlayPoolUnavailable = true;
                return -1;
            }
            EnsureRectCapacity(count + 1, noWrap);
            poolFrames = noWrap ? overlayNoWrapFrames : overlayFrames;
            poolTexts = noWrap ? overlaysNoWrap : overlays;
            poolTextRects = noWrap ? overlayNoWrapTextRects : overlayTextRects;
            if (poolTexts[count] == null)
            {
                GameObject obj = Instantiate(template.GetComponent<RectTransform>().parent.gameObject, tableArea);
                poolFrames[count] = (RectTransform)obj.transform;
                poolTexts[count] = obj.GetComponentInChildren<TMP_Text>(true);
                if (poolTexts[count] == null)
                {
                    Destroy(obj);
                    if (noWrap) overlayNoWrapPoolUnavailable = true;
                    else overlayPoolUnavailable = true;
                    if (noWrap) { if (!warnedOverlayNoWrapPool) { warnedOverlayNoWrapPool = true; Debug.LogWarning("UtsushiDisplay: overlayNoWrap pool unavailable. count=" + overlayNoWrapCreatedCount); } }
                    else if (!warnedOverlayPool) { warnedOverlayPool = true; Debug.LogWarning("UtsushiDisplay: overlay pool unavailable. count=" + overlayCreatedCount); }
                    return -1;
                }
                poolTextRects[count] = poolTexts[count].GetComponent<RectTransform>();
                poolTextRects[count].anchorMin = Vector2.zero;
                poolTextRects[count].anchorMax = Vector2.one;
                poolTextRects[count].offsetMin = Vector2.zero;
                poolTextRects[count].offsetMax = Vector2.zero;
                poolTextRects[count].gameObject.SetActive(true);
                if (noWrap) overlayNoWrapCreatedCount++; else overlayCreatedCount++;
            }
            return count;
        }

        private void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f); rect.anchorMax = new Vector2(0f, 1f); rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
        }

        private DataList ListValue(DataDictionary dict, string key)
        {
            DataToken token;
            if (dict != null && dict.TryGetValue(key, out token) && token.TokenType == TokenType.DataList) return token.DataList;
            return null;
        }

        private DataToken Cell(DataList list, int row, int col)
        {
            if (list == null || row < 0 || row >= list.Count) return "";
            DataToken rowToken = list[row];
            if (rowToken.TokenType != TokenType.DataList || col < 0 || col >= rowToken.DataList.Count) return "";
            return rowToken.DataList[col];
        }

        private double Number(DataToken token)
        {
            if (token.TokenType == TokenType.Int) return token.Int;
            if (token.TokenType == TokenType.Long) return token.Long;
            if (token.TokenType == TokenType.Double) return token.Double;
            return 0d;
        }

        private string Value(DataToken token)
        {
            if (token.TokenType == TokenType.String) return token.String == null ? "" : token.String;
            if (token.TokenType == TokenType.Int) return token.Int.ToString();
            if (token.TokenType == TokenType.Long) return token.Long.ToString();
            if (token.TokenType == TokenType.Double) return token.Double.ToString();
            return "";
        }

        private int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private bool ValidColor(string value)
        {
            if (value == null || value.Length != 7 || value[0] != '#') return false;
            for (int i = 1; i < 7; i++) if (HexDigit(value[i]) < 0) return false;
            return true;
        }

        private Color ParseColor(string value)
        {
            return new Color((HexDigit(value[1]) * 16 + HexDigit(value[2])) / 255f, (HexDigit(value[3]) * 16 + HexDigit(value[4])) / 255f, (HexDigit(value[5]) * 16 + HexDigit(value[6])) / 255f, 1f);
        }

        private string Hex(Color color)
        {
            return "#" + Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f).ToString("X2") + Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f).ToString("X2");
        }

        private float Luminance(Color color) { return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b; }

        private float LinearChannel(float channel)
        {
            return channel <= 0.04045f ? channel / 12.92f : Mathf.Pow((channel + 0.055f) / 1.055f, 2.4f);
        }

        private float RelativeLuminance(Color color)
        {
            return 0.2126f * LinearChannel(color.r) + 0.7152f * LinearChannel(color.g) + 0.0722f * LinearChannel(color.b);
        }

        private float ContrastRatio(Color first, Color second)
        {
            float firstLuminance = RelativeLuminance(first);
            float secondLuminance = RelativeLuminance(second);
            float lighter = Mathf.Max(firstLuminance, secondLuminance);
            float darker = Mathf.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        private Color AutomaticChipTextColor(Color fill)
        {
            return Luminance(fill) >= 0.5f ? new Color(DefaultBodyRed, DefaultBodyGreen, DefaultBodyBlue, 1f) : Color.white;
        }

        private Color ChipTextColor(int i, Color fill)
        {
            if (workHasChipFg[i]) return workChipFg[i];
            if (workHasFg[i] && ContrastRatio(workFg[i], fill) >= ChipMinContrast) return workFg[i];
            return AutomaticChipTextColor(fill);
        }

        private float Median(float[] values, int count, float fallback)
        {
            if (count == 0) return fallback;
            for (int i = 1; i < count; i++)
            {
                float v = values[i]; int j = i - 1;
                while (j >= 0 && values[j] > v) { values[j + 1] = values[j]; j--; }
                values[j + 1] = v;
            }
            return count % 2 == 0 ? (values[count / 2 - 1] + values[count / 2]) * 0.5f : values[count / 2];
        }

        private string Clean(string value, bool keepLines)
        {
            if (keepLines) value = value.Replace("\r\n", "\n").Replace("\r", "\n");
            else value = value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            if (value.Length > CellGlyphCeiling)
            {
                int length = CellGlyphCeiling;
                char last = value[length - 1];
                if (last >= 0xD800 && last <= 0xDBFF) length--;
                value = value.Substring(0, length) + "…";
            }
            return value.Replace("<", "<noparse><</noparse>");
        }

        private string Styled(string raw, int bits, float pt, Color fg, bool hasFg, Color bg, bool hasBg, float basePt, bool header, bool keepLines, string bodyHex)
        {
            string text = Clean(raw, keepLines);
            if ((bits & 1) != 0 || header) text = "<b>" + text + "</b>";
            if ((bits & 2) != 0) text = "<i>" + text + "</i>";
            if ((bits & 4) != 0) text = "<u>" + text + "</u>";
            if ((bits & 8) != 0) text = "<s>" + text + "</s>";
            if (pt > 0f && basePt > 0f)
            {
                int percent = Mathf.RoundToInt(FontFactor(pt, basePt) * 100f);
                if (percent != 100) text = "<size=" + percent.ToString() + "%>" + text + "</size>";
            }
            Color chosen = bodyColor;
            if (hasFg)
            {
                chosen = fg;
                if (!hasBg && ((!lightTheme && Luminance(chosen) < 0.25f) || (lightTheme && Luminance(chosen) > 0.85f))) chosen = bodyColor;
            }
            else if (hasBg) chosen = Luminance(bg) >= 0.5f ? new Color(DefaultBodyRed, DefaultBodyGreen, DefaultBodyBlue, 1f) : Color.white;
            string colorHex = chosen.r == bodyColor.r && chosen.g == bodyColor.g && chosen.b == bodyColor.b ? bodyHex : Hex(chosen);
            return "<color=" + colorHex + ">" + text + "</color>";
        }

        private string StyledCell(int i, bool header, bool keepLines)
        {
            if (workChipKind[i] != 2) return Styled(workRaw[i], workBits[i], workPt[i], workFg[i], workHasFg[i], workBg[i], workHasBg[i], workBasePt, header, keepLines, workBodyHex);
            Color fill = workHasChipBg[i] ? workChipBg[i] : chipColor;
            Color fg = ChipTextColor(i, fill);
            return Styled(workRaw[i], workBits[i], workPt[i], fg, true, fill, true, workBasePt, header, keepLines, workBodyHex);
        }

        private float FontFactor(float pt, float basePt)
        {
            return Mathf.Clamp(pt / basePt, MinFontFactor, MaxFontFactor);
        }

        private Color OpaqueOverBoard(Color color)
        {
            return new Color(color.r * color.a + boardColor.r * (1f - color.a), color.g * color.a + boardColor.g * (1f - color.a), color.b * color.a + boardColor.b * (1f - color.a), 1f);
        }

        public void RenderNow()
        {
            rangeMissingShown = false;
            tableTooLarge = false;
            displayedSource = null; displayedRevision = -1;
            workRows = 0; workCols = 0;
            InitColors();
            workBodyHex = Hex(bodyColor);
            if (background != null) background.color = boardColor;
            if (titleText != null) titleText.color = bodyColor;
            if (statusText != null) statusText.color = bodyColor;
            InitPools();
            if (segmentFrames == null) return;
            for (int i = 0; i < segmentCreatedCount; i++) { if (segmentFrames[i] != null) segmentFrames[i].gameObject.SetActive(false); if (segments[i] != null) segments[i].text = ""; }
            for (int i = 0; i < cellCreatedCount; i++) if (cells[i] != null) cells[i].gameObject.SetActive(false);
            for (int i = 0; i < overlayCreatedCount; i++) { if (overlays[i] != null) overlays[i].text = ""; if (overlayFrames[i] != null) overlayFrames[i].gameObject.SetActive(false); }
            for (int i = 0; i < overlayNoWrapCreatedCount; i++) { if (overlaysNoWrap[i] != null) overlaysNoWrap[i].text = ""; if (overlayNoWrapFrames[i] != null) overlayNoWrapFrames[i].gameObject.SetActive(false); }
            for (int i = 0; i < chipCreatedCount; i++) { if (chips[i] != null) chips[i].gameObject.SetActive(false); if (arrows[i] != null) arrows[i].gameObject.SetActive(false); }
            if (titleText != null) titleText.text = "";
            cellCount = 0; overlayCount = 0; overlayNoWrapCount = 0; segmentCount = 0; chipCount = 0; chipPoolUnavailable = false; grid.gameObject.SetActive(false);
            if (source == null) return;
            if (tableArea.rect.width <= 0f || tableArea.rect.height <= 0f || textScale <= 0f || maxRowHeight <= 0f) return;
            if (!source.HasData()) { displayedSource = source; displayedRevision = source.GetRevision(); return; }
            string name = string.IsNullOrEmpty(rangeName) ? source.GetFirstRangeName() : rangeName;
            DataDictionary range = source.GetRange(name);
            if (range == null) { if (titleText != null) titleText.text = GetMissingRangeMessage(); rangeMissingShown = true; displayedSource = source; displayedRevision = source.GetRevision(); return; }
            DataToken token;
            if (titleText != null) titleText.text = showTitle && range.TryGetValue("title", out token) && token.TokenType == TokenType.String ? Clean(Value(token), false) : "";
            bool header = range.TryGetValue("header", out token) && token.TokenType == TokenType.Boolean && token.Boolean;
            DataList rows = ListValue(range, "rows"), widths = ListValue(range, "cols"), colw = ListValue(range, "colw"), rowh = ListValue(range, "rowh");
            DataList align = ListValue(range, "align"), ha = ListValue(range, "ha"), bold = ListValue(range, "bold"), styles = ListValue(range, "style");
            DataList sizes = ListValue(range, "size"), colors = ListValue(range, "color"), backgrounds = ListValue(range, "bg"), merges = ListValue(range, "merges"), wrap = ListValue(range, "wrap"), chip = ListValue(range, "chip");
            int nr = rows == null ? 0 : rows.Count;
            int nc = 0;
            for (int r = 0; r < nr; r++) if (rows[r].TokenType == TokenType.DataList) nc = Mathf.Max(nc, rows[r].DataList.Count);
            if (nr == 0 || nc == 0) { displayedSource = source; displayedRevision = source.GetRevision(); return; }
            workRows = nr;
            workCols = nc;
            workRaw = new string[nr * nc];
            workAscii = new int[nr * nc];
            workOther = new int[nr * nc];
            workBits = new int[nr * nc];
            workAlignment = new int[nr * nc];
            workPt = new float[nr * nc];
            workFg = new Color[nr * nc];
            workBg = new Color[nr * nc];
            workChipBg = new Color[nr * nc];
            workChipFg = new Color[nr * nc];
            workChipKind = new int[nr * nc];
            workHasFg = new bool[nr * nc];
            workHasBg = new bool[nr * nc];
            workHasChipBg = new bool[nr * nc];
            workHasChipFg = new bool[nr * nc];
            workWrapping = new bool[nr * nc];
            workCovered = new bool[nr * nc];
            workOverlayUsed = new bool[nr * nc];
            workSpillCandidates = new bool[nr * nc];
            workSpanRows = new int[nr * nc];
            workSpanCols = new int[nr * nc];
            workLongest = new int[nc];
            workHeader = header;
            CollectCellData(rows, sizes, align, ha, bold, styles, colors, backgrounds, wrap, chip);
            workMerges = merges;
            ApplyMerges();
            CalculateLayout(widths, colw, rowh, sizes);
            DrawCellsAndOverlays();
            DrawColumns();
            displayedSource = source; displayedRevision = source.GetRevision();
        }

        private void CollectCellData(DataList rows, DataList sizes, DataList align, DataList ha, DataList bold, DataList styles, DataList colors, DataList backgrounds, DataList wrap, DataList chip)
        {
            float[] points = new float[MaxFontSizeKinds];
            int[] counts = new int[MaxFontSizeKinds];
            int distinct = 0;
            for (int r = 0; r < workRows; r++)
            {
                for (int c = 0; c < workCols; c++)
                {
                    int i = r * workCols + c;
                    workRaw[i] = Value(Cell(rows, r, c));
                    int scanLength = workRaw[i].Length;
                    for (int j = 0; j < scanLength; j++)
                    {
                        if (workRaw[i][j] <= 127) workAscii[i]++;
                        else workOther[i]++;
                    }
                    workLongest[c] = Mathf.Max(workLongest[c], workAscii[i] + 2 * workOther[i]);
                    workPt[i] = (float)Number(Cell(sizes, r, c));
                    if (workPt[i] > 0f)
                    {
                        int j = 0;
                        while (j < distinct && points[j] != workPt[i]) j++;
                        if (j < MaxFontSizeKinds)
                        {
                            if (j == distinct) points[distinct++] = workPt[i];
                            counts[j]++;
                        }
                    }
                    workBits[i] = styles != null ? (int)Number(Cell(styles, r, c)) : (Number(Cell(bold, r, c)) == 1d ? 1 : 0);
                    string f = Value(Cell(colors, r, c));
                    string b = Value(Cell(backgrounds, r, c));
                    workHasFg[i] = ValidColor(f);
                    workHasBg[i] = ValidColor(b);
                    if (workHasFg[i]) workFg[i] = ParseColor(f);
                    if (workHasBg[i]) workBg[i] = ParseColor(b);
                    string a = Value(Cell(ha, r, c));
                    if (a != "l" && a != "c" && a != "r") a = align != null && c < align.Count ? Value(align[c]) : "l";
                    workAlignment[i] = a == "c" ? 1 : (a == "r" ? 2 : 0);
                    workWrapping[i] = Number(Cell(wrap, r, c)) == 1d;
                    string chipValue = Value(Cell(chip, r, c));
                    if (chipValue == "a") workChipKind[i] = 1;
                    else if (chipValue.Length > 0 && chipValue[0] == 'c')
                    {
                        workChipKind[i] = 2;
                        int firstSeparator = chipValue.IndexOf("|");
                        if (firstSeparator >= 0)
                        {
                            int secondSeparator = chipValue.IndexOf("|", firstSeparator + 1);
                            string chipBg = secondSeparator >= 0 ? chipValue.Substring(firstSeparator + 1, secondSeparator - firstSeparator - 1) : chipValue.Substring(firstSeparator + 1);
                            workHasChipBg[i] = ValidColor(chipBg);
                            if (workHasChipBg[i]) workChipBg[i] = ParseColor(chipBg);
                            if (secondSeparator >= 0)
                            {
                                string chipFg = chipValue.Substring(secondSeparator + 1);
                                workHasChipFg[i] = ValidColor(chipFg);
                                if (workHasChipFg[i]) workChipFg[i] = ParseColor(chipFg);
                            }
                        }
                    }
                }
            }
            int best = 0;
            workBasePt = 0f;
            for (int i = 0; i < distinct; i++)
            {
                if (counts[i] > best)
                {
                    best = counts[i];
                    workBasePt = points[i];
                }
            }
        }

        private float EmWidth(int i)
        {
            float size = workPt[i] > 0f && workBasePt > 0f ? FontFactor(workPt[i], workBasePt) : 1f;
            bool bold = (workBits[i] & 1) != 0 || (workHeader && i / workCols == 0);
            return ((workAscii[i] * FitFontWidthFactor + workOther[i]) * size * (bold ? BoldFactor : 1f) + (workChipKind[i] == 2 ? ChipExtraEm : 0f)) * SafetyMargin;
        }

        private void CalculateLayout(DataList widths, DataList colw, DataList rowh, DataList sizes)
        {
            workXs = new float[workCols];
            workWs = new float[workCols];
            workYs = new float[workRows];
            workHs = new float[workRows];
            float[] sorted = new float[Mathf.Max(workRows, workCols)];
            int valid = 0;
            for (int r = 0; r < workRows; r++)
            {
                float v = rowh == null ? 1f : (r < rowh.Count ? (float)Number(rowh[r]) : 0f);
                workHs[r] = v;
                if (v > 0f) sorted[valid++] = v;
            }
            float fallback = Median(sorted, valid, rowh == null ? 1f : 21f);
            float total = 0f;
            for (int r = 0; r < workRows; r++)
            {
                if (workHs[r] <= 0f) workHs[r] = fallback;
                sorted[r] = workHs[r];
                total += workHs[r];
            }
            float median = Median(sorted, workRows, fallback);
            float scale = Mathf.Min(tableArea.rect.height / total, maxRowHeight / median);
            float cumulative = 0f;
            for (int r = 0; r < workRows; r++)
            {
                float exact = workHs[r] * scale;
                if (exact < MinReadableRowHeight) tableTooLarge = true;
                workYs[r] = r == 0 ? 0f : Mathf.Max(Mathf.Round(cumulative), workYs[r - 1] + workHs[r - 1]);
                cumulative += exact;
                workHs[r] = Mathf.Max(1f, Mathf.Round(cumulative) - workYs[r]);
                sorted[r] = workHs[r];
            }
            workHeightAll = workYs[workRows - 1] + workHs[workRows - 1];
            float medianHeight = Median(sorted, workRows, 0f);
            float preferred = sizes != null && rowh != null && workBasePt > 0f ? workBasePt * (4f / 3f) * scale * Mathf.Clamp(textScale, 0.5f, 3f) : medianHeight * DefaultCellHeightFactor * Mathf.Clamp(textScale, 0.5f, 3f) / DefaultTextScale;
            float rowLimit = RowFitRatio * medianHeight;
            float upper = Mathf.Min(preferred, rowLimit);

            float[] needEm = new float[workCols];
            bool[] emptyColumn = new bool[workCols];
            workSpillCandidates = new bool[workRows * workCols];
            int nonEmpty = 0;
            float sumNeedEm = 0f;
            for (int c = 0; c < workCols; c++)
            {
                float need = 0f;
                for (int r = 0; r < workRows; r++)
                {
                    int i = r * workCols + c;
                    if (workCovered[i] || workSpanRows[i] > 0 || workChipKind[i] > 0 || workWrapping[i] || workRaw[i].Contains("\n") || workRaw[i].Contains("\r")) continue;
                    if (workAlignment[i] == 0 && c + 1 < workCols)
                    {
                        int end = c + 1;
                        while (end < workCols)
                        {
                            int right = r * workCols + end;
                            if (workRaw[right] != "" || workHasBg[right] || workCovered[right] || workSpanRows[right] > 0 || workChipKind[right] != 0) break;
                            end++;
                        }
                        if (end == workCols)
                        {
                            workSpillCandidates[i] = true;
                            continue;
                        }
                    }
                    need = Mathf.Max(need, EmWidth(i));
                }
                if (need <= 0f)
                {
                    emptyColumn[c] = true;
                    needEm[c] = SpacerEm;
                }
                else
                {
                    needEm[c] = need;
                    nonEmpty++;
                    sumNeedEm += need;
                }
            }

            bool autoLayout = autoFitColumns && widths == null;
            bool manualWidths = widths != null;
            DataList weightList = manualWidths ? widths : colw;
            bool useColw = !manualWidths && colw != null;
            float gap = autoLayout ? (showGrid ? 0f : columnGap) : (showGrid || useColw ? 0f : columnGap);
            float tableWidth = tableArea.rect.width;
            float available = Mathf.Max(0f, tableWidth - gap * (workCols - 1));
            float horizontalLimit = preferred;
            if (autoLayout)
            {
                horizontalLimit = sumNeedEm > 0f ? Mathf.Max(0f, (available - nonEmpty * 2f * Padding) / sumNeedEm) : preferred;
                float tableConstraint = tableWidth - gap * (workCols - 1);
                for (int r = 0; r < workRows; r++)
                {
                    for (int c = 0; c < workCols; c++)
                    {
                        int i = r * workCols + c;
                        if (!workSpillCandidates[i]) continue;
                        float leftNeed = EmWidth(i);
                        int leftNonEmpty = 0;
                        for (int k = 0; k < c; k++)
                        {
                            leftNeed += needEm[k];
                            if (!emptyColumn[k]) leftNonEmpty++;
                        }
                        float widthLimit = tableConstraint - 2f * Padding * (leftNonEmpty + 1);
                        if (leftNeed > 0f) horizontalLimit = Mathf.Min(horizontalLimit, Mathf.Max(0f, widthLimit / leftNeed));
                    }
                }
            }
            else if (manualWidths)
            {
                float fw = MedianFromList(widths, 1f);
                float weightTotal = 0f;
                for (int c = 0; c < workCols; c++)
                {
                    float w = c < widths.Count ? (float)Number(widths[c]) : 0f;
                    if (w <= 0f) w = fw;
                    weightTotal += w;
                }
                for (int c = 0; c < workCols; c++)
                {
                    float w = c < widths.Count ? (float)Number(widths[c]) : 0f;
                    if (w <= 0f) w = fw;
                    float colWidth = weightTotal > 0f ? available * w / weightTotal : available / workCols;
                    if (!emptyColumn[c] && needEm[c] > 0f) horizontalLimit = Mathf.Min(horizontalLimit, Mathf.Max(0f, colWidth - 2f * Padding) / needEm[c]);
                }
            }
            float minimum = MinFontRatio * Mathf.Min(preferred, rowLimit);
            workBasePx = Mathf.Max(minimum, Mathf.Min(upper, horizontalLimit));

            float[] weights = new float[workCols];
            float weightFallback = 1f;
            if (weightList != null)
            {
                valid = 0;
                for (int c = 0; c < workCols; c++)
                {
                    float v = c < weightList.Count ? (float)Number(weightList[c]) : 0f;
                    if (v > 0f) sorted[valid++] = v;
                }
                weightFallback = Median(sorted, valid, useColw ? 100f : 1f);
            }
            total = 0f;
            for (int c = 0; c < workCols; c++)
            {
                float v;
                if (autoLayout)
                {
                    v = colw != null ? (c < colw.Count ? (float)Number(colw[c]) : 0f) : needEm[c];
                }
                else
                {
                    v = weightList == null ? Mathf.Clamp(workLongest[c], MinMeasuredColumnWidth, MaxMeasuredColumnWidth) : (c < weightList.Count ? (float)Number(weightList[c]) : 0f);
                }
                if (v <= 0f) v = weightFallback;
                weights[c] = v;
                total += v;
            }
            if (autoLayout)
            {
                float[] minimumWidths = new float[workCols];
                float sumMinimum = 0f;
                for (int c = 0; c < workCols; c++)
                {
                    minimumWidths[c] = needEm[c] * workBasePx + (emptyColumn[c] ? 0f : 2f * Padding);
                    sumMinimum += minimumWidths[c];
                }
                float extra = available - sumMinimum;
                cumulative = 0f;
                for (int c = 0; c < workCols; c++)
                {
                    float width;
                    if (extra >= 0f) width = minimumWidths[c] + extra * weights[c] / total;
                    else width = sumMinimum > 0f ? minimumWidths[c] * available / sumMinimum : available / workCols;
                    float start = Mathf.Round(cumulative + c * gap);
                    cumulative += width;
                    float end = Mathf.Round(cumulative + c * gap);
                    workXs[c] = start;
                    workWs[c] = end - start;
                }
            }
            else
            {
                cumulative = 0f;
                for (int c = 0; c < workCols; c++)
                {
                    float start = Mathf.Round(cumulative + c * gap);
                    cumulative += available * weights[c] / total;
                    float end = Mathf.Round(cumulative + c * gap);
                    workXs[c] = start;
                    workWs[c] = end - start;
                }
            }
        }

        internal bool IsKnownOverflow(int row, int col)
        {
            if (row < 0 || row >= workRows || col < 0 || col >= workCols) return false;
            if (workRaw == null || workCovered == null || workOverlayUsed == null || workXs == null || workWs == null) return false;
            int i = row * workCols + col;
            if (workCovered[i] || workRaw[i] == "") return false;
            if (workWrapping[i] || workRaw[i].Contains("\n") || workRaw[i].Contains("\r")) return false;
            if (workOverlayUsed[i]) return workSpillCandidates != null && workSpillCandidates[i] && EmWidth(i) * workBasePx > tableArea.rect.width - workXs[col] - 2f * Padding;
            return EmWidth(i) * workBasePx > workWs[col] - 2f * Padding;
        }

        private float MedianFromList(DataList list, float fallback)
        {
            if (list == null) return fallback;
            float[] values = new float[list.Count];
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                float value = (float)Number(list[i]);
                if (value > 0f) values[count++] = value;
            }
            return Median(values, count, fallback);
        }
        private void ApplyMerges()
        {
            if (workMerges != null) for (int i = 0; i < workMerges.Count; i++)
            {
                if (workMerges[i].TokenType != TokenType.DataList || workMerges[i].DataList.Count < 4) continue;
                DataList m = workMerges[i].DataList;
                int r = (int)Number(m[0]);
                int c = (int)Number(m[1]);
                int rh = (int)Number(m[2]);
                int cw = (int)Number(m[3]);
                if (r < 0 || c < 0 || r >= workRows || c >= workCols || rh < 1 || cw < 1) continue;
                rh = Mathf.Min(rh, workRows - r);
                cw = Mathf.Min(cw, workCols - c);
                if (rh == 1 && cw == 1) continue;
                bool overlaps = false;
                for (int rr = r; rr < r + rh; rr++)
                {
                    for (int cc = c; cc < c + cw; cc++)
                    {
                        if (workCovered[rr * workCols + cc] || workSpanRows[rr * workCols + cc] > 0) overlaps = true;
                    }
                }
                if (overlaps) continue;
                workSpanRows[r * workCols + c] = rh;
                workSpanCols[r * workCols + c] = cw;
                for (int rr = r; rr < r + rh; rr++)
                {
                    for (int cc = c; cc < c + cw; cc++)
                    {
                        if (rr != r || cc != c) workCovered[rr * workCols + cc] = true;
                    }
                }
            }
        }

        private int GetChipPoolIndex()
        {
            if (chipTemplate == null || arrowTemplate == null || chipContainer == null || arrowContainer == null || chipPoolUnavailable)
            {
                if (!warnedChipPool)
                {
                    warnedChipPool = true;
                    Debug.LogWarning("UtsushiDisplay: chip pool unavailable. count=" + chipCreatedCount + ", check chip/arrow templates.");
                }
                chipPoolUnavailable = true;
                return -1;
            }
            EnsureChipCapacity(chipCount + 1);
            int index = chipCount;
            if (chips[index] == null)
            {
                GameObject chipObject = Instantiate(chipTemplate.gameObject, chipContainer);
                chips[index] = chipObject.GetComponent<Image>();
                GameObject arrowObject = Instantiate(arrowTemplate.gameObject, arrowContainer);
                arrows[index] = arrowObject.GetComponent<Image>();
                if (chips[index] == null || arrows[index] == null)
                {
                    chipPoolUnavailable = true;
                    if (!warnedChipPool)
                    {
                        warnedChipPool = true;
                        Debug.LogWarning("UtsushiDisplay: chip pool unavailable. count=" + chipCreatedCount + ", template has no Image.");
                    }
                    return -1;
                }
                chipCreatedCount++;
            }
            chipCount++;
            return index;
        }

        private void DrawChip(int i, float x, float y, float width, float height)
        {
            int kind = workChipKind[i];
            if (kind == 0) return;
            float font = workBasePx * (workPt[i] > 0f && workBasePt > 0f ? FontFactor(workPt[i], workBasePt) : 1f);
            float arrowWidth = font * ArrowWidthEm;
            float arrowHeight = font * ArrowHeightEm;
            float arrowX = x + width - Padding - arrowWidth;
            float arrowY = y + (height - arrowHeight) * 0.5f;
            Color fill = chipColor;
            float chipX = x + ChipMargin;
            float chipY = y;
            float chipWidth = width - 2f * ChipMargin;
            float chipHeight = Mathf.Min(height - 2f * ChipMargin, Mathf.Round(font * ChipHeightEm));
            if (kind == 2)
            {
                if (chipWidth < 4f || chipHeight < 4f) return;
                fill = workHasChipBg[i] ? workChipBg[i] : (workHasAutoChipColor != null && workHasAutoChipColor[i] ? workAutoChipColors[i] : chipColor);
                chipY = y + (height - chipHeight) * 0.5f;
                arrowX = chipX + chipWidth - font * ChipPadEm - arrowWidth;
            }
            int pool = GetChipPoolIndex();
            if (pool < 0) return;
            if (kind == 2)
            {
                float scale = chipHeight / ChipSpriteHeight;
                Place((RectTransform)chips[pool].transform, chipX, chipY, chipWidth / scale, ChipSpriteHeight);
                chips[pool].transform.localScale = new Vector3(scale, scale, 1f);
                chips[pool].color = fill;
                chips[pool].gameObject.SetActive(true);
                Color arrowColor = ChipTextColor(i, fill);
                arrows[pool].color = arrowColor;
            }
            else
            {
                Color backgroundColor = workHasBg[i] ? workBg[i] : boardColor;
                if (workHasChipFg[i]) arrows[pool].color = workChipFg[i];
                else if (workHasFg[i] && ContrastRatio(workFg[i], backgroundColor) >= ChipMinContrast) arrows[pool].color = workFg[i];
                else arrows[pool].color = chipArrowColor;
            }
            Place((RectTransform)arrows[pool].transform, arrowX, arrowY, arrowWidth, arrowHeight);
            arrows[pool].transform.localScale = Vector3.one;
            arrows[pool].gameObject.SetActive(true);
        }

        private bool IsPaletteBlankCharacter(char value)
        {
            return value == '-' || value == '\u2010' || value == '\u2011' || value == '\u2013' || value == '\u2014' || value == '\u2015' || value == '\u30fc' || value == '\uFF0D' || value == '\u30fb' || value == '/' || value == '\uFF0F' || IsPaletteWhitespace(value);
        }

        private bool IsPaletteWhitespace(char value)
        {
            return value == ' ' || (value >= '\t' && value <= '\r') || value == '\u0085' || value == '\u00A0' || value == '\u1680' || (value >= '\u2000' && value <= '\u200A') || value == '\u2028' || value == '\u2029' || value == '\u202F' || value == '\u205F' || value == '\u3000';
        }

        private string NormalizeChipPaletteValue(string value)
        {
            int start = 0;
            int end = value.Length - 1;
            while (start <= end && IsPaletteWhitespace(value[start])) start++;
            while (end >= start && IsPaletteWhitespace(value[end])) end--;
            if (end < start) return "";
            return value.Substring(start, end - start + 1).Replace("\u3000", "");
        }

        private bool IsPaletteBlankValue(string value)
        {
            if (value.Length == 0) return true;
            for (int i = 0; i < value.Length; i++)
                if (!IsPaletteBlankCharacter(value[i])) return false;
            return true;
        }

        private void BuildChipPaletteAssignments()
        {
            workAutoChipColors = new Color[workRaw.Length];
            workHasAutoChipColor = new bool[workRaw.Length];
            if (!chipAutoPalette || chipPalette == null || chipPalette.Length == 0) return;
            string[] names = new string[workRaw.Length];
            int[] paletteIndices = new int[workRaw.Length];
            int nameCount = 0;
            for (int r = 0; r < workRows; r++)
            {
                for (int c = 0; c < workCols; c++)
                {
                    int i = r * workCols + c;
                    if (workChipKind[i] != 2 || workHasChipBg[i]) continue;
                    string name = NormalizeChipPaletteValue(workRaw[i]);
                    if (IsPaletteBlankValue(name)) continue;
                    int n = 0;
                    while (n < nameCount && names[n] != name) n++;
                    if (n == nameCount)
                    {
                        names[nameCount] = name;
                        paletteIndices[nameCount] = nameCount % chipPalette.Length;
                        nameCount++;
                    }
                    workAutoChipColors[i] = chipPalette[paletteIndices[n]];
                    workHasAutoChipColor[i] = true;
                }
            }
        }

        private void DrawCellsAndOverlays()
        {
            BuildChipPaletteAssignments();
            if (showGrid)
            {
                grid.color = gridColor;
                Place((RectTransform)grid.transform, 0f, 0f, tableArea.rect.width, workHeightAll);
                grid.gameObject.SetActive(true);
            }
            for (int r = 0; r < workRows; r++)
            {
                for (int c = 0; c < workCols; c++)
                {
                    int i = r * workCols + c;
                    if (workCovered[i]) continue;
                    int rh = Mathf.Max(1, workSpanRows[i]);
                    int cw = Mathf.Max(1, workSpanCols[i]);
                    float width = workXs[c + cw - 1] + workWs[c + cw - 1] - workXs[c];
                    float height = workYs[r + rh - 1] + workHs[r + rh - 1] - workYs[r];
                    bool isHeader = workHeader && r == 0;
                    bool isZebra = zebra && !isHeader && r % 2 == 1;
                    if (workHasBg[i] || isHeader || isZebra || showGrid)
                    {
                        Image rect = GetCellRect();
                        if (rect != null)
                        {
                            Color fill = workHasBg[i] ? workBg[i] : (isHeader ? headerBackgroundColor : (isZebra ? zebraColor : boardColor));
                            if (showGrid && !workHasBg[i] && (isHeader || isZebra)) fill = OpaqueOverBoard(fill);
                            rect.color = fill;
                            float inset = showGrid ? Mathf.Max(0f, gridWidth) : 0f;
                            Place((RectTransform)rect.transform, workXs[c] + inset, workYs[r] + inset, width - inset * 2f, height - inset * 2f);
                            rect.gameObject.SetActive(true);
                        }
                    }
                    float cellInset = showGrid ? Mathf.Max(0f, gridWidth) : 0f;
                    DrawChip(i, workXs[c] + cellInset, workYs[r] + cellInset, width - 2f * cellInset, height - 2f * cellInset);
                    int kind = workSpanRows[i] > 0 ? 1 : (workWrapping[i] || workRaw[i].Contains("\n") || workRaw[i].Contains("\r") ? 2 : 0);
                    if (workRaw[i].Length == 0) kind = 0;
                    if (kind == 0 && workChipKind[i] == 0 && workAlignment[i] == 0 && workRaw[i].Length > 0)
                    {
                        if (EmWidth(i) * workBasePx > workWs[c] - 2f * Padding)
                        {
                            int end = c + 1;
                            while (end < workCols && workRaw[r * workCols + end] == "" && !workHasBg[r * workCols + end] && !workCovered[r * workCols + end] && workSpanRows[r * workCols + end] == 0 && workChipKind[r * workCols + end] == 0) end++;
                            if (end > c + 1)
                            {
                                kind = 3;
                                width = workXs[end - 1] + workWs[end - 1] - workXs[c];
                            }
                        }
                    }
                    if (kind == 0) continue;
                    bool noWrap = kind == 3 || (kind == 1 && !workWrapping[i]);
                    int overlayIndex = noWrap
                        ? GetOverlayIndex(overlayNoWrapFrames, overlaysNoWrap, overlayNoWrapTextRects, overlayNoWrapTemplate, overlayNoWrapCount, true)
                        : GetOverlayIndex(overlayFrames, overlays, overlayTextRects, overlayTemplate, overlayCount, false);
                    if (overlayIndex < 0) continue;
                    TMP_Text overlay = noWrap ? overlaysNoWrap[overlayIndex] : overlays[overlayIndex];
                    RectTransform frame = noWrap ? overlayNoWrapFrames[overlayIndex] : overlayFrames[overlayIndex];
                    if (noWrap) overlayNoWrapCount++;
                    else overlayCount++;
                    Place(frame, workXs[c] + Padding, workYs[r], width - 2f * Padding, height);
                    overlay.color = bodyColor;
                    overlay.fontSize = workBasePx;
                    overlay.alignment = workAlignment[i] == 1 ? TextAlignmentOptions.Center : (workAlignment[i] == 2 ? TextAlignmentOptions.Right : TextAlignmentOptions.Left);
                    overlay.text = StyledCell(i, isHeader, true);
                    frame.gameObject.SetActive(true);
                    workOverlayUsed[i] = true;
                }
            }
        }

        private void DrawColumns()
        {
            for (int c = 0; c < workCols; c++)
            {
                int first = 0;
                while (first < workRows)
                {
                    int last = first;
                    int glyphs = 0;
                    while (last < workRows)
                    {
                        int rowGlyphs = EstimateCellGlyphs(workRaw[last * workCols + c]);
                        if (last > first && glyphs + rowGlyphs > SegmentGlyphBudget) break;
                        glyphs += rowGlyphs;
                        last++;
                        if (glyphs > SegmentGlyphBudget) break;
                    }
                    int index = GetSegmentPoolIndex();
                    if (index < 0) return;
                    segmentColumn[index] = c;
                    segmentFirstRow[index] = first;
                    segmentLastRow[index] = last - 1;
                    float segmentHeight = 0f;
                    for (int r = first; r < last; r++) segmentHeight += workHs[r];
                    Place(segmentFrames[index], workXs[c], workYs[first], workWs[c], segmentHeight);
                    TMP_Text column = segments[index];
                    if (column != null)
                    {
                        column.color = bodyColor;
                        column.fontSize = workBasePx;
                        column.alignment = TextAlignmentOptions.TopLeft;
                        string content = "";
                        float dummyOffset = workHs[first] * 0.5f - VerticalOffsetFactor * workBasePx;
                        bool dummyShifted = Mathf.Abs(dummyOffset) >= 1f;
                        content = "<line-height=0>";
                        if (dummyShifted) content += "<voffset=" + Mathf.RoundToInt(-dummyOffset).ToString() + ">";
                        content += "<alpha=#00>M<alpha=#FF>";
                        if (dummyShifted) content += "</voffset>";
                        content += "\n";
                        bool sameHeight = true;
                        for (int r = first + 1; r < last; r++) if (workHs[r] != workHs[first]) sameHeight = false;
                        for (int r = first; r < last; r++)
                        {
                            int i = r * workCols + c;
                            if (r > first) content += "\n";
                            if (!sameHeight || r == first) content += "<line-height=" + ((int)workHs[r]).ToString() + ">";
                            content += "<align=" + (workAlignment[i] == 1 ? "center" : (workAlignment[i] == 2 ? "right" : "left")) + ">";
                            float font = workBasePx * (workPt[i] > 0f && workBasePt > 0f ? FontFactor(workPt[i], workBasePt) : 1f);
                            float offset = workHs[r] * 0.5f - VerticalOffsetFactor * workBasePx + GlyphCenterFactor * (font - workBasePx);
                            bool shifted = Mathf.Abs(offset) >= 1f;
                            if (shifted) content += "<voffset=" + Mathf.RoundToInt(-offset).ToString() + ">";
                            if (!workCovered[i] && !workOverlayUsed[i]) content += StyledCell(i, workHeader && r == 0, false);
                            if (shifted) content += "</voffset>";
                        }
                        column.text = content;
                        segmentFrames[index].gameObject.SetActive(true);
                    }
                    first = last;
                }
            }
        }

        private int EstimateCellGlyphs(string value)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char valueChar = value[i];
                bool whitespace = valueChar == ' ' || (valueChar >= '\t' && valueChar <= '\r') || valueChar == '\u0085' || valueChar == '\u00A0' || valueChar == '\u1680' || (valueChar >= '\u2000' && valueChar <= '\u200A') || valueChar == '\u2028' || valueChar == '\u2029' || valueChar == '\u202F' || valueChar == '\u205F' || valueChar == '\u3000';
                if (!whitespace && !(valueChar >= 0xDC00 && valueChar <= 0xDFFF)) count++;
            }
            return count;
        }

        private int GetSegmentPoolIndex()
        {
            if (segmentPoolUnavailable || columnTemplate == null || tableArea == null)
            {
                if (!warnedSegmentPool) { warnedSegmentPool = true; Debug.LogWarning("UtsushiDisplay: segment pool unavailable. count=" + segmentCreatedCount); }
                segmentPoolUnavailable = true;
                return -1;
            }
            EnsureTextCapacity(segmentCount + 1);
            int index = segmentCount++;
            if (segments[index] == null)
            {
                GameObject frame = Instantiate(columnTemplate.GetComponent<RectTransform>().parent.gameObject, tableArea);
                segmentFrames[index] = (RectTransform)frame.transform;
                segments[index] = frame.GetComponentInChildren<TMP_Text>(true);
                if (segments[index] == null)
                {
                    Destroy(frame);
                    segmentPoolUnavailable = true;
                    if (!warnedSegmentPool) { warnedSegmentPool = true; Debug.LogWarning("UtsushiDisplay: segment pool unavailable. count=" + segmentCreatedCount); }
                    return -1;
                }
                segmentTextRects[index] = segments[index].GetComponent<RectTransform>();
                segmentTextRects[index].anchorMin = Vector2.zero;
                segmentTextRects[index].anchorMax = Vector2.one;
                segmentTextRects[index].offsetMin = new Vector2(Padding, 0f);
                segmentTextRects[index].offsetMax = new Vector2(-Padding, 0f);
                segmentTextRects[index].gameObject.SetActive(true);
                frame.SetActive(false);
                segmentCreatedCount++;
            }
            return index;
        }
    }
}
