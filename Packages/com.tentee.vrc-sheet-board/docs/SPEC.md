# Sheet Board 仕様書

この文書が Sheet Board の唯一の仕様です。実装・README・検証はこの文書に従います。末尾の「改訂の経緯」は記録であり、規範ではありません。

## 1. 目的

Google スプレッドシートの決めた範囲を、**ワールドを再アップロードせずに** VRChat ワールド内の掲示板へ表示する。イベントの当番表・ローテーション表・タイムテーブル・お知らせなど、運営チームが頻繁に書き換える表を想定する。

- 運営スタッフはシートを編集するだけでよい。GitHub を扱うのは管理者 1 人だけ。
- 来訪者は VRChat の設定（Allow Untrusted URLs）を変えなくてよい。
- シートの見た目（色・書式・結合・列幅・行の高さ・配置）をなるべくそのまま再現する。

対象環境: Unity 2022.3、VRChat Worlds SDK 3.8 以降、UdonSharp 1.x、TextMeshPro 3.0.6、Google Apps Script（V8）。

## 2. 全体構成

```text
Google スプレッドシート
  └ Apps Script（シートにバインド。§6）
      ・「SheetBoard設定」タブに公開する範囲を名前付きで並べる
      ・編集時・書式変更時・5 分ごとに内容を確認し、変化があれば JSON（§5）を Gist へ PATCH
      ・変化が無くても 60 分に 1 回は生存通知として PATCH
  └ GitHub Gist（URL は固定、中身だけ変わる）
        │ gist.githubusercontent.com は String Loading の信頼ドメイン
        ▼
VRChat ワールド
  SheetBoardSource（JSON 1 本につき 1 つ。§3）
      ・エディターで焼き込んだ VRCUrl[]（同じ URL に ?v=0..N-1）を順に使って取得
      ・VRCJson で解析し、最後に成功したデータを保持
  SheetBoardDisplay（掲示板の数だけ。§4）
      ・Source の revision を監視し、変わったら自分の範囲名の表を描画
```

### 2.1 前提（実測・一次資料で確認済み）

- String Loading の信頼ドメインの正は `https://api.vrchat.cloud/api/1/config` の `stringHostUrlList`（取得には User-Agent が必要。無いと 403）。Google 系は含まれず、`gist.githubusercontent.com` と `*.github.io` は含まれる。
- String Loader は**リダイレクトを追わない**。
- String Loader は 1 件 / 5 秒で、ワールド内のすべての読み込み処理が共有し、超過分はランダム順に待つ。
- `VRCUrl` は実行時に作れない。
- gist raw（コミット SHA なしの形）は `Cache-Control: max-age=300`。クエリ文字列が違えば CDN キャッシュの別エントリになる。
- VRChat クライアント自身が文字列をキャッシュするかは未確認。

### 2.2 同期

ネットワーク同期は使わない（`[UdonSynced]` もネットワークイベントも無い）。全クライアントが同じ URL を読むので、途中参加者も同じ内容になる。

## 3. SheetBoardSource

`[UdonBehaviourSyncMode(BehaviourSyncMode.None)]`。

### 3.1 Inspector

```csharp
public VRCUrl[] urls;                  // エディター（§7.2）が baseUrl から生成する
public string baseUrl = "";            // 運用者が貼る URL。実行時は使わない
public float refreshInterval = 60f;    // 秒。実効値は max(30, この値)
public float nearRadius = 0f;          // メートル。0 = 距離に関係なく更新する
public float staleMinutes = 90f;       // 生存通知がこの時間途絶えたら「止まっています」
public float manualCooldown = 30f;     // 手動更新のクールダウン（秒）
public float startDelayMax = 3f;       // 初回取得をランダムに遅らせる上限（秒）
// 状態行の文言（§3.6）
public string urlMissingText, loadingText, waitingNearText, failedText, freshText, staleText, retryText;
public string minutesUnit = "分前", hoursUnit = "時間前", daysUnit = "日前";
```

### 3.2 取得スケジュール

時間はすべて `Time.realtimeSinceStartup`（以下 now）。`SendCustomEventDelayedSeconds` で 1 秒ごとの `_Tick` を回す（`Update` は使わない）。

- **ループの維持**: 遅延イベントは引数を持てないので、次の予定時刻 `nextTickAt` で重複ループを止める。`_Tick` は `now < nextTickAt − 0.5 × 周期` なら何もせずに終わり（重複した古いループが消える）、それ以外は `nextTickAt = now + 周期` として再スケジュールしてから処理する。許容幅を持たせるのは、遅延イベントが予定よりわずかに早く発火してもループが止まらないようにするため。`OnEnable` では、ループが止まっているとき（`now > nextTickAt + 2 × 周期`、または未開始）だけ新しくスケジュールする。世代番号は使わない。
- **初回**: 最初の有効化で `nextFetchAt = now + Random(0, startDelayMax)`、URL の添字を `Random(0, urls.Length)` から始める。
- **成功**: `nextFetchAt = now + max(30, refreshInterval) × Random(1.0, 1.1)`。
- **失敗**（読み込みエラー、または JSON が §5 として不正）: `failCount` を増やし、`min(30 × 2^(failCount−1), 300)` 秒 × Random(0.8, 1.2) 後に再試行。成功で `failCount = 0`。
- **近接条件**: `nearRadius > 0` のとき、直近 `max(2, 2 × 報告された nearCheckInterval)` 秒以内に Display から `_ReportNear()` が無ければ取得しない。近づいた時点で `nextFetchAt` を過ぎていれば、その `_Tick` で取得する。
- **多重取得の禁止**: 取得中は次の取得を始めない。60 秒コールバックが来なければ失敗扱いにする。取得に使った `VRCUrl` を保持し、コールバックの `result.Url` と一致しない（タイムアウト後に遅れて届いた）結果は無視する。
- **URL のローテーション**: 取得ごとに添字を 1 進める。null または空の URL は飛ばす。有効な URL が 1 つも無ければ取得しない（状態は「URL 未設定」）。有効な URL の有無は最初の有効化で 1 度だけ数えてキャッシュする。

