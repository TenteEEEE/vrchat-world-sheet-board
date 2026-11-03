using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace SheetBoard
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SheetBoardDisplay : UdonSharpBehaviour
    {
        [Header("Settings")]
        public SheetBoardSource source;
        public string rangeName = "";
        public int maxRows = 40;
        public int maxCols = 12;
        public bool showTitle = true;
        public bool zebra = false;
        public bool lightTheme = true;
        public bool showGrid = true;
        public float gridWidth = 1f;
        public int maxOverlays = 24;
        public float nearCheckInterval = 1f;
        public float maxRowHeight = 64f;
        public float textScale = 1.4f;
        public float columnGap = 12f;
        [Header("Colours")]
        public Color boardColor, bodyColor, gridColor, headerBackgroundColor, zebraColor, normalColor, staleColor, errorColor, busyColor;
        [Header("Texts")]
        public string rangeMissingText = "範囲「{name}」が見つかりません";
        public string sourceMissingText = "Sourceが設定されていません";
        public string refreshIdleText = "更新";
        public string refreshBusyText = "更新中…";

        [Header("Internal references (installer-wired)")]
        public Image background;
        public TMP_Text titleText, statusText;
        public RectTransform tableArea;
        public TMP_Text columnTemplate, overlayTemplate, overlayNoWrapTemplate;
        public Image cellTemplate, statusDot;
        public Button refreshButton;
        public TMP_Text refreshLabel;

        private const float MinInterval = 0.25f, MaxInterval = 1.5f, Padding = 6f, DefaultTextScale = 1.4f;
        private const int MaxCellChars = 200;
        private const float MinFontFactor = 0.6f, MaxFontFactor = 1.6f;
        private const int MinMeasuredColumnWidth = 2, MaxMeasuredColumnWidth = 40;
        private const float FitFontWidthFactor = 0.55f, DefaultCellHeightFactor = 0.62f, VerticalOffsetFactor = 0.78f;
        private const int MaxFontSizeKinds = 16;
        private const float DefaultBodyRed = 0x20 / 255f, DefaultBodyGreen = 0x21 / 255f, DefaultBodyBlue = 0x24 / 255f;
        private float nextWatchAt;
        private bool watchStarted;
        private bool warnedTemplates;
        private bool busyLampOn;
        private SheetBoardSource displayedSource;
        private bool colorsReady;
        private RectTransform[] frames;
        private TMP_Text[] columns;
        private Image[] cells;
        private Image grid;
        private RectTransform[] overlayFrames;
        private TMP_Text[] overlays;
        private RectTransform[] overlayNoWrapFrames;
        private TMP_Text[] overlaysNoWrap;
        private int cellCount;
        private int overlayCount;
        private int overlayNoWrapCount;
        private int displayedRevision = -1;
        private RectTransform[] columnTextRects;
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
        private Color[] workFg, workBg;
        private bool[] workHasFg, workHasBg, workWrapping, workCovered, workOverlayUsed;
        private bool overlayPoolUnavailable;
        private bool overlayNoWrapPoolUnavailable;

        private void InitColors()
        {
            if (colorsReady) return;
            colorsReady = true;
            if (boardColor.a <= 0f) boardColor = lightTheme ? Color.white : new Color(0x12 / 255f, 0x16 / 255f, 0x1C / 255f, 1f);
            if (bodyColor.a <= 0f) bodyColor = lightTheme ? new Color(DefaultBodyRed, DefaultBodyGreen, DefaultBodyBlue, 1f) : new Color(0xE8 / 255f, 0xED / 255f, 0xF2 / 255f, 1f);
            if (gridColor.a <= 0f) gridColor = lightTheme ? new Color(0xE2 / 255f, 0xE3 / 255f, 0xE3 / 255f, 1f) : new Color(1f, 1f, 1f, 0.08f);
            if (headerBackgroundColor.a <= 0f) headerBackgroundColor = new Color(0x2E / 255f, 0xD3 / 255f, 0xC6 / 255f, 0.18f);
            if (zebraColor.a <= 0f) zebraColor = lightTheme ? new Color(0f, 0f, 0f, 0.025f) : new Color(1f, 1f, 1f, 0.015f);
            if (normalColor.a <= 0f) normalColor = new Color(0x4C / 255f, 0xAF / 255f, 0x50 / 255f, 1f);
            if (staleColor.a <= 0f) staleColor = new Color(1f, 0xB3 / 255f, 0f, 1f);
            if (errorColor.a <= 0f) errorColor = new Color(0xE5 / 255f, 0x39 / 255f, 0x35 / 255f, 1f);
            if (busyColor.a <= 0f) busyColor = new Color32(0x21, 0x96, 0xF3, 0xFF);
        }

        private void OnEnable()
        {
            if (refreshButton == null) refreshButton = GetComponentInChildren<Button>();
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
            if (source == null) SetStatus(sourceMissingText, errorColor);
            else if (access != 0)
            {
                if (displayedSource != source || displayedRevision != source.GetRevision()) RenderNow();
                SetStatus(access == 2 ? source.permissionDeniedText : source.permissionWaitingText, errorColor);
            }
            else
            {
                VRCPlayerApi player = Networking.LocalPlayer;
                float interval = Mathf.Clamp(nearCheckInterval, MinInterval, MaxInterval);
                if (source.nearRadius > 0f && Utilities.IsValid(player) && Vector3.Distance(player.GetPosition(), transform.position) <= source.nearRadius) source._ReportNear(interval);
                if (displayedSource != source || displayedRevision != source.GetRevision()) RenderNow();
                int level = source.GetStatusLevel();
                SetStatus(source.GetStatusText(), level == 0 ? normalColor : (level == 1 ? staleColor : errorColor));
            }
            if (busy)
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
            if (refreshButton != null) refreshButton.interactable = source != null && access == 0 && source.GetRefreshWaitSeconds() == 0f;
            if (refreshLabel != null)
            {
                string label = busy ? refreshBusyText : refreshIdleText;
                if (refreshLabel.text != label) refreshLabel.text = label;
            }
        }

        private void InitPools()
        {
            if (!warnedTemplates && (tableArea == null || columnTemplate == null || cellTemplate == null || overlayTemplate == null || overlayNoWrapTemplate == null))
            {
                warnedTemplates = true;
                Debug.LogWarning("SheetBoardDisplay: missing table area or template reference.");
            }
            if (frames != null || tableArea == null || columnTemplate == null || cellTemplate == null) return;
            frames = new RectTransform[Mathf.Max(0, maxCols)];
            columns = new TMP_Text[frames.Length];
            columnTextRects = new RectTransform[frames.Length];
            cells = new Image[Mathf.Max(0, maxRows) * frames.Length];
            overlayFrames = new RectTransform[Mathf.Max(0, maxOverlays)];
            overlays = new TMP_Text[overlayFrames.Length];
            overlayTextRects = new RectTransform[overlayFrames.Length];
            overlayNoWrapFrames = new RectTransform[Mathf.Max(0, maxOverlays)];
            overlaysNoWrap = new TMP_Text[overlayNoWrapFrames.Length];
            overlayNoWrapTextRects = new RectTransform[overlayNoWrapFrames.Length];
            GameObject underlay = Instantiate(cellTemplate.gameObject, tableArea);
            grid = underlay.GetComponent<Image>();
            underlay.transform.SetAsFirstSibling();
            underlay.SetActive(false);
            for (int i = 0; i < frames.Length; i++)
            {
                GameObject frame = Instantiate(columnTemplate.GetComponent<RectTransform>().parent.gameObject, tableArea);
                frames[i] = (RectTransform)frame.transform;
                columns[i] = frame.GetComponentInChildren<TMP_Text>(true);
                if (columns[i] != null)
                {
                    columnTextRects[i] = columns[i].GetComponent<RectTransform>();
                    columnTextRects[i].anchorMin = Vector2.zero;
                    columnTextRects[i].anchorMax = Vector2.one;
                    columnTextRects[i].offsetMin = new Vector2(Padding, 0f);
                    columnTextRects[i].offsetMax = new Vector2(-Padding, 0f);
                    columnTextRects[i].gameObject.SetActive(true);
                }
                frame.SetActive(false);
            }
        }

        private Image GetCellRect()
        {
            if (cellCount >= cells.Length) return null;
            if (cells[cellCount] == null)
            {
                GameObject obj = Instantiate(cellTemplate.gameObject, tableArea);
                cells[cellCount] = obj.GetComponent<Image>();
                obj.transform.SetAsFirstSibling();
                grid.transform.SetAsFirstSibling();
            }
            return cells[cellCount++];
        }

        private int GetOverlayIndex(RectTransform[] poolFrames, TMP_Text[] poolTexts, RectTransform[] poolTextRects, TMP_Text template, int count, bool noWrap)
        {
            if (template == null || (noWrap ? overlayNoWrapPoolUnavailable : overlayPoolUnavailable) || count >= poolTexts.Length) return -1;
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
                    return -1;
                }
                poolTextRects[count] = poolTexts[count].GetComponent<RectTransform>();
                poolTextRects[count].anchorMin = Vector2.zero;
                poolTextRects[count].anchorMax = Vector2.one;
                poolTextRects[count].offsetMin = Vector2.zero;
                poolTextRects[count].offsetMax = Vector2.zero;
                poolTextRects[count].gameObject.SetActive(true);
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
            if (value.Length > MaxCellChars)
            {
                int length = MaxCellChars;
                char last = value[length - 1];
                if (last >= 0xD800 && last <= 0xDBFF) length--;
                value = value.Substring(0, length) + "…";
            }
            return value.Replace("<", "<\u200B");
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
            displayedSource = null; displayedRevision = -1;
            InitColors();
            workBodyHex = Hex(bodyColor);
            if (background != null) background.color = boardColor;
            if (titleText != null) titleText.color = bodyColor;
            if (statusText != null) statusText.color = bodyColor;
            InitPools();
            if (frames == null) return;
            for (int i = 0; i < frames.Length; i++) frames[i].gameObject.SetActive(false);
            for (int i = 0; i < columns.Length; i++) if (columns[i] != null) columns[i].text = "";
            for (int i = 0; i < cellCount; i++) if (cells[i] != null) cells[i].gameObject.SetActive(false);
            for (int i = 0; i < overlays.Length; i++) { if (overlays[i] != null) overlays[i].text = ""; if (overlayFrames[i] != null) overlayFrames[i].gameObject.SetActive(false); }
            for (int i = 0; i < overlaysNoWrap.Length; i++) { if (overlaysNoWrap[i] != null) overlaysNoWrap[i].text = ""; if (overlayNoWrapFrames[i] != null) overlayNoWrapFrames[i].gameObject.SetActive(false); }
            if (titleText != null) titleText.text = "";
            cellCount = 0; overlayCount = 0; overlayNoWrapCount = 0; grid.gameObject.SetActive(false);
            if (source == null) return;
            if (tableArea.rect.width <= 0f || tableArea.rect.height <= 0f || textScale <= 0f || maxRowHeight <= 0f) return;
            if (!source.HasData()) { displayedSource = source; displayedRevision = source.GetRevision(); return; }
            string name = string.IsNullOrEmpty(rangeName) ? source.GetFirstRangeName() : rangeName;
            DataDictionary range = source.GetRange(name);
            if (range == null) { if (titleText != null) titleText.text = rangeMissingText.Replace("{name}", name); displayedSource = source; displayedRevision = source.GetRevision(); return; }
            DataToken token;
            if (titleText != null) titleText.text = showTitle && range.TryGetValue("title", out token) && token.TokenType == TokenType.String ? Clean(Value(token), false) : "";
            bool header = range.TryGetValue("header", out token) && token.TokenType == TokenType.Boolean && token.Boolean;
            DataList rows = ListValue(range, "rows"), widths = ListValue(range, "cols"), colw = ListValue(range, "colw"), rowh = ListValue(range, "rowh");
            DataList align = ListValue(range, "align"), ha = ListValue(range, "ha"), bold = ListValue(range, "bold"), styles = ListValue(range, "style");
            DataList sizes = ListValue(range, "size"), colors = ListValue(range, "color"), backgrounds = ListValue(range, "bg"), merges = ListValue(range, "merges"), wrap = ListValue(range, "wrap");
            int nr = rows == null ? 0 : Mathf.Min(rows.Count, Mathf.Max(0, maxRows));
            nr = frames.Length == 0 ? 0 : Mathf.Min(nr, cells.Length / frames.Length);
            int nc = 0;
            for (int r = 0; r < nr; r++) if (rows[r].TokenType == TokenType.DataList) nc = Mathf.Max(nc, rows[r].DataList.Count);
            nc = Mathf.Min(nc, Mathf.Min(frames.Length, Mathf.Max(0, maxCols)));
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
            workHasFg = new bool[nr * nc];
            workHasBg = new bool[nr * nc];
            workWrapping = new bool[nr * nc];
            workCovered = new bool[nr * nc];
            workOverlayUsed = new bool[nr * nc];
            workSpanRows = new int[nr * nc];
            workSpanCols = new int[nr * nc];
            workLongest = new int[nc];
            workHeader = header;
            CollectCellData(rows, sizes, align, ha, bold, styles, colors, backgrounds, wrap);
            CalculateLayout(widths, colw, rowh, sizes);
            workMerges = merges;
            ApplyMerges();
            DrawCellsAndOverlays();
            DrawColumns();
            displayedSource = source; displayedRevision = source.GetRevision();
        }

        private void CollectCellData(DataList rows, DataList sizes, DataList align, DataList ha, DataList bold, DataList styles, DataList colors, DataList backgrounds, DataList wrap)
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
                    int scanLength = Mathf.Min(workRaw[i].Length, MaxCellChars + 1);
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

        private void CalculateLayout(DataList widths, DataList colw, DataList rowh, DataList sizes)
        {
            workXs = new float[workCols];
            workWs = new float[workCols];
            workYs = new float[workRows];
            workHs = new float[workRows];
            float[] sorted = new float[Mathf.Max(workRows, workCols)];
            int valid = 0;
            DataList weightList = widths != null ? widths : colw;
            bool useColw = widths == null && colw != null;
            for (int c = 0; c < workCols; c++)
            {
                float v = weightList == null ? Mathf.Clamp(workLongest[c], MinMeasuredColumnWidth, MaxMeasuredColumnWidth) : (c < weightList.Count ? (float)Number(weightList[c]) : 0f);
                workWs[c] = v;
                if (v > 0f) sorted[valid++] = v;
            }
            float fallback = Median(sorted, valid, useColw ? 100f : 1f);
            float total = 0f;
            for (int c = 0; c < workCols; c++)
            {
                if (workWs[c] <= 0f) workWs[c] = fallback;
                total += workWs[c];
            }
            float gap = showGrid || useColw ? 0f : columnGap;
            float available = Mathf.Max(0f, tableArea.rect.width - gap * (workCols - 1));
            float cumulative = 0f;
            for (int c = 0; c < workCols; c++)
            {
                float start = Mathf.Round(cumulative + c * gap);
                cumulative += available * workWs[c] / total;
                float end = Mathf.Round(cumulative + c * gap);
                workXs[c] = start;
                workWs[c] = end - start;
            }
            valid = 0;
            for (int r = 0; r < workRows; r++)
            {
                float v = rowh == null ? 1f : (r < rowh.Count ? (float)Number(rowh[r]) : 0f);
                workHs[r] = v;
                if (v > 0f) sorted[valid++] = v;
            }
            fallback = Median(sorted, valid, rowh == null ? 1f : 21f);
            total = 0f;
            for (int r = 0; r < workRows; r++)
            {
                if (workHs[r] <= 0f) workHs[r] = fallback;
                sorted[r] = workHs[r];
                total += workHs[r];
            }
            float median = Median(sorted, workRows, fallback);
            float scale = Mathf.Min(tableArea.rect.height / total, maxRowHeight / median);
            cumulative = 0f;
            for (int r = 0; r < workRows; r++)
            {
                workYs[r] = Mathf.Round(cumulative);
                cumulative += workHs[r] * scale;
                workHs[r] = Mathf.Round(cumulative) - workYs[r];
                sorted[r] = workHs[r];
            }
            workHeightAll = Mathf.Round(cumulative);
            float medianHeight = Median(sorted, workRows, 0f);
            workBasePx = sizes != null && rowh != null && workBasePt > 0f ? workBasePt * (4f / 3f) * scale * Mathf.Clamp(textScale, 0.5f, 3f) : medianHeight * DefaultCellHeightFactor * Mathf.Clamp(textScale, 0.5f, 3f) / DefaultTextScale;
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

        private void DrawCellsAndOverlays()
        {
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
                    int kind = workSpanRows[i] > 0 ? 1 : (workWrapping[i] || workRaw[i].Contains("\n") || workRaw[i].Contains("\r") ? 2 : 0);
                    if (kind == 0 && workAlignment[i] == 0 && workRaw[i].Length > 0)
                    {
                        float font = workBasePx * (workPt[i] > 0f && workBasePt > 0f ? FontFactor(workPt[i], workBasePt) : 1f);
                        if (workAscii[i] * font * FitFontWidthFactor + workOther[i] * font > workWs[c] - 2f * Padding)
                        {
                            int end = c + 1;
                            while (end < workCols && workRaw[r * workCols + end] == "" && !workHasBg[r * workCols + end] && !workCovered[r * workCols + end] && workSpanRows[r * workCols + end] == 0) end++;
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
                    overlay.text = Styled(workRaw[i], workBits[i], workPt[i], workFg[i], workHasFg[i], workBg[i], workHasBg[i], workBasePt, isHeader, true, workBodyHex);
                    frame.gameObject.SetActive(true);
                    workOverlayUsed[i] = true;
                }
            }
        }

        private void DrawColumns()
        {
            bool sameHeight = true;
            for (int r = 1; r < workRows; r++)
            {
                if (workHs[r] != workHs[0]) sameHeight = false;
            }
            for (int c = 0; c < workCols; c++)
            {
                Place(frames[c], workXs[c], 0f, workWs[c], workHeightAll);
                TMP_Text column = columns[c];
                if (column == null) continue;
                column.color = bodyColor;
                column.fontSize = workBasePx;
                column.alignment = TextAlignmentOptions.TopLeft;
                string content = "";
                for (int r = 0; r < workRows; r++)
                {
                    int i = r * workCols + c;
                    if (r > 0) content += "\n";
                    if (!sameHeight || r == 0) content += "<line-height=" + ((int)workHs[r]).ToString() + ">";
                    content += "<align=" + (workAlignment[i] == 1 ? "center" : (workAlignment[i] == 2 ? "right" : "left")) + ">";
                    float font = workBasePx * (workPt[i] > 0f && workBasePt > 0f ? FontFactor(workPt[i], workBasePt) : 1f);
                    float offset = workHs[r] * 0.5f - VerticalOffsetFactor * font;
                    bool shifted = Mathf.Abs(offset) >= 1f;
                    if (shifted) content += "<voffset=" + Mathf.RoundToInt(-offset).ToString() + ">";
                    if (!workCovered[i] && !workOverlayUsed[i]) content += Styled(workRaw[i], workBits[i], workPt[i], workFg[i], workHasFg[i], workBg[i], workHasBg[i], workBasePt, workHeader && r == 0, false, workBodyHex);
                    if (shifted) content += "</voffset>";
                }
                column.text = content;
                frames[c].gameObject.SetActive(true);
            }
        }
    }
}
