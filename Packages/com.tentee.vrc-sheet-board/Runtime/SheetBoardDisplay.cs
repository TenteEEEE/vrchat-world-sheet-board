using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;

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
        public Color boardColor, bodyColor, gridColor, headerBackgroundColor, zebraColor, normalColor, staleColor, errorColor;
        [Header("Texts")]
        public string rangeMissingText = "範囲「{name}」が見つかりません";
        public string sourceMissingText = "Sourceが設定されていません";

        [Header("Internal references (installer-wired)")]
        public Image background;
        public TMP_Text titleText, statusText;
        public RectTransform tableArea;
        public TMP_Text columnTemplate, overlayTemplate, overlayNoWrapTemplate;
        public Image cellTemplate, statusDot;

        private const float MinInterval = 0.25f, MaxInterval = 1.5f, Padding = 6f, DefaultTextScale = 1.4f;
        private float nextWatchAt;
        private bool watchStarted;
        private bool warnedTemplates;
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

        private void InitColors()
        {
            if (colorsReady) return;
            colorsReady = true;
            if (boardColor.a <= 0f) boardColor = lightTheme ? Color.white : new Color(0x12 / 255f, 0x16 / 255f, 0x1C / 255f, 1f);
            if (bodyColor.a <= 0f) bodyColor = lightTheme ? new Color(0x20 / 255f, 0x21 / 255f, 0x24 / 255f, 1f) : new Color(0xE8 / 255f, 0xED / 255f, 0xF2 / 255f, 1f);
            if (gridColor.a <= 0f) gridColor = lightTheme ? new Color(0xE2 / 255f, 0xE3 / 255f, 0xE3 / 255f, 1f) : new Color(1f, 1f, 1f, 0.08f);
            if (headerBackgroundColor.a <= 0f) headerBackgroundColor = new Color(0x2E / 255f, 0xD3 / 255f, 0xC6 / 255f, 0.18f);
            if (zebraColor.a <= 0f) zebraColor = lightTheme ? new Color(0f, 0f, 0f, 0.025f) : new Color(1f, 1f, 1f, 0.015f);
            if (normalColor.a <= 0f) normalColor = new Color(0x4C / 255f, 0xAF / 255f, 0x50 / 255f, 1f);
            if (staleColor.a <= 0f) staleColor = new Color(1f, 0xB3 / 255f, 0f, 1f);
            if (errorColor.a <= 0f) errorColor = new Color(0xE5 / 255f, 0x39 / 255f, 0x35 / 255f, 1f);
        }

        private void OnEnable()
        {
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
            InitColors();
            if (source == null) { SetStatus(sourceMissingText, errorColor); return; }
            VRCPlayerApi player = Networking.LocalPlayer;
            if (source.nearRadius > 0f && Utilities.IsValid(player) && Vector3.Distance(player.GetPosition(), transform.position) <= source.nearRadius) source._ReportNear(interval);
            if (displayedSource != source || displayedRevision != source.GetRevision()) RenderNow();
            int level = source.GetStatusLevel();
            SetStatus(source.GetStatusText(), level == 0 ? normalColor : (level == 1 ? staleColor : errorColor));
        }

        private void SetStatus(string value, Color color)
        {
            if (statusText != null && statusText.text != value) statusText.text = value;
            if (statusDot != null) statusDot.color = color;
        }

        public void OnRefreshPressed() { if (source != null) source.RequestRefresh(); }

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
            cells = new Image[Mathf.Max(0, maxRows) * frames.Length];
            overlayFrames = new RectTransform[Mathf.Max(0, maxOverlays)];
            overlays = new TMP_Text[overlayFrames.Length];
            overlayNoWrapFrames = new RectTransform[Mathf.Max(0, maxOverlays)];
            overlaysNoWrap = new TMP_Text[overlayNoWrapFrames.Length];
            GameObject underlay = Instantiate(cellTemplate.gameObject, tableArea);
            grid = underlay.GetComponent<Image>();
            underlay.transform.SetAsFirstSibling();
            underlay.SetActive(false);
            for (int i = 0; i < frames.Length; i++)
            {
                GameObject frame = Instantiate(columnTemplate.GetComponent<RectTransform>().parent.gameObject, tableArea);
                frames[i] = (RectTransform)frame.transform;
                columns[i] = frame.GetComponentInChildren<TMP_Text>(true);
                if (columns[i] != null) columns[i].GetComponent<RectTransform>().gameObject.SetActive(true);
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

        private TMP_Text GetOverlay()
        {
            if (overlayTemplate == null || overlayCount >= overlays.Length) return null;
            if (overlays[overlayCount] == null)
            {
                GameObject obj = Instantiate(overlayTemplate.GetComponent<RectTransform>().parent.gameObject, tableArea);
                overlayFrames[overlayCount] = (RectTransform)obj.transform;
                overlays[overlayCount] = obj.GetComponentInChildren<TMP_Text>(true);
                if (overlays[overlayCount] != null) overlays[overlayCount].GetComponent<RectTransform>().gameObject.SetActive(true);
            }
            TMP_Text result = overlays[overlayCount];
            if (result != null) overlayCount++;
            return result;
        }

        private TMP_Text GetNoWrapOverlay()
        {
            if (overlayNoWrapTemplate == null || overlayNoWrapCount >= overlaysNoWrap.Length) return null;
            if (overlaysNoWrap[overlayNoWrapCount] == null)
            {
                GameObject obj = Instantiate(overlayNoWrapTemplate.GetComponent<RectTransform>().parent.gameObject, tableArea);
                overlayNoWrapFrames[overlayNoWrapCount] = (RectTransform)obj.transform;
                overlaysNoWrap[overlayNoWrapCount] = obj.GetComponentInChildren<TMP_Text>(true);
                if (overlaysNoWrap[overlayNoWrapCount] != null) overlaysNoWrap[overlayNoWrapCount].GetComponent<RectTransform>().gameObject.SetActive(true);
            }
            TMP_Text result = overlaysNoWrap[overlayNoWrapCount];
            if (result != null) overlayNoWrapCount++;
            return result;
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
            if (value.Length > 200) value = value.Substring(0, 200) + "…";
            return value.Replace("<", "<\u200B");
        }

        private string Styled(string raw, int bits, float pt, Color fg, bool hasFg, Color bg, bool hasBg, float basePt, bool header, bool keepLines)
        {
            string text = Clean(raw, keepLines);
            if ((bits & 1) != 0 || header) text = "<b>" + text + "</b>";
            if ((bits & 2) != 0) text = "<i>" + text + "</i>";
            if ((bits & 4) != 0) text = "<u>" + text + "</u>";
            if ((bits & 8) != 0) text = "<s>" + text + "</s>";
            if (pt > 0f && basePt > 0f)
            {
                int percent = Mathf.RoundToInt(Mathf.Clamp(pt / basePt, 0.6f, 1.6f) * 100f);
                if (percent != 100) text = "<size=" + percent.ToString() + "%>" + text + "</size>";
            }
            Color chosen = bodyColor;
            if (hasFg)
            {
                chosen = fg;
                if (!hasBg && ((!lightTheme && Luminance(chosen) < 0.25f) || (lightTheme && Luminance(chosen) > 0.85f))) chosen = bodyColor;
            }
            else if (hasBg) chosen = Luminance(bg) >= 0.5f ? new Color(0x20 / 255f, 0x21 / 255f, 0x24 / 255f, 1f) : Color.white;
            return "<color=" + Hex(chosen) + ">" + text + "</color>";
        }

        private Color OpaqueOverBoard(Color color)
        {
            return new Color(color.r * color.a + boardColor.r * (1f - color.a), color.g * color.a + boardColor.g * (1f - color.a), color.b * color.a + boardColor.b * (1f - color.a), 1f);
        }

        public void RenderNow()
        {
            displayedSource = null; displayedRevision = -1;
            InitColors();
            if (background != null) background.color = boardColor;
            if (titleText != null) titleText.color = bodyColor;
            if (statusText != null) statusText.color = bodyColor;
            InitPools();
            if (frames == null) return;
            for (int i = 0; i < frames.Length; i++) frames[i].gameObject.SetActive(false);
            for (int i = 0; i < cellCount; i++) if (cells[i] != null) cells[i].gameObject.SetActive(false);
            for (int i = 0; i < overlayCount; i++) if (overlayFrames[i] != null) overlayFrames[i].gameObject.SetActive(false);
            for (int i = 0; i < overlayNoWrapCount; i++) if (overlayNoWrapFrames[i] != null) overlayNoWrapFrames[i].gameObject.SetActive(false);
            cellCount = 0; overlayCount = 0; overlayNoWrapCount = 0; grid.gameObject.SetActive(false);
            if (source == null) return;
            if (tableArea.rect.width <= 0f || tableArea.rect.height <= 0f || textScale <= 0f || maxRowHeight <= 0f) return;
            if (!source.HasData()) { if (titleText != null) titleText.text = ""; displayedSource = source; displayedRevision = source.GetRevision(); return; }
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
            int n = nr * nc;
            string[] raw = new string[n]; int[] ascii = new int[n], other = new int[n], bits = new int[n], alignment = new int[n];
            float[] pt = new float[n]; Color[] fg = new Color[n], bg = new Color[n];
            bool[] hasFg = new bool[n], hasBg = new bool[n], wrapping = new bool[n], covered = new bool[n], overlayUsed = new bool[n];
            int[] spanRows = new int[n], spanCols = new int[n], longest = new int[nc];
            float[] points = new float[16]; int[] counts = new int[16]; int distinct = 0;
            for (int r = 0; r < nr; r++) for (int c = 0; c < nc; c++)
            {
                int i = r * nc + c; raw[i] = Value(Cell(rows, r, c));
                for (int j = 0; j < raw[i].Length; j++) { if (raw[i][j] <= 127) ascii[i]++; else other[i]++; }
                longest[c] = Mathf.Max(longest[c], ascii[i] + 2 * other[i]);
                pt[i] = (float)Number(Cell(sizes, r, c));
                if (pt[i] > 0f) { int j = 0; while (j < distinct && points[j] != pt[i]) j++; if (j < 16) { if (j == distinct) points[distinct++] = pt[i]; counts[j]++; } }
                bits[i] = styles != null ? (int)Number(Cell(styles, r, c)) : (Number(Cell(bold, r, c)) == 1d ? 1 : 0);
                string f = Value(Cell(colors, r, c)), b = Value(Cell(backgrounds, r, c));
                hasFg[i] = ValidColor(f); hasBg[i] = ValidColor(b);
                if (hasFg[i]) fg[i] = ParseColor(f); if (hasBg[i]) bg[i] = ParseColor(b);
                string a = Value(Cell(ha, r, c)); if (a != "l" && a != "c" && a != "r") a = align != null && c < align.Count ? Value(align[c]) : "l";
                alignment[i] = a == "c" ? 1 : (a == "r" ? 2 : 0);
                wrapping[i] = Number(Cell(wrap, r, c)) == 1d;
            }
            float basePt = 0f; int best = 0;
            for (int i = 0; i < distinct; i++) if (counts[i] > best) { best = counts[i]; basePt = points[i]; }
            float[] xs = new float[nc], ws = new float[nc], ys = new float[nr], hs = new float[nr];
            float[] sorted = new float[Mathf.Max(nr, nc)]; int valid = 0;
            DataList weightList = widths != null ? widths : colw; bool useColw = widths == null && colw != null;
            for (int c = 0; c < nc; c++) { float v = weightList == null ? Mathf.Clamp(longest[c], 2, 40) : (c < weightList.Count ? (float)Number(weightList[c]) : 0f); ws[c] = v; if (v > 0f) sorted[valid++] = v; }
            float fallback = Median(sorted, valid, useColw ? 100f : 1f), total = 0f;
            for (int c = 0; c < nc; c++) { if (ws[c] <= 0f) ws[c] = fallback; total += ws[c]; }
            float gap = showGrid || useColw ? 0f : columnGap;
            float available = Mathf.Max(0f, tableArea.rect.width - gap * (nc - 1)), cumulative = 0f;
            for (int c = 0; c < nc; c++) { float start = Mathf.Round(cumulative + c * gap); cumulative += available * ws[c] / total; float end = Mathf.Round(cumulative + c * gap); xs[c] = start; ws[c] = end - start; }
            valid = 0;
            for (int r = 0; r < nr; r++) { float v = rowh == null ? 1f : (r < rowh.Count ? (float)Number(rowh[r]) : 0f); hs[r] = v; if (v > 0f) sorted[valid++] = v; }
            fallback = Median(sorted, valid, rowh == null ? 1f : 21f); total = 0f;
            for (int r = 0; r < nr; r++) { if (hs[r] <= 0f) hs[r] = fallback; sorted[r] = hs[r]; total += hs[r]; }
            float median = Median(sorted, nr, fallback), scale = Mathf.Min(tableArea.rect.height / total, maxRowHeight / median);
            cumulative = 0f;
            for (int r = 0; r < nr; r++) { ys[r] = Mathf.Round(cumulative); cumulative += hs[r] * scale; hs[r] = Mathf.Round(cumulative) - ys[r]; sorted[r] = hs[r]; }
            float heightAll = Mathf.Round(cumulative), medianHeight = Median(sorted, nr, 0f);
            float basePx = sizes != null && rowh != null && basePt > 0f ? basePt * (4f / 3f) * scale * Mathf.Clamp(textScale, 0.5f, 3f) : medianHeight * 0.62f * Mathf.Clamp(textScale, 0.5f, 3f) / DefaultTextScale;
            if (merges != null) for (int i = 0; i < merges.Count; i++)
            {
                if (merges[i].TokenType != TokenType.DataList || merges[i].DataList.Count < 4) continue;
                DataList m = merges[i].DataList; int r = (int)Number(m[0]), c = (int)Number(m[1]), rh = (int)Number(m[2]), cw = (int)Number(m[3]);
                if (r < 0 || c < 0 || r >= nr || c >= nc || rh < 1 || cw < 1) continue;
                rh = Mathf.Min(rh, nr - r); cw = Mathf.Min(cw, nc - c); if (rh == 1 && cw == 1) continue;
                bool overlaps = false;
                for (int rr = r; rr < r + rh; rr++) for (int cc = c; cc < c + cw; cc++) if (covered[rr * nc + cc] || spanRows[rr * nc + cc] > 0) overlaps = true;
                if (overlaps) continue;
                spanRows[r * nc + c] = rh; spanCols[r * nc + c] = cw;
                for (int rr = r; rr < r + rh; rr++) for (int cc = c; cc < c + cw; cc++) if (rr != r || cc != c) covered[rr * nc + cc] = true;
            }
            if (showGrid) { grid.color = gridColor; Place((RectTransform)grid.transform, 0f, 0f, tableArea.rect.width, heightAll); grid.gameObject.SetActive(true); }
            for (int r = 0; r < nr; r++) for (int c = 0; c < nc; c++)
            {
                int i = r * nc + c; if (covered[i]) continue;
                int rh = Mathf.Max(1, spanRows[i]), cw = Mathf.Max(1, spanCols[i]);
                float width = xs[c + cw - 1] + ws[c + cw - 1] - xs[c], height = ys[r + rh - 1] + hs[r + rh - 1] - ys[r];
                bool isHeader = header && r == 0, isZebra = zebra && !isHeader && r % 2 == 1;
                if (hasBg[i] || isHeader || isZebra || showGrid)
                {
                    Image rect = GetCellRect();
                    if (rect != null) { Color fill = hasBg[i] ? bg[i] : (isHeader ? headerBackgroundColor : (isZebra ? zebraColor : boardColor)); if (showGrid && !hasBg[i] && (isHeader || isZebra)) fill = OpaqueOverBoard(fill); rect.color = fill; float inset = showGrid ? Mathf.Max(0f, gridWidth) : 0f; Place((RectTransform)rect.transform, xs[c] + inset, ys[r] + inset, width - inset * 2f, height - inset * 2f); rect.gameObject.SetActive(true); }
                }
                int kind = spanRows[i] > 0 ? 1 : (wrapping[i] || raw[i].Contains("\n") || raw[i].Contains("\r") ? 2 : 0);
                if (kind == 0 && alignment[i] == 0 && raw[i].Length > 0)
                {
                    float font = basePx * (pt[i] > 0f && basePt > 0f ? Mathf.Clamp(pt[i] / basePt, 0.6f, 1.6f) : 1f);
                    if (ascii[i] * font * 0.55f + other[i] * font > ws[c] - 2f * Padding)
                    {
                        int end = c + 1;
                        while (end < nc && raw[r * nc + end] == "" && !hasBg[r * nc + end] && !covered[r * nc + end] && spanRows[r * nc + end] == 0) end++;
                        if (end > c + 1) { kind = 3; width = xs[end - 1] + ws[end - 1] - xs[c]; }
                    }
                }
                if (kind != 0)
                {
                    bool noWrap = kind == 3 || (kind == 1 && !wrapping[i]); TMP_Text overlay = noWrap ? GetNoWrapOverlay() : GetOverlay();
                    if (overlay != null)
                    {
                        RectTransform frame = noWrap ? overlayNoWrapFrames[overlayNoWrapCount - 1] : overlayFrames[overlayCount - 1];
                        Place(frame, xs[c] + Padding, ys[r], width - 2f * Padding, height);
                        RectTransform tr = overlay.GetComponent<RectTransform>(); tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
                        overlay.color = bodyColor; overlay.fontSize = basePx; overlay.alignment = alignment[i] == 1 ? TextAlignmentOptions.Center : (alignment[i] == 2 ? TextAlignmentOptions.Right : TextAlignmentOptions.Left);
                        overlay.text = Styled(raw[i], bits[i], pt[i], fg[i], hasFg[i], bg[i], hasBg[i], basePt, isHeader, true); frame.gameObject.SetActive(true); overlayUsed[i] = true;
                    }
                }
            }
            bool sameHeight = true; for (int r = 1; r < nr; r++) if (hs[r] != hs[0]) sameHeight = false;
            for (int c = 0; c < nc; c++)
            {
                Place(frames[c], xs[c], 0f, ws[c], heightAll); TMP_Text column = columns[c]; if (column == null) continue;
                RectTransform tr = column.GetComponent<RectTransform>(); tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one; tr.offsetMin = new Vector2(Padding, 0f); tr.offsetMax = new Vector2(-Padding, 0f);
                column.color = bodyColor; column.fontSize = basePx; column.alignment = TextAlignmentOptions.TopLeft; string content = "";
                for (int r = 0; r < nr; r++)
                {
                    int i = r * nc + c; if (r > 0) content += "\n";
                    if (!sameHeight || r == 0) content += "<line-height=" + ((int)hs[r]).ToString() + ">";
                    content += "<align=" + (alignment[i] == 1 ? "center" : (alignment[i] == 2 ? "right" : "left")) + ">";
                    float font = basePx * (pt[i] > 0f && basePt > 0f ? Mathf.Clamp(pt[i] / basePt, 0.6f, 1.6f) : 1f);
                    float offset = hs[r] * 0.5f - 0.78f * font; bool shifted = Mathf.Abs(offset) >= 1f;
                    if (shifted) content += "<voffset=" + Mathf.RoundToInt(-offset).ToString() + ">";
                    if (!covered[i] && !overlayUsed[i]) content += Styled(raw[i], bits[i], pt[i], fg[i], hasFg[i], bg[i], hasBg[i], basePt, header && r == 0, false);
                    if (shifted) content += "</voffset>";
                }
                column.text = content; frames[c].gameObject.SetActive(true);
            }
            displayedSource = source; displayedRevision = source.GetRevision();
        }
    }
}