### 3.3 手動更新

`public void RequestRefresh()`: ローカルのみ。前回の受付から `manualCooldown` 秒未満、または取得中なら無視。受け付けたら近接条件とバックオフを無視して次の `_Tick` で取得する。

### 3.4 取得結果の処理

`public bool ApplyJson(string json)`（エディターの検証からも直接呼ぶ。成功で true）。

1. `json` が null か空なら失敗。
2. `VRCJson.TryDeserializeFromJson` でルートが `DataDictionary`、`format == "sheetboard/1"`（文字列）、`rev` が文字列、`updatedUnix` が数値、`ranges` が `DataDictionary` であること。数値は Int / Long / Double のどれでも受ける。
3. **時刻の検証**: `updatedUnix` は `0 < x < 253402300799`（西暦 9999 年末まで）。範囲外なら失敗。`aliveUnix`（任意）は範囲外・非数値なら 0 として扱う。範囲外の値で `DateTime` が例外を投げると Udon の behaviour が止まるため、必ずここで弾く。
4. **巻き戻り防止**: 保持中の `updatedUnix` より古い `updatedUnix` が来たら（CDN に残った古い版）、データは差し替えずに成功として扱う。
5. `rev` が保持中と同じならデータは差し替えない。違えば `ranges`・`rev`・`updatedUnix` を保存し、`revision`（int）を 1 増やす。
6. `aliveUnix` は手順 4・5 の結果にかかわらず `max(保持値, 新しい値)` に更新する。

失敗時は**保持データを決して消さない**。

### 3.5 公開メソッド（Display 用）

UdonSharp では他の behaviour のプロパティを呼ばず、`out` 引数も使わないので、すべて戻り値のあるメソッドにする。

```csharp
public int GetRevision();
public bool HasData();                         // 一度でも取得に成功したか
public DataDictionary GetRange(string name);   // 無ければ null
public string GetFirstRangeName();             // ranges の最初のキー。無ければ ""
public string GetStatusText();                 // キャッシュ済みの文字列を返すだけ
public int GetStatusLevel();                   // 0 = 正常, 1 = 止まっている/再試行中, 2 = エラー/未取得
public void _ReportNear(float checkInterval);  // Display から
public void RequestRefresh();
public bool ApplyJson(string json);
```

状態行の文字列とレベルは **`_Tick` で 1 秒に 1 回だけ計算してキャッシュ**する（Display が何枚あっても計算は 1 回）。`ApplyJson` の直後にも再計算する。

### 3.6 状態行

| 状況 | level | 既定の文言 |
| --- | --- | --- |
| URL 未設定 | 2 | `URLが設定されていません` |
| 初回取得前・初回取得中 | 2 | `読み込み中…` |
| 近接待ちで一度も取得していない | 2 | `近づくと読み込みます` |
| 一度も成功せずに失敗（再試行待ち） | 2 | `読み込めません（{code}）  再試行まで{s}秒` |
| 成功・正常 | 0 | `更新 {time}（{ago}）` |
| 成功・生存通知が `staleMinutes` 超過 | 1 | `更新 {time}（{ago}）・自動公開が止まっています` |
| 成功済みで直近が失敗 | 1 | `更新 {time}（{ago}）・再試行中` |

- 「止まっています」の判定は `aliveUnix > 0 かつ 現在 − aliveUnix > staleMinutes`。`aliveUnix` が 0（旧形式）のときは判定しない。
- `{time}` は `updatedUnix`（内容が最後に変わった時刻）を**閲覧者の現地時刻**にした `HH:mm`。`Hour.ToString("00") + ":" + Minute.ToString("00")` で組む（書式文字列の `:` はカルチャ依存のため）。
- `{ago}` は現在と `updatedUnix` の差。60 分未満は `n + minutesUnit`、24 時間未満は `h + hoursUnit`、それ以上は `d + daysUnit`。現在時刻はローカルプレイヤーが有効なら `Networking.GetNetworkDateTime()`、そうでなければ `DateTime.UtcNow`。
- 一度も成功していない間の取得中は `{s}` を出さず `loadingText`（level 2）。成功済みなら取得中も直前の状態（正常・止まっている・再試行中）をそのまま出す（定期取得のたびに赤くしない）。
- 文字列の組み立てに `string.Format` / 補間は使わず、`+` と `Replace` を使う。

## 4. SheetBoardDisplay

`[UdonBehaviourSyncMode(BehaviourSyncMode.None)]`。

### 4.1 Inspector

```csharp
public SheetBoardSource source;
public string rangeName = "";          // 空なら最初の範囲（§4.6）
public bool lightTheme = true;
public int maxRows = 40;               // 見出し行を含む
public int maxCols = 12;
public bool showTitle = true;
public bool showGrid = true;
public float gridWidth = 1f;           // px
public bool zebra = false;
public float textScale = 1.4f;         // 0.5〜3
public float maxRowHeight = 64f;       // px
public float columnGap = 12f;          // px。格子なし・cols 指定のときだけ有効
public int maxOverlays = 24;           // プールごと
public float nearCheckInterval = 1f;   // 秒。0.25〜1.5 にクランプ
// 色（α 0 なら lightTheme に応じた既定値で埋める）
public Color boardColor, bodyColor, gridColor, headerBackgroundColor, zebraColor, normalColor, staleColor, errorColor;
public string rangeMissingText = "範囲「{name}」が見つかりません";
public string sourceMissingText = "Sourceが設定されていません";
// installer が配線する内部参照（[Header] で区切る）
public Image background;
public TMP_Text titleText, statusText;
public RectTransform tableArea;
public TMP_Text columnTemplate, overlayTemplate, overlayNoWrapTemplate;
public Image cellTemplate, statusDot;
```

TMP 参照は `TMP_Text` 型にする（`TextMeshProUGUI.fontSize` の setter は Udon 非公開）。

| 色 | ライト（既定） | ダーク |
| --- | --- | --- |
| boardColor | `#FFFFFF` | `#12161C` |
| bodyColor | `#202124` | `#E8EDF2` |
| gridColor | `#E2E3E3` | `#FFFFFF` α 0.08 |
| headerBackgroundColor | `#2ED3C6` α 0.18 | 同左 |
| zebraColor | `#000000` α 0.025 | `#FFFFFF` α 0.015 |
| normal / stale / error | `#4CAF50` / `#FFB300` / `#E53935` | 同左 |

### 4.2 描画の構造

描画順（奥から）: 格子の下地 → セル矩形 → 列 TMP → オーバーレイ。

- **列 TMP**: 列ごとに 1 つ。`columnTemplate` の親（RectMask2D の列枠）ごと複製する。セルは改行区切りで 1 列の TMP に並べる。テンプレートは折り返し OFF・overflow Overflow・richText ON・margin 0。
- **セル矩形**: `cellTemplate` から必要になった分だけ作る（上限 `maxRows × maxCols`）。格子の下地も `cellTemplate` から 1 枚作る。
- **オーバーレイ**: 折り返しあり（`overlayTemplate`）と折り返しなし（`overlayNoWrapTemplate`）の 2 プール。どちらも上限 `maxOverlays`。
- プールは初回の描画で作り、以後は SetActive で再利用する。実際に描く行数・列数は「プールに収まる数」で上限をかける（実行時に `maxRows` などを上げてもはみ出さない）。
- Udon で使えない API: `TMP_Text.transform / gameObject / rectTransform` → `GetComponent<RectTransform>()` を使う。`richText / enableWordWrapping / overflowMode / margin` の setter → テンプレート側で設定する。使える setter は `text / color / fontSize / alignment`。

### 4.3 前処理（1 回の走査）

描画の最初にセルを 1 回だけ走査し、以後はこの配列だけを読む（DataList の参照を繰り返さない）。

- セルごと: 加工前の文字列、ASCII 文字数と非 ASCII 文字数、pt、style ビット、文字色（有無と Color）、背景色（有無と Color）、横配置（int 0 左 / 1 中央 / 2 右）、wrap。
- 結合: `covered[]`（結合で覆われたセル）と、起点セルの行数・列数。範囲外は切り詰め、重なる結合と 1×1 の結合は捨てる。
- 基準 pt: 異なる pt 値を最大 16 種類まで数えて最頻値を取る（線形時間）。

### 4.4 レイアウト

- **列幅**: `cols`（手動）があればその比、無ければ `colw`（シートの列幅 px）の比、どちらも無ければ列の最長セルの表示幅（ASCII 1・その他 2、2〜40 にクランプ）の比。配列が列数より短い・0 以下の値は「有効な値の中央値」（無ければ `colw` は 100、手動・自動は 1）で補う。
- **列間**: `showGrid` または `colw` 使用時は 0、それ以外は `columnGap`。
- **行の高さ**: `rowh` があればその比（足りない・0 以下は有効値の中央値、無ければ 21）、無ければ全行同じ。倍率 `k = min(表領域の高さ / Σrowh, maxRowHeight / median(rowh))`。
- **整数化**: 行の上端 `Y_r = round(累積)`、高さ `h_r = Y_{r+1} − Y_r` として整数 px にそろえ、セル矩形と `<line-height>` の両方で同じ値を使う（丸め誤差の累積でずれないように）。列も同様。
- **基準の文字サイズ**: `size` と `rowh` の両方があるとき `basePx = 基準pt × 4/3 × k × textScale`。それ以外は `basePx = median(h) × 0.62 × textScale / 1.4`（既定の 1.4 で従来と同じ大きさになり、「書式を反映」なしの範囲でも拡大率が効く）。
- **セル内パディング**: 左右 6px。列 TMP もオーバーレイも、枠の内側へ 6px 寄せる。はみ出し判定は「列幅 − 12」と比べる。
- **描けない状態**: 表領域の幅・高さが 0 以下、`textScale`・`maxRowHeight` が 0 以下のときは描かず、描画済みの revision を確定しない（次回また描画を試みる）。

### 4.5 セルの文字列

1. 値を文字列化（数値なら `ToString()`）。
2. 列 TMP では改行を空白 1 つに置き換える（オーバーレイでは改行を残す）。
3. 200 文字を超えたら切って `…`。
4. **エスケープ**: `<` を `<` + U+200B に置き換える（ソースには `"​"` と書く）。タイトルも同じ処理（改行→空白、200 文字、エスケープ）を通す。
5. 書式: style ビット（1 太字、2 斜体、4 下線、8 取り消し線）を `<b><i><u><s>` で囲む。見出し行（`header`）は太字。`size` があれば `size / 基準pt` を 0.6〜1.6 に収めた倍率を `<size=NN%>` で付ける（100% は付けない）。
6. 文字色（コントラスト補正）を `<color=#RRGGBB>` で付ける:
   - 文字色が無く背景色があるとき: 背景の輝度 `0.2126R + 0.7152G + 0.0722B` が 0.5 以上なら `#202124`、未満なら `#FFFFFF`。
   - どちらも無いとき: `bodyColor`。
   - 文字色があり背景色が無いとき: ダークで輝度 0.25 未満、またはライトで 0.85 超なら `bodyColor`。それ以外はその色。
   - 背景色があるときの明示色はそのまま。

列 TMP の各行の先頭に `<line-height=h_r>`（行の高さが全部同じなら先頭 1 回）、`<align=left|center|right>`、縦中央用の `<voffset=-N>`…`</voffset>`（`N = h_r/2 − 0.78 × その行の実効文字サイズ`、|N| < 1 なら付けない）を付ける。結合で覆われたセルとオーバーレイで描くセルは空行にする（タグは付けて行位置を保つ）。

### 4.6 範囲の選択とタイトル

- `rangeName` が空なら `source.GetFirstRangeName()`（設定タブの一番上の有効な行）を使う。
- `source.HasData()` が false の間はタイトルを空にし、表を描かない。
- データがあり範囲が無いときだけ `rangeMissingText` を出す。
- `showTitle` かつ `title` があれば、§4.5 の加工をしてタイトルに出す。

### 4.7 セル矩形の色

- 描くのは結合で覆われていないセル（結合の起点は結合範囲の大きさで 1 つ）。
- 色の優先順位: `bg` → 見出し行なら `headerBackgroundColor` → ゼブラ行なら `zebraColor` → `showGrid` なら `boardColor` → どれでもなければ描かない。
- 半透明の色（見出し・ゼブラ）は、`showGrid` のとき格子色の上で濁らないよう `boardColor` と合成した不透明色で描く。
- `showGrid` のとき、表全体の大きさの下地を `gridColor` で 1 枚敷き、セル矩形を四辺 `gridWidth` だけ内側に縮める。

### 4.8 オーバーレイ

次のセルはオーバーレイで描き、列 TMP の該当行は空にする。

1. 結合の起点（1×1 を除く）。`wrap` なら折り返しありプール、それ以外は折り返しなしプール。
2. `wrap == 1` または改行を含むセル。折り返しありプール。
3. はみ出し: `wrap` でなく左寄せで、推定幅（ASCII = 文字サイズ × 0.55、その他 = 文字サイズ × 1.0）が「列幅 − 12」を超え、右隣が空（値なし・背景色なし・結合なし）。右へ続く空セルの終わりまでを枠にする。折り返しなしプール。

プールが尽きた・テンプレートが null のときは列 TMP に 1 行で描く。オーバーレイは縦中央、横は配置どおり、文字サイズ `basePx`。

### 4.9 監視ループ

`_Watch` も §3.2 と同じ方法（予定時刻と許容幅、止まっているときだけ `OnEnable` で再開）でループを維持する。最初に再スケジュールしてから:

1. `source` が null なら状態行に `sourceMissingText`。
2. `nearRadius > 0` かつローカルプレイヤーが有効で、距離が `nearRadius` 以下なら `source._ReportNear(nearCheckInterval)`。
3. `source` の参照か `GetRevision()` が前回の描画と違えば描画する。
4. 状態行と状態ランプを更新する（文字列が同じなら代入しない）。

`public void RenderNow()`（現在のデータで強制的に描画）と色・プールの初期化は、`Start` を経ないエディターの C# から呼んでも動くこと。テンプレートが欠けているときは初回に 1 度だけ `Debug.LogWarning` を出す。

### 4.10 手動更新ボタン

ボタンの onClick は Display の `OnRefreshPressed()` を呼び、Display が `source.RequestRefresh()` を呼ぶ。

## 5. JSON 形式 `sheetboard/1`

Apps Script が出力し、Source が読む。UTF-8。

```json
{
  "format": "sheetboard/1",
  "rev": "3f2a9c1e",
  "updated": "2026-11-02T12:34:56+09:00",
  "updatedUnix": 1793590496,
  "aliveUnix": 1793594096,
  "ranges": {
    "rotation": {
      "title": "今週のローテーション",
      "header": false,
      "rows": [["個室番号", "ゲスト"], ["1／AB", "ゲスト01"]],
      "style": [[1, 1], [1, 0]],
      "size": [[10, 10], [10, 10]],
      "color": [["", ""], ["#FFFFFF", ""]],
      "bg": [["#D9D2E9", "#D9D2E9"], ["#111111", ""]],
      "ha": [["c", "c"], ["c", "c"]],
      "wrap": [[0, 0], [0, 0]],
      "colw": [250, 240],
      "rowh": [57, 46],
      "merges": [[0, 0, 1, 2]]
    }
  }
}
```

| キー | 型 | 必須 | 意味 |
| --- | --- | --- | --- |
| `format` | 文字列 | 必須 | `"sheetboard/1"` |
| `rev` | 文字列 | 必須 | 設定タブ順の `[範囲名, 範囲データ]` の配列を作り、各範囲内のオブジェクトのキー順を固定した JSON の MD5 先頭 8 桁。範囲順の変更も更新とする |
| `updated` | 文字列 | 任意 | 人が読むための ISO 8601（ランタイムは読まない） |
| `updatedUnix` | 数値 | 必須 | 内容が最後に変わった時刻（UNIX 秒）。内容が同じ再送では変えない |
| `aliveUnix` | 数値 | 任意 | 最後に公開した時刻（UNIX 秒）。生存通知でも更新される |
| `ranges` | オブジェクト | 必須 | 範囲名 → 範囲。キーの順序は設定タブの順 |

範囲のキー（すべて任意、`rows` 以外は「書式を反映」が TRUE のときだけ出る。`title`・`header`・`cols`・`align` は常に出しうる）:

| キー | 型 | 意味 |
| --- | --- | --- |
| `rows` | 文字列の 2 次元配列 | 表示値（必須） |
| `title` | 文字列 | タイトル |
| `header` | 真偽 | 0 行目を見出しとして強調 |
| `cols` / `align` | 数値配列 / 文字列配列 | 手動の列幅比 / 列ごとの配置（設定タブ F・G 列） |
| `style` | int の 2 次元配列 | 1 太字 / 2 斜体 / 4 下線 / 8 取り消し線 |
| `size` | 数値の 2 次元配列 | 文字サイズ（pt） |
| `color` | 文字列の 2 次元配列 | 文字色 `#RRGGBB`。黒 `#000000` は空 |
| `bg` | 文字列の 2 次元配列 | 背景色。白 `#FFFFFF` は空 |
| `ha` | 文字列の 2 次元配列 | セルごとの横配置 `l` / `c` / `r`（G 列が空のとき） |
| `wrap` | 0/1 の 2 次元配列 | 折り返し |
| `colw` / `rowh` | 数値配列 | 列幅 / 行の高さ（px）。`colw` は F 列が空のとき |
| `merges` | 配列の配列 | `[r, c, rowSpan, colSpan]`（出力後の 0 始まりの番号） |

ランタイムは、任意キーが無くても、配列の長さが `rows` と合わなくても例外を出さずに動くこと。旧形式の `bold`（0/1 の 2 次元配列）は `style` が無いときだけ読む（Apps Script は出力しない）。

## 6. Apps Script（`AppsScript/`）

配布物は `SheetBoard.gs` と `appsscript.json`。シートの「拡張機能 → Apps Script」に入れる（スタンドアロンのプロジェクトでは動かない）。デプロイは不要。

### 6.1 状態の保存先

| 保存先 | 中身 |
| --- | --- |
| UserProperties（実行者ごと） | GitHub トークン、ログイン名、Device Flow の device_code |
| DocumentProperties（シートで共有） | gistId、**Gist の所有者のログイン名**、**ワールドに貼る URL**、前回の rev、updatedUnix、最終公開時刻、直近のエラー、メール送信時刻（種類ごと）、警告、URL 変更のお知らせ、保留中の公開フラグ |

### 6.2 メニュー

| 項目 | 動作 | 誰が使うか |
| --- | --- | --- |
| はじめにセットアップ | §6.4 のウィザード | 管理者 |
| 今すぐ公開 | 実行者がトークンを持っていれば強制公開。持っていなければ公開せず、保留フラグを立てて「次の自動確認（最大 5 分）で公開されます」と表示 | 誰でも |
| 状態とURLを表示 | DocumentProperties の URL・最終公開・直近のエラー・警告を表示 | 誰でも |
| GitHub連携をやり直す | トークンを取り直し、トリガーが無ければ設置し直す | 管理者 |
| 連携を解除 | 自分のトリガーとトークンを削除（Gist は消さない） | 管理者 |
| 公開先を作り直す | §6.5 の管理者交代用。新しい Gist（新しい URL）を作る | 管理者 |

トークンを持たない実行者が公開処理を起こしても、共有のエラー状態・メール抑止には一切書き込まない。

### 6.3 設定タブ「SheetBoard設定」

1 行目が見出し、2 行目以降が 1 範囲。

| 列 | 見出し | 意味 |
| --- | --- | --- |
| A | 名前 | ワールド側 `rangeName`。**空、または `#` で始まる行は無視**（コメント行） |
| B | 範囲 | `シート名!A1:F19`。B 列は書式なしテキスト |
| C | タイトル | 空なら省略 |
| D | 1行目は見出し | チェックボックス |
| E | 書式を反映 | チェックボックス |
| F | 列幅の比 | 空ならシートの列幅（`colw`） |
| G | 配置 | 空ならシートの配置（`ha`） |

- セットアップ時に無ければ作り、例の行として名前 `#sample`（無効）、範囲 `<設定タブ以外の左端のシート名>!A1:F20`（クォートなし）、見出し FALSE、書式 TRUE を入れる。**例の行は `#` を消すまで公開されない**（範囲を決める前に公開されて Gist の履歴に残るのを防ぐ）。
- 範囲は自前の `parseRangeRef(text)` → `{sheet, a1}` で解析する。受け付ける形: `シート名!A1:C10`、`'シート名'!A1:C10`（`''` は `'`）、先頭の `'` が消えた `シート名'!A1:C10`、`A1:C10`（シート名省略 = 設定タブ以外の左端）。区切りは最後の `!`（セル先頭の `'` はスプレッドシートが文字列扱いの合図として消すため、この寛容さが必要）。
- **行ごとの誤りはその行だけ飛ばす**（警告に「「名前」: シート「X」が見つかりません」「「名前」: 範囲「A1:C10」を読めません」を記録）。1 行の誤りで全体の公開を止めない。
- 名前の重複は先の行を採用して警告。範囲は最大 20 個。
- 範囲は API を呼ぶ前に、シートの最終行・最終列と上限（100 行 × 20 列）で切り詰める（`A:F` のような列全体の指定でも行ごとの呼び出しが膨らまないように）。

### 6.4 セットアップと GitHub 連携（OAuth Device Flow）

1. 設定タブを用意する。
2. トークンが無ければ Device Flow: `POST https://github.com/login/device/code`（`client_id`、`scope=gist`）。`user_code` を大きく表示し、「GitHubを開く」「コードをコピー」と状態行を出す。ダイアログの JS が `interval` 秒ごとに `pollDeviceToken()` を呼ぶ。`authorization_pending` は継続、`slow_down` は +5 秒、`expired_token`「コードの有効期限（15 分）が切れました」、`access_denied`「GitHub で承認がキャンセルされました」、その他「連携できませんでした（エラー名）」でやり直しを案内。`google.script.run` の一時的な失敗は 3 回まで再試行する。
3. 成功したらトークンを UserProperties に保存し、`GET /user` でログイン名を保存する。`/user` の失敗は「GitHub のアカウントを確認できませんでした（ステータス）」。
4. `GITHUB_CLIENT_ID` は配布元の OAuth App の公開 Client ID（秘密ではない）。空のときは「Personal access token（classic、gist 権限）を貼り付ける」ダイアログにし、`X-OAuth-Scopes` に `gist` が無ければ拒否する。
5. `finishSetup()`: 保存済みの Gist の所有者が実行者と違えば、トリガーを設置せず「この公開先は <所有者> さんの GitHub にあります。管理者を交代する場合は、メニューの『公開先を作り直す』を使ってください」と表示して終わる。それ以外はトリガーを設置し、強制公開し、URL を表示し、URL 変更のお知らせを消す。連携のやり直し（§6.2）でトリガーを設置し直すときも同じ所有者確認を行う。

### 6.5 Gist

- 作成: `POST /gists`、`public: false`、ファイル `sheetboard.json`、description `Sheet Board: <スプレッドシート名>`。gistId・所有者ログイン名・URL を DocumentProperties に保存する。
- 更新: `PATCH /gists/{id}`（`Authorization: Bearer`、`Accept: application/vnd.github+json`、`X-GitHub-Api-Version: 2022-11-28`）。
- URL: `https://gist.githubusercontent.com/<所有者>/<id>/raw/sheetboard.json`（コミット SHA を含めない）。
- **所有者の確認**: 実行者のログイン名が保存済みの Gist の所有者と違えば PATCH も作り直しもせず、「この公開先は <所有者> さんの GitHub にあります。管理者を交代する場合は、メニューの『公開先を作り直す』を使ってください」というエラーにする（2 人が交互に作り直し続ける事故を防ぐ）。所有者が未保存（旧版から更新した場合）なら、その時点の実行者を所有者として保存する。
- 所有者本人の PATCH が 404（Gist が消された）のときだけ作り直し、新しい URL を「URL 変更のお知らせ」に記録してメールする。このメールも種類ごとの 24 時間抑止を通す（成功時の抑止リセットは URL 変更の種類を対象外にする）。
- 作成時にログイン名が未保存なら、先に `GET /user` で取得して保存する（URL に null が入らないように）。
- **公開先を作り直す（管理者交代用）**: メニュー「公開先を作り直す」は、確認ダイアログの後、DocumentProperties の gistId・所有者・URL を消し、実行者の GitHub に新しい Gist を作って公開する（トークンが無ければ先に連携画面を開き、承認後にこのメニューをもう一度実行する）。公開成功後、新しい所有者の自動公開トリガーを不足時に設置する。元の Gist は消さない。新しい URL を表示し、ワールドの URL 差し替えが必要だと伝える。

### 6.6 公開処理 `publish(force)`

1. ロックを取る（トリガー経由は `tryLock(0)`、メニュー経由は `tryLock(10000)`）。取れなければ保留フラグを立てて終わる。
2. 実行者がトークンを持たなければ保留フラグを立てて終わる（§6.2）。
3. 設定タブを読み、各範囲の表示値と、書式を反映する範囲は書式（§5）を一括取得する。非表示の行・列は出力しない。チェックボックスは `■` / `□`。
4. `rev` を計算する。`force` でなく保留フラグも無く、`rev` が前回と同じで、最終公開から 60 分未満なら終わる。
5. `rev` が変わったときだけ `updatedUnix` を現在時刻にする。`aliveUnix` は常に現在時刻。`updated` はシートのタイムゾーン（`sheetTimeZone()`: スプレッドシート → スクリプト → `Asia/Tokyo`）で ISO 8601。
6. JSON が 500 KB を超えたらエラー。§6.5 のとおり PATCH。
7. 成功したら rev・時刻を保存し、直近のエラーと種類ごとのメール抑止をリセットし、保留フラグを下ろす。警告は内容が変わったときだけ書き込む。
8. 失敗したら直近のエラーを `<ISO 時刻> <文言>` で記録し、§6.8 の通知。

### 6.7 トリガー

- インストール型 onEdit（`onEditInstalled`）と onChange（`onChangeInstalled`、`changeType` が `FORMAT`・行列の挿入削除・`OTHER`）: 前回の公開から 5 秒未満なら、保留フラグを立てて終わる（5 分ごとの確認で公開される）。それ以外は公開する。
- 時間主導 5 分ごと（`onTimer`）: 公開する（保留フラグがあれば内容が同じでも公開する）。
- トリガー経由の公開は例外を投げない（Google 側の失敗メールと二重にならないように）。
- `ScriptApp.getProjectTriggers()` は実行者自身のトリガーしか返さないので、設置・解除は実行者のトリガーだけを扱う。
- 単純トリガー名 `onEdit` は使わない。

### 6.8 失敗の通知

種類（HTTP ステータス、それ以外は `other`）ごとに 24 時間に 1 回まで、トリガー所有者へメールする。本文にスプレッドシート名と URL を入れる。401 / 403 は「メニュー → GitHub連携をやり直す」を案内する。

### 6.9 `appsscript.json`

V8、`Asia/Tokyo`、スコープは `spreadsheets.currentonly`、`script.external_request`、`script.scriptapp`、`script.container.ui`、`script.send_mail`、`userinfo.email` の 6 つ。

### 6.10 実装上の決まり

- 内部関数は名前の末尾に `_` を付けて `google.script.run` から呼べないようにする。メニュー・トリガー・ダイアログから呼ぶ関数だけ `_` なし。
- Google API に触れない純粋関数（`parseRangeRef`、`trimEmpty`、`parseRatioList`、`parseAlignList`、`normalizeColor`、`generalAlign`、`mapAlign`、`styleBits`、`hiddenFilter`、`remapMerges`、`buildRangePayload`、`stableStringify`、`isoWithOffset`）は名前を変えず `_` も付けない。Node の `vm` で読み込んでもトップレベルで例外にならないこと。
- トークンはログ・URL・メール・エラー文に出さない。ダイアログに差し込む動的な文字列はすべてエスケープする。
- 表記: 「設定タブ」に統一。文末の句点をそろえる。

### 6.11 セキュリティ上の前提

シートの編集者は設定タブを書き換えて任意のタブを公開でき、バインドされたスクリプトも書き換えられる（書き換えたコードは管理者のトリガー＝管理者のトークンで動く）。したがって**編集者は信頼できる人に限り、設定タブは保護し、GitHub は Sheet Board 専用（運営の共用）アカウントを使う**ことを README で求める。

## 7. エディター

### 7.1 パッケージに含めるメニュー（`Tools/TenteEEEE/Sheet Board/`）

- `Install Sample into Current Scene`: Source 1 つと Display 1 つを配置して配線する。既に Source があれば Display だけ追加し、既存の Display と重ならないよう X 方向にずらし、選択状態にしてログを出す。TMP Essentials が無ければダイアログで案内して中止する。prefab が無ければダイアログで中止する（自動ビルドしない）。
- メニューは再生中は無効にする（validator）。
- 同梱の prefab・マテリアル・フォント・program asset はビルド済みで、利用者は Build を実行しない。

### 7.2 Source のインスペクター

- `baseUrl` は前後の空白を除いて使う。
- 「URL を適用」: `urls` を `baseUrl + ?v=0..N−1`（既に `?` があれば `&v=`）で作り直す。`N = clamp(ceil(300 / max(30, refreshInterval)) + 1, 4, 64)`。`baseUrl` が空なら `urls` を空にする。
- 「接続を確認」（非同期、`UnityWebRequest`、`redirectLimit = 0`、User-Agent 付き）: 信頼リスト（config の `stringHostUrlList`、失敗時は内蔵の既定リスト）、HTTP 200 でリダイレクトでないこと、`sheetboard/1` であること、gist URL にコミット SHA が無いこと、`http://` でないことを検査し、範囲名の一覧を表示する。確認中はボタンを無効にし、Poll は例外を捕まえて必ず終了処理をする。User-Agent の版数は定数 1 か所で持つ。
- `baseUrl` が空かつ `urls` が空なら「未適用」警告は出さない。
- proxy を書き換える操作（このボタン、§7.3 の「自動で設定」、§7.1 の Install Sample）は、UdonSharp のプログラムが未コンパイルなら先に `UdonSharpCompilerV1.CompileSync` でコンパイルし、それでも未コンパイル（コンパイルエラー等）のときだけ実行せずに「UdonSharp のコンパイルエラーを直してからもう一度実行してください」と表示する。

### 7.3 Display のインスペクター

- `rangeName` の横に、同じ Source の直近の「接続を確認」で得た範囲名を選べるリストを出す。空欄のときは「（空欄 = 最初の範囲）」と添える。
- `source` が空でシーンに Source が 1 つなら「自動で設定」、複数あれば手動指定を促す HelpBox。
- `textScale` はスライダー（0.5〜3）。

### 7.4 開発用ツール（パッケージ外）

`Assets/SheetBoardDev/`（パッケージに含めない）に置く:

- `Build Prefabs` と `Export UnityPackage`（作者が出荷前に実行）。
- `SheetBoardPreview.RenderSampleBatch()`: 配線検証（両 prefab の Udon 側の値を `publicVariables.TryGetVariableValue` で読む、Install Sample、データ前のタイトル）と、サンプル JSON の撮影（`Logs/SheetBoardPreview*.png`）。
- サンプル JSON（`sample.json`、`sample_fidelity.json`）。
- dev 用 asmdef `SheetBoard.Editor.Dev`（`SheetBoard.Editor` は `InternalsVisibleTo("SheetBoard.Editor.Dev")`）。

## 8. prefab

- `SheetBoard Source.prefab`: `SheetBoardSource` のみ。
- `SheetBoard Display.prefab`（ライト、背景 `#FFFFFF`）と `SheetBoard Display Dark.prefab`（ダーク、背景 `#12161C`）。同じレイアウト:
  - ワールド空間 Canvas 1600 × 1000 px、スケール 0.001（1.6 m × 1.0 m）、レイヤー Default、`GraphicRaycaster`・`VRCUiShape`・トリガーの BoxCollider（Canvas の px サイズ × 奥行き 6）。
  - Background（Image）、Title（TMP、上端 90 px）、TableArea（左右 40・上 110・下 80 の余白）とその中の ColumnTemplate / OverlayTemplate / OverlayNoWrapTemplate（RectMask2D の枠 → TMP、非アクティブ）・CellTemplate（Image、非アクティブ）、StatusDot と Status（左下）、RefreshButton（右下、「更新」）。
  - すべての RectTransform はアンカーとオフセットで配置する。ボタン以外は `raycastTarget = false`。
- フォント: 同梱の Noto Sans JP から動的 TMP フォントアセットを作り、`m_ClearDynamicDataOnBuild = true`。出荷時は動的データを空にしておく。

## 9. 制限

- 反映: セル・書式の編集は数秒〜十数秒（5 秒以内の連続編集は次の確認で）。数式の結果だけの変化は最大約 5 分。ワールド側は約 1 分ごとに取得。
- Gist の CDN キャッシュ（300 秒）は URL ローテーションで避けるが、他のクライアントが直前に読んだ同じ URL からは古い版が返りうる（§3.4 の巻き戻り防止で表示は戻らない）。
- 公開した内容は URL を知っていれば誰でも読め、Gist の履歴に残る。URL に GitHub のユーザー名、Gist の説明にスプレッドシート名が入る。
- 再現できないもの: 条件付き書式の色、罫線（代わりに格子）、画像・グラフ・図形、絵文字やフォントに無い記号、フォントの種類、縦位置（中央固定）、プルダウンの ▼。
- Apps Script の無料アカウントのトリガー実行時間は 90 分 / 日。大きな範囲での実行時間は未計測。

## 改訂の経緯（記録）

- 初版設計の後、撮影検証で次を改めた: 文字サイズをシートの pt 基準にした（行高基準ではシートの約 2 倍になった）、縦中央の voffset、`<nobr>` が日本語の折り返しを止めないため折り返しなしのプールを分けた、リニア色空間で低い α が明るく見えるため α を下げた。
- 実機確認で次を改めた: セル先頭の `'` が消える問題に対応した範囲の自前解析、タイムゾーンが空のシートへの対応、「古いデータ」が編集しないだけで点く誤解を生存通知で解消、範囲名が空のときの扱い。
- 公開前監査（2026-11-02）とその再監査で次を改めた: ループの重複防止に許容幅を持たせた（許容幅ゼロではイベントが早く発火するとループが止まる）、管理者交代用の「公開先を作り直す」、 実行者ごとの状態と共有状態の分離（スタッフの操作で管理者の通知が止まる・URL が空になる・Gist の作り直しが繰り返される問題）、例の行を無効化（未確定の範囲が公開される問題）、時刻の検証と巻き戻り防止、行位置の整数化、開発用ツールのパッケージ外への移動。
