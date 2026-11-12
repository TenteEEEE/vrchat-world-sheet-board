# Utsushi 仕様書

この文書が Utsushi の唯一の仕様です。実装と README はこの文書に従います。

**実装状況**: Google Sheets からの公開は 1 スプレッドシートにつき 1 つの URL とし、ワールド内の閲覧権限は各 Source の PermissionController で制御する。

## 1. 目的

Google スプレッドシートの決めた範囲を、**ワールドを再アップロードせずに** VRChat ワールド内の掲示板へ表示する。イベントの当番表・ローテーション表・タイムテーブル・お知らせなど、運営チームが頻繁に書き換える表を想定する。

- 運営スタッフはシートを編集するだけでよい。GitHub を扱うのは管理者 1 人だけ。
- 来訪者は VRChat の設定（Allow Untrusted URLs）を変えなくてよい。
- シートの見た目（色・書式・結合・列幅・行の高さ・配置）をなるべくそのまま再現する。

対象環境: Unity 2022.3、VRChat Worlds SDK ^3.10.0、UdonSharp 1.x、TextMeshPro 3.0.6、Google Apps Script（V8）。

## 2. 全体構成

```text
Google スプレッドシート
  └ Apps Script（シートにバインド。§6）
      ・「Utsushi設定」タブに公開する範囲を名前付きで並べる
      ・編集時・書式変更時・5 分ごとに内容を確認し、変化があれば JSON（§5）を Gist へ PATCH
      ・変化が無くても 60 分に 1 回は生存通知として PATCH
  └ GitHub Gist（URL は固定、中身だけ変わる）
        │ gist.githubusercontent.com は String Loading の信頼ドメイン
        ▼
VRChat ワールド
  UtsushiSource（JSON 1 本につき 1 つ。§3）
      ・エディターで焼き込んだ VRCUrl[]（同じ URL に ?v=0..N-1）を順に使って取得
      ・VRCJson で解析し、最後に成功したデータを保持
  UtsushiDisplay（掲示板の数だけ。§4）
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

## 3. UtsushiSource

`[UdonBehaviourSyncMode(BehaviourSyncMode.None)]`。

### 3.1 Inspector

```csharp
public VRCUrl[] urls;                  // エディター（§7.2）が baseUrl から生成する
public string baseUrl = "";            // 運用者が貼る URL。実行時は使わない
public float refreshInterval = 60f;    // 秒。実効値は max(30, この値)
public float nearRadius = 0f;          // メートル。0 = 距離に関係なく更新する
private const float StaleMinutes = 90f;       // 生存通知がこの時間途絶えたら「止まっています」
private const float ManualCooldown = 5f;      // 手動更新のクールダウン（秒、既定 5）
private const float StartDelayMax = 3f;       // 初回取得をランダムに遅らせる上限（秒）
// 状態行の文言（§3.6）
public UtsushiAudience audience = UtsushiAudience.Public; // 全員 / ワールドスタッフ以上 / ワールド管理者のみ
public UtsushiLanguage language = UtsushiLanguage.Auto; // Auto / Japanese / English
public UdonBehaviour permissionController; // 制限付き閲覧で使う PermissionController
// 以下の文言フィールドは空欄が既定。§3.6 の表示言語別文言で補う
public string permissionDeniedText, permissionWaitingText;
public string urlMissingText, loadingText, waitingNearText, failedText, freshText, staleText, retryText;
public string refreshingSuffix; // 成功済みの状態行の末尾に付ける
public string minutesUnit, hoursUnit, daysUnit;
```

Source Inspector では表示言語を `Auto` / `Japanese` / `English` から選び、`permissionDeniedText`、`permissionWaitingText` を含む状態表示文言を上書きできる。Auto は閲覧者の `VRCPlayerApi.GetCurrentLanguage()` が `ja` で始まるとき日本語、それ以外は英語。文言フィールドが空、または従来の日本語既定値と完全一致する場合は、表示言語の既定文言を使う。独自の値はそのまま表示する。置き換え記号は `{code}` = エラー、`{s}` = 秒、`{time}` = 更新時刻、`{ago}` = 経過時間。

### 3.2 取得スケジュール

時間はすべて `Time.realtimeSinceStartup`（以下 now）。`SendCustomEventDelayedSeconds` で 1 秒ごとの `_Tick` を回す（`Update` は使わない）。

- **ループの維持**: 遅延イベントは引数を持てないので、次の予定時刻 `nextTickAt` で重複ループを止める。`_Tick` は `now < nextTickAt − 0.5 × 周期` なら何もせずに終わり（重複した古いループが消える）、それ以外は `nextTickAt = now + 周期` として再スケジュールしてから処理する。許容幅を持たせるのは、遅延イベントが予定よりわずかに早く発火してもループが止まらないようにするため。`OnEnable` では、ループが止まっているとき（`now > nextTickAt + 2 × 周期`、または未開始）だけ新しくスケジュールする。世代番号は使わない。
- **初回**: 最初の有効化で `nextFetchAt = now + Random(0, StartDelayMax)`、URL の添字を `Random(0, urls.Length)` から始める。
- **成功**: `nextFetchAt = now + max(30, refreshInterval) × Random(1.0, 1.1)`。
- **失敗**（読み込みエラー、または JSON が §5 として不正）: `failCount` を増やし、`min(30 × 2^(failCount−1), 300)` 秒 × Random(0.8, 1.2) 後に再試行。成功で `failCount = 0`。
- **近接条件**: `nearRadius > 0` のとき、直近 `max(2, 2 × 報告された nearCheckInterval)` 秒以内に Display から `_ReportNear()` が無ければ取得しない。近づいた時点で `nextFetchAt` を過ぎていれば、その `_Tick` で取得する。
- **多重取得の禁止**: 取得中は次の取得を始めない。60 秒コールバックが来なければ失敗扱いにする。取得に使った `VRCUrl` を保持し、コールバックの `result.Url` と一致しない（タイムアウト後に遅れて届いた）結果は無視する。
- **URL のローテーション**: 取得ごとに添字を 1 進める。null または空の URL は飛ばす。有効な URL が 1 つも無ければ取得しない（状態は「URL 未設定」）。有効な URL の有無は最初の有効化で 1 度だけ数えてキャッシュする。
- タイムアウトした URL は `2 × Timeout`（120 秒）退役し、その間は再利用しない。退役中に遅れて届いた結果は無視する。すべての URL が退役中なら取得を開始せず、最も早い退役期限に次回取得を予定する。固定の「URL 待機中」エラーには置き換えず、手動更新中の表示も解除する。期限を過ぎた URL は再び利用できる。権限剥奪で退役した URL にも同じ期限を適用する。

### 3.3 手動更新

`public void RequestRefresh()`: ローカルのみ。有効な URL が無い、権限が無い、取得中、前回の受付から `ManualCooldown` 秒未満なら無視。受け付けたら近接条件とバックオフを無視して次の `_Tick` で取得し、状態行を再計算する。

`public float GetRefreshWaitSeconds()`: 権限なし・有効な URL なし・取得中は `-1`。手動更新のクールダウン中は残り秒数、受付可能なら `0`。

### 3.4 取得結果の処理

`public bool ApplyJson(string json)`（エディターの検証からも直接呼ぶ。成功で true）。

1. `json` が null か空なら失敗。
2. `VRCJson.TryDeserializeFromJson` でルートが `DataDictionary`、`format == "sheetboard/1"`（文字列）、`rev` が文字列、`updatedUnix` が数値、`ranges` が `DataDictionary` であること。数値は Int / Long / Double のどれでも受ける。
3. **時刻の検証**: `updatedUnix` は `0 < x < 253402300799`（西暦 9999 年末まで）。範囲外なら失敗。`aliveUnix`（任意）は範囲外・非数値なら 0 として扱う。範囲外の値で `DateTime` が例外を投げると Udon の behaviour が止まるため、必ずここで弾く。
4. **巻き戻り防止**: 保持中の `updatedUnix` より古い `updatedUnix` が来たら（CDN に残った古い版）、データは差し替えずに成功として扱う。
5. `rev` が保持中と同じならデータは差し替えない。違えば `ranges`・`rev`・`updatedUnix` を保存し、`revision`（int）を 1 増やす。
6. `aliveUnix` は手順 4・5 の結果にかかわらず `max(保持値, 新しい値)` に更新する。

任意の最上位 `order` は、範囲名の文字列だけからなる配列で、各要素が `ranges` に存在する場合に採用する。`GetFirstRangeName()` は有効な `order` の先頭を返す。`order` が無い、または不正なら `ranges` 辞書の順に戻る。

失敗時は**保持データを決して消さない**。

閲覧権限の評価は、`OnEnable`、1 秒ごとの `_Tick`、`RequestRefresh`、取得開始直前、取得成功・失敗コールバック、`ApplyJson` で行う。`HasData`、`GetRange`、`GetFirstRangeName`、`GetAccessStatus`、`GetRefreshWaitSeconds` などの取得用メソッドは権限を再評価せず、キャッシュ済みの判定を返す。

### 3.5 公開メソッド（Display 用）

UdonSharp では他の behaviour のプロパティを呼ばず、`out` 引数も使わないので、すべて戻り値のあるメソッドにする。

```csharp
public int GetRevision();
public bool HasData();                         // 一度でも取得に成功したか
public DataDictionary GetRange(string name);   // 無ければ null
public string GetFirstRangeName();             // order の先頭、無ければ ranges の最初のキー。無ければ ""
public string GetStatusText();                 // キャッシュ済みの文字列を返すだけ
public int GetStatusLevel();                   // 0 = 正常, 1 = 止まっている/再試行中, 2 = エラー/未取得, 3 = 準備中
public void _ReportNear(float checkInterval);  // Display から
public void RequestRefresh();
public bool IsBusy();                          // 権限と有効な URL があり、取得中または手動更新待ち
public float GetRefreshWaitSeconds();          // 押せない: -1、待機中: 残り秒数、受付可: 0
public bool ApplyJson(string json);
```

状態行の文字列とレベルは **`_Tick` で 1 秒に 1 回だけ計算してキャッシュ**する（Display が何枚あっても計算は 1 回）。`ApplyJson` の直後にも再計算する。

状態レベルは 0 = 正常、1 = 注意（再試行中・自動公開停止）、2 = エラー（閲覧権限なし・URL 未設定・一度も読み込めずに失敗）、3 = 準備中（権限確認中・読み込み中・近づくと読み込みます）とする。レベル 3 のランプは Display の `busyColor` を使う。

### 3.6 状態行

| 状況 | level | 日本語の既定文言 | 英語の既定文言 |
| --- | --- | --- | --- |
| 閲覧権限なし | 2 | `閲覧権限がありません` | `You don't have permission to view this` |
| 権限確認中 | 3 | `権限を確認中…` | `Checking permission…` |
| URL 未設定 | 2 | `URLが設定されていません` | `No URL is set` |
| 初回取得中（まだ読み込みに失敗していない） | 3 | `読み込み中…` | `Loading…` |
| 近接待ちで一度も取得していない | 3 | `近づくと読み込みます` | `Come closer to load` |
| 一度も読み込めずに失敗（再試行待ち） | 2 | `読み込めません・{s}秒後に再試行（{code}）` | `Couldn't load · retrying in {s}s ({code})` |
| 成功・正常 | 0 | `更新 {time}（{ago}）` | `Updated {time} ({ago})` |
| 成功・生存通知が `StaleMinutes` 超過 | 1 | `更新 {time}（{ago}）・更新が止まっている可能性があります` | `Updated {time} ({ago}) · may be out of date` |
| 成功済みで直近が失敗 | 1 | `更新 {time}（{ago}）・再試行中` | `Updated {time} ({ago}) · retrying` |
| 成功済みの状態行に付く更新中表示 | — | `・更新中…` | ` · updating…` |
| 経過時間（1分未満） | — | `たった今` | `just now` |
| エラーの種類 | — | `時間切れ` / `データの形式が不正` / `HTTP nnn` | `timed out` / `invalid data` / `HTTP nnn` |
| 公開範囲0件 | — | `表示する範囲がまだありません` | `No range has been published yet` |
| 大きい表 | — | `・表が大きく文字が小さくなっています` | ` · table is too large to read comfortably` |
| 経過時間（分） | — | `分前` | ` min ago` |
| 経過時間（時間） | — | `時間前` | ` h ago` |
| 経過時間（日） | — | `日前` | ` d ago` |
| 範囲なし（Display） | — | `範囲「{name}」が見つかりません` | `Range "{name}" was not found` |
| Source 未設定（Display） | — | `データ元が設定されていません` | `No data source is set` |
| 更新ボタン（Display） | — | `更新` | `Refresh` |

- 「止まっています」の判定は `aliveUnix > 0 かつ 現在 − aliveUnix > StaleMinutes`。`aliveUnix` が 0（旧形式）のときは判定しない。
- `{time}` は `updatedUnix`（内容が最後に変わった時刻）を**閲覧者の現地時刻**にした `HH:mm`。`Hour.ToString("00") + ":" + Minute.ToString("00")` で組む（書式文字列の `:` はカルチャ依存のため）。
- `{ago}` は現在と `updatedUnix` の差。1 分未満は `たった今` / `just now`、60 分未満は `n + minutesUnit`、24 時間未満は `h + hoursUnit`、それ以上は `d + daysUnit`。現在時刻はローカルプレイヤーが有効なら `Networking.GetNetworkDateTime()`、そうでなければ `DateTime.UtcNow`。
- 一度も成功していない間の取得中は `{s}` を出さず `loadingText`（level 3）。初回成功前に手動更新待ちの場合も `loadingText` を出す。
- 成功済みの取得中は level を変えず、直前の状態行の末尾に `refreshingSuffix` を追加する（定期取得のたびに赤くしない）。
- 文字列の組み立てに `string.Format` / 補間は使わず、`+` と `Replace` を使う。

## 4. UtsushiDisplay

`[UdonBehaviourSyncMode(BehaviourSyncMode.None)]`。

### 4.1 Inspector

```csharp
public UtsushiSource source;
public string rangeName = "";          // 空なら最初の範囲（§4.6）
public bool lightTheme = true;
public bool showTitle = true;
public bool showGrid = true;
public float gridWidth = 1f;           // px
public bool zebra = false;
public float textScale = 1.4f;         // 0.5〜3
public bool autoFitColumns = true;     // 列幅と文字サイズを自動調整
public float maxRowHeight = 64f;       // px
public float columnGap = 12f;          // px。showGrid が OFF かつ colw を使わないとき有効
public bool chipAutoPalette = true;    // チップを名前ごとに自動配色
public Color[] chipPalette;             // 自動配色パレット（10 色）
public float nearCheckInterval = 1f;   // 秒。0.25〜1.5 にクランプ
// 色（α 0 なら lightTheme に応じた既定値で埋める）
public Color boardColor, bodyColor, gridColor, headerBackgroundColor, zebraColor, chipColor, chipArrowColor, normalColor, staleColor, errorColor, busyColor, refreshButtonColor, refreshLabelColor, refreshDisabledColor, refreshDisabledLabelColor;
public string rangeMissingText = "";
public string sourceMissingText = "";
public string refreshIdleText = ""; // 空欄なら表示言語の既定文言
// installer が配線する内部参照（[Header] で区切る）
public Image background;
public TMP_Text titleText, statusText;
public RectTransform tableArea;
public TMP_Text columnTemplate, overlayTemplate, overlayNoWrapTemplate;
public Image cellTemplate, chipTemplate, arrowTemplate, statusDot;
public Button refreshButton;
public TMP_Text refreshLabel;
```

TMP 参照は `TMP_Text` 型にする（`TextMeshProUGUI.fontSize` の setter は Udon 非公開）。
Source の 13 個と Display の 3 個、合計 16 個の文言フィールドは上書き用。Display Inspector では `rangeMissingText`、`sourceMissingText`、`refreshIdleText` を編集できる。16 個の各フィールドは、空欄または従来の日本語既定値と完全一致する場合に表示言語の既定文言を使い、それ以外は入力値を使う。

| 色 | ライト（既定） | ダーク |
| --- | --- | --- |
| boardColor | `#FFFFFF` | `#12161C` |
| bodyColor | `#202124` | `#E8EDF2` |
| gridColor | `#E2E3E3` | `#FFFFFF` α 0.08 |
| headerBackgroundColor | `#0F766E` α 0.08 | `#5EC4BA` α 0.12 |
| zebraColor | `#000000` α 0.025 | `#FFFFFF` α 0.015 |
| normalColor | `#1E8E3E` | `#81C995` |
| staleColor | `#B06000` | `#FDD663` |
| errorColor | `#D93025` | `#F28B82` |
| busyColor | `#1A73E8` | `#8AB4F8` |
| refreshButtonColor | `#0F766E` | `#1F6F69` |
| refreshLabelColor | `#FFFFFF` | `#F1F5F7` |
| refreshDisabledColor | `#E3E6E8` | `#252B33` |
| refreshDisabledLabelColor | `#56616B` | `#8B96A1` |
| chipColor | `#E8EAED` | `#3C4043` |
| chipArrowColor | `#5F6368` | `#9AA0A6` |

`chipPalette` の既定値（順番どおり 10 色）は、ライトが `#F8D7DA`, `#FDE2C0`, `#FFF1B0`, `#D6F0CF`, `#CBEDE7`, `#D2E4FB`, `#E1DAF6`, `#F6D6EA`, `#E8E1D2`, `#D8E0E7`、ダークが `#7A2E3A`, `#7A4A16`, `#66570F`, `#2F5E32`, `#1F5C56`, `#24497A`, `#4A3B7A`, `#6E2E5C`, `#5A4E3A`, `#3C4A57`。`chipAutoPalette` は既定で ON。

### 4.2 描画の構造

兄弟順・描画順（奥から）: 格子の下地 → セル矩形 → ChipLayer → ArrowLayer → 列 TMP・オーバーレイ。チップと ▼ はそれぞれテンプレートから複製するプール。各プールは必要になった分だけ遅延生成し、不足時に伸ばす。表はデータの全行・全列を描画する。表が大きいほど文字は小さくなり、処理は重くなる。

- **列 TMP**: 列ごとに行ブロック単位のセグメントを使う。`columnTemplate` の親（RectMask2D の列枠）ごと複製する。セルは改行区切りで並べ、1 セグメントの予算は 6,000 文字。各セグメントの先頭に透明な M のダミー行を置いて縦中央を補正する。テンプレートは折り返し OFF・overflow Overflow・richText ON・margin 0。
- TMP 3.0.6 は 1 つのテキストが 16,383 文字を超えても末尾を捨てず、`new Material` で複製したマテリアルを使う子メッシュに溢れた文字を描く（`TMPro_UGUI_Private.cs` の「Limit the mesh of the main text object to 65535 vertices and use sub objects for the overflow」）。そのマテリアルは再描画ごとに作られ、バッチも分かれる。セグメント化はこのオーバーフロー経路を避けるために行う。
- **セル矩形**: `cellTemplate` から必要になった分だけ遅延生成し、不足時に伸ばす。格子の下地も `cellTemplate` から 1 枚作る。
- **チップ・▼**: `chipTemplate` と `arrowTemplate` から作り、同じ添字のチップと矢印を組にして再利用する。ChipLayer はセル矩形より手前、ArrowLayer は ChipLayer より手前、列 TMP とオーバーレイは両レイヤーより手前に置く。
- **オーバーレイ**: 折り返しあり（`overlayTemplate`）と折り返しなし（`overlayNoWrapTemplate`）の 2 プール。どちらも必要に応じて遅延生成し、不足時に伸ばす。文字が空のセルにはオーバーレイを使わない。
- 生成スプライトはチップが 64 × 64 px（border は四辺 31 px）、▼ が 64 × 40 px（border 0 px）。
- すべてのプールは遅延生成し、以後は SetActive で再利用する。行数・列数・オーバーレイ数による描画上限は設けない。
- Udon で使えない API: `TMP_Text.transform / gameObject / rectTransform` → `GetComponent<RectTransform>()` を使う。`richText / enableWordWrapping / overflowMode / margin` の setter → テンプレート側で設定する。使える setter は `text / color / fontSize / alignment`。

### 4.3 前処理（1 回の走査）

描画の最初にセルを 1 回だけ走査し、以後はこの配列だけを読む（DataList の参照を繰り返さない）。

- セルごと: 加工前の文字列、ASCII 文字数と非 ASCII 文字数、pt、style ビット、文字色（有無と Color）、背景色（有無と Color）、横配置（int 0 左 / 1 中央 / 2 右）、wrap、チップの種別、チップ背景色、チップ文字色。
- 結合: `covered[]`（結合で覆われたセル）と、起点セルの行数・列数。範囲外は切り詰め、重なる結合と 1×1 の結合は捨てる。
- 基準 pt: 異なる pt 値を最大 16 種類まで数えて最頻値を取る（線形時間）。

### 4.4 レイアウト

- 列幅: 手動の cols があればその比。手動指定がなく autoFitColumns が ON（既定）の場合、文字が収まる最低幅を確保し、余りをシート列幅 colw の比、または colw がなければ必要幅の比で配る。autoFitColumns が OFF なら従来どおり colw、または最長セル幅（ASCII 1・その他 2、2〜40 にクランプ）の比を使う。配列の不足・0 以下の値は有効値の中央値で補う。
- 列幅の見積もり: 1em あたり (ASCII 数 × 0.55 + 非 ASCII 数) × サイズ倍率 × 太字係数 × 1.05。サイズ倍率は <size%> と同じ 0.6〜1.6、太字係数は 1.06（太字または見出し行）。表示可能なセル文字を数え、空き列は 0.6em とする。結合セル・折り返しセル・改行を含むセル・右側が行末まで空（値なし・背景色なし・結合なし）の左寄せセルは列の必要幅に含めない。途中に値などがある行の左寄せセルは必要幅に含める。
- チップのセルは列の必要幅に `ChipExtraEm`（1.3em）を加える。チップのセルは、はみ出しオーバーレイの候補にしない。
- 列間: autoFitColumns の自動列幅では showGrid が ON なら 0、OFF なら columnGap。それ以外は従来どおり showGrid または colw 使用時は 0、それ以外は columnGap。
- 行の高さ: rowh があればその比（足りない・0 以下は有効値の中央値、無ければ 21）、無ければ全行同じ。倍率 k = min(表領域の高さ / Σrowh, maxRowHeight / median(rowh))。
- 整数化: 行の上端 Y_r = round(累積)、高さ h_r = Y_{r+1} − Y_r として整数 px にそろえ、セル矩形と <line-height> の両方で同じ値を使う（丸め誤差の累積でずれないように）。列も同様。
- 基準の文字サイズ: preferred は従来の size・rowh・textScale による計算値。最終値は preferred、0.70 × 整数化した行高の中央値、横幅の上限の最小値。横幅の上限を適用する場合も、0.5 × min(preferred, 行高の上限) を下限とする。下限に達した場合の横方向の切れは許容する。autoFitColumns が OFF かつ手動 cols なしでは横幅の上限を適用しない。
- 行末まで右側が空のため必要幅から除外したセルも、autoFitColumns が ON で手動 cols がない場合は横幅の文字サイズ制約に含める。そのセルの幅は、左列を最低幅にしたとき表の右端までに残る幅で見積もる。
- 手動列幅と余白: cols があるときは幅比を維持して必要に応じて文字を縮小する。自動列幅では文字列のある列に左右 6px の余白を加え、空き列には加えない。文字が収まった後の余りを列幅比で配り、必要幅の合計が表領域を超える場合は必要幅の比で縮める。
- セル内パディング: 左右 6px。列 TMP もオーバーレイも枠の内側へ 6px 寄せる。はみ出し判定は共有する見積もり幅と「列幅 − 12」を比べる。

### 4.5 セルの文字列

1. 値を文字列化（数値なら `ToString()`）。
2. 列 TMP では改行を空白 1 つに置き換える（オーバーレイでは改行を残す）。
3. 1 セルに表示する文字は 4,000 文字まで。超える場合は末尾を `…` に置き換えて表示する。
4. **エスケープ**: `<` は `<noparse><</noparse>` に置き換える。タイトルにも同じ処理（改行→空白、4,000 文字、エスケープ）を適用する。
5. 書式: style ビット（1 太字、2 斜体、4 下線、8 取り消し線）を `<b><i><u><s>` で囲む。見出し行（`header`）は太字。`size` があれば `size / 基準pt` を 0.6〜1.6 に収めた倍率を `<size=NN%>` で付ける（100% は付けない）。
6. 文字色（コントラスト補正）を `<color=#RRGGBB>` で付ける:
   - 文字色が無く背景色があるとき: 背景の輝度 `0.2126R + 0.7152G + 0.0722B` が 0.5 以上なら `#202124`、未満なら `#FFFFFF`。
   - どちらも無いとき: `bodyColor`。
   - 文字色があり背景色が無いとき: ダークで輝度 0.25 未満、またはライトで 0.85 超なら `bodyColor`。それ以外はその色。
   - 背景色があるときの明示色はそのまま。

各列 TMP セグメントの先頭にダミー行を置く。`<line-height=0>`、透明な `M`、基準サイズの V0 を使い、各セグメント先頭の行位置を合わせる。透明化は閉じタグのない `<alpha=#00>` で行い、後続文字の前に `<alpha=#FF>` を付けて戻す。TMP 3.0.6 に `</alpha>` はない。各セル行の先頭には `<line-height=h_r>`（行の高さが全部同じなら先頭 1 回）、`<align=left|center|right>`、縦中央用の `<voffset=-offset>`…`</voffset>` を付ける。`offset = h_r/2 − 0.78 × b + 0.38 × (f − b)` で、`b` は基準文字サイズ、`f` はセルの実効文字サイズ。`|offset| < 1` なら `<voffset>` は付けない。結合で覆われたセルとオーバーレイで描くセルは空行にする（タグは付けて行位置を保つ）。

チップセルの文字色は、チップ文字色、コントラスト比 3.0 以上を満たすセル文字色、自動文字色の順で選ぶ。

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

次のセルは、文字が空でない場合にオーバーレイで描き、列 TMP の該当行は空にする。

1. 結合の起点（1×1 を除く）。`wrap` なら折り返しありプール、それ以外は折り返しなしプール。
2. `wrap == 1` または改行を含むセル。折り返しありプール。
3. はみ出し: `wrap` でなく左寄せで、共有する幅見積もり EmWidth(i) × basePx（ASCII 0.55em、その他 1em、<size%> 倍率、太字係数 1.06、余裕 1.05 を含む）が「列幅 − 12」を超え、右隣が空（値なし・背景色なし・結合なし）。右へ続く空セルの終わりまでを枠にする。折り返しなしプール。

既知の横方向のはみ出し判定は、列 TMP で描いたセルに加えて、行末まで右側が空のセルを折り返しなしオーバーレイで描いた場合にも適用する。結合・折り返しセルは対象外。

プールが尽きた・テンプレートが null のときは列 TMP に 1 行で描く。オーバーレイは縦中央、横は配置どおり、文字サイズ `basePx`。

### 4.9 チップ

- 種別 2 のチップの寸法: 左右の余白 `ChipMargin = 3px`、高さ `min(セル高 − 2 × 3px, round(f × ChipHeightEm))`、`ChipHeightEm = 1.55`。幅はセル幅から左右の余白を引く。チップセルの必要幅には `ChipExtraEm = 1.3em` を加える。
- チップは 64 × 64 px、四辺の border が 31 px のスライス画像で、ピル形に伸縮する。高さに合わせた倍率で描画する。
- ▼ はセル内で縦中央に置く。チップ内では右端から `0.45em` 内側、矢印だけのセルではセル右端から `6px` 内側に置く。幅は `0.62em`、高さは `0.36em`。▼ の画像は 64 × 40 px、border は 0。
- 塗り色の優先順位は、参照元セルのチップ背景色 → 自動配色 → `chipColor`。
- 自動配色は表示値の前後の空白を除き、内部の全角空白を取り除いた文字列で名前を比較する。大文字・小文字は区別する。空欄、または空白と `- ‐ ‑ ‒ – — ― ー － ・ / ／` だけの値は名前にしない。表を上から左から走査し、初出の名前ごとにパレットを順番に割り当て、パレットの末尾の次は先頭に戻る。同じ名前には同じ色を使う。既定パレットは §4.1 のライト・ダーク各 10 色。
- 種別 1（矢印のみ）の ▼ は、チップ文字色があればそれを使う。そうでなければセル文字色が背景色（背景指定がなければ `boardColor`）とのコントラスト比 3.0 以上のときにその色を使い、それ以外は `chipArrowColor` を使う。

### 4.10 監視ループ

`_Watch` の状態更新ごとに、`source.IsBusy()` が true なら `busyColor` のランプを α 1 と 0.35 で交互に切り替える。busy でなければ従来の level 色を使う。別の遅延イベントは作らない。

アクセス不可・Source 未設定の表示は従来どおり。アクセス可能なら状態行は `source.GetStatusText()` を使う。

`_Watch` も §3.2 と同じ方法（予定時刻と許容幅、止まっているときだけ `OnEnable` で再開）でループを維持する。最初に再スケジュールしてから:

1. `source` が null なら状態行に `sourceMissingText`。
2. `nearRadius > 0` かつローカルプレイヤーが有効で、距離が `nearRadius` 以下なら `source._ReportNear(nearCheckInterval)`。
3. `source` の参照か `GetRevision()` が前回の描画と違えば描画する。
4. 状態行と状態ランプを更新する（文字列が同じなら代入しない）。

`public void RenderNow()`（現在のデータで強制的に描画）と色・プールの初期化は、`Start` を経ないエディターの C# から呼んでも動くこと。テンプレートが欠けているときは初回に 1 度だけ `Debug.LogWarning` を出す。

### 4.11 手動更新ボタン

ボタンの onClick は Display の `OnRefreshPressed()` を呼び、Display が `source.RequestRefresh()` を呼ぶ。押した直後にも状態行・ランプ・ボタン表示を更新する。

取得中と手動更新のクールダウン中は `GetRefreshWaitSeconds()` に従ってボタンを無効化する。ラベルは常に `refreshIdleText` を表示する。押せない間はボタンの塗りと文字色で示す。ボタンの ColorBlock の disabled は白で、無効時の色に乗算による減衰を加えない。

## 5. JSON 形式 `sheetboard/1`

このデータ形式識別子は互換のため旧名のまま残しています。

| 旧名で残している識別子 | 理由 |
| --- | --- |
| Gist ファイル名 `sheetboard.json` | 配布済みワールドの URL |
| `format: "sheetboard/1"` | ワールド側の照合 |
| Properties のキー | 設置済みシートとの互換性 |
| トリガーのハンドラ名 | 設置済みトリガーとの互換性 |

Apps Script が出力し、Source が読む。UTF-8。

```json
{
  "format": "sheetboard/1",
  "rev": "3f2a9c1e",
  "updated": "2026-11-02T12:34:56+09:00",
  "updatedUnix": 1793590496,
  "aliveUnix": 1793594096,
  "order": ["rotation"],
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
| `order` | 文字列配列 | 任意 | 範囲名を設定タブ順に並べる。Source はこれを優先し、無い場合だけ `ranges` 辞書の順に戻る |
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
| `chip` | 文字列の 2 次元配列 | チップ種別と任意の背景色・文字色。`a` = 矢印のみ、`c` = チップと矢印、`c|背景色|文字色` = 色を含むチップ（色が無い側は空欄） |
| `colw` / `rowh` | 数値配列 | 列幅 / 行の高さ（px）。`colw` は F 列が空のとき |
| `merges` | 配列の配列 | `[r, c, rowSpan, colSpan]`（出力後の 0 始まりの番号） |

ランタイムは、任意キーが無くても、配列の長さが `rows` と合わなくても例外を出さずに動くこと。最上位の `order` は設定タブ順を保持する任意キーで、無い場合は `ranges` 辞書の順に戻る。旧 0.1.0 ランタイムは未知の最上位キー `order` を無視し、既存の JSON と同様に `ranges` を読むため互換性がある。全セルが既定値なら `style`、`color`、`bg`、`ha`、`wrap` の書式配列は省略されうる。`chip` は「書式を反映」が TRUE のときだけ出力し、全セルが空なら省略する。旧ランタイムは未知の範囲キー `chip` を無視するため、互換性がある。`chip` の配列は表示値の切り詰めに影響しない。0.1.0 は書式配列の無いキーを空値として扱い、既定値と同じ表示になる。`size` は空値で表示倍率が変わるため省略しない。旧形式の `bold`（0/1 の 2 次元配列）は `style` が無いときだけ読む（Apps Script は出力しない）。

## 6. Apps Script（`AppsScript/`）

配布物は `Utsushi.gs` と `appsscript.json`。シートの「拡張機能 → Apps Script」に入れる（スタンドアロンのプロジェクトでは動かない）。デプロイは不要。

### 6.1 状態の保存先

| 保存先 | 中身 |
| --- | --- |
| UserProperties（実行者ごと） | GitHub access token、refresh token、各トークンの期限、認証方式、再認証フラグ、ログイン名、Device Flow の device_code |
| DocumentProperties（シートで共有） | gistId、**Gist の所有者のログイン名**、**ワールドに貼る URL**、前回の rev、updatedUnix、最終公開時刻、直近のエラー、メール送信時刻（種類ごと）、警告、URL 変更のお知らせ、保留中の公開フラグ |

### 6.2 メニュー

| 項目 | 動作 | 誰が使うか |
| --- | --- | --- |
| セットアップ・修復 | §6.4 のウィザード。セットアップ済みで実行者に連携情報が無ければ、GitHub 接続の前に確認画面を出す | 管理者 |
| 選択範囲を掲示板に追加 | 選択中の表を設定タブへ追加（`addSelectionToBoard()`） | 誰でも |
| 今すぐ公開 | 実行者がトークンを持っていれば強制公開。持っていなければ公開せず、保留フラグを立てて「受け付けました。数分以内に自動で公開されます。操作は不要です。」と表示 | 誰でも |
| 状態とURLを表示 | DocumentProperties の URL・最終公開・状態ブロック・警告を表示。エラーは状態ブロックで案内する | 誰でも |
| 管理者用 → GitHub連携をやり直す | トークンを取り直し、トリガーが無ければ設置し直す | 管理者 |
| 管理者用 → 公開先を作り直す | §6.5 の管理者交代用。理解チェック後、新しい Gist（新しい URL）を作る | 管理者 |
| 管理者用 → 連携を解除 | 自分のトリガーと Utsushi の認証情報を削除（Gist は消さない） | 管理者 |

トークンを持たない実行者が公開処理を起こしても、共有のエラー状態・メール抑止には一切書き込まない。
「公開先を作り直す」は、旧 URL の掲示板が更新されなくなり再アップロードが必要なこと、管理者交代時だけ使うことを説明する。利用者が「上の内容を理解しました」にチェックするまで実行ボタンを無効にする。

セットアップ済みのシートで管理者以外が「セットアップ・修復」を押した場合は、現在の所有者と「スタッフの方は操作不要です。シートを編集するだけで掲示板に反映されます」を表示する。［閉じる］と［管理者を引き継ぐので続ける］を出し、後者を選んだときだけ連携フローを始める。

- セットアップ前の追加・公開操作では「まだセットアップされていません。管理者が Utsushi → セットアップ・修復 を実行すると公開されます。」と案内する。
- 範囲 0 件の確認で「いいえ」の場合は「公開を取りやめました。掲示板は変わっていません。」と表示する。連携解除後にスタッフが公開すると「管理者がGitHub連携を解除したため、自動公開は止まっています。新しい管理者が Utsushi → セットアップ・修復 を実行してください。」と表示する。

### 6.3 設定タブ「Utsushi設定」
- 設定タブは `Utsushi設定` → 旧名 `SheetBoard設定` → `settingsSheetId` の順で探索し、見つかったタブは新しい名前へ付け直す。

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
| H | 状態 | 行ごとの状態。スクリプトが書き込み、利用者は編集しない |
| J1:J4 | 全体の状態 | 全体の状態、次にすること、内容の更新時刻。スクリプトが書き込み、利用者は編集しない |

1 枚のスプレッドシートからは 1 つの JSON / Gist / URL を公開する。閲覧権限によって異なるデータが必要な場合は、スプレッドシートと Source / Display を分ける。

- セットアップ時に無ければ作り、例の行として名前 `#sample`（無効）、範囲 `<設定タブ以外の左端のシート名>!A1:F20`（クォートなし）、見出し FALSE、書式 TRUE を入れる。**例の行は `#` を消すまで公開されない**（範囲を決める前に公開されて Gist の履歴に残るのを防ぐ）。
- 範囲は自前の `parseRangeRef(text)` → `{sheet, a1}` で解析する。受け付ける形: `シート名!A1:C10`、`'シート名'!A1:C10`（`''` は `'`）、先頭の `'` が消えた `シート名'!A1:C10`、`A1:C10`（シート名省略 = 設定タブ以外の左端）。区切りは最後の `!`（セル先頭の `'` はスプレッドシートが文字列扱いの合図として消すため、この寛容さが必要）。
- **行ごとの誤りはその行だけ飛ばす**（警告に「「名前」: シート「X」が見つかりません」「「名前」: 範囲「A1:C10」を読めません」を記録）。1 行の誤りで全体の公開を止めない。
- 名前の重複は先の行を採用して警告。範囲数に上限は設けない。
- 範囲は API を呼ぶ前に、シートの最終行・最終列で切り詰める（`A:F` のような列全体の指定でもシートに存在するセルだけを読む）。行数・列数の固定上限は設けない。
- H 列の文言は次のとおり。名前と範囲が両方空なら空欄。名前が空で範囲がある場合は `⚠ 名前（A列）が空のため公開されません`。`#` で始まる名前は `非公開（名前の先頭が # のため）`。重複は `⚠ 同じ名前が上の行にあるため使われません`。読み取りエラーは `⚠ ` に利用者向けエラー文を続ける。正常な範囲は `✔ 掲示板に表示されます`。5,000 セルを超える場合は大きな範囲の注意を付ける。範囲に文字が無い場合は `⚠ 範囲に文字が入っていません`。選択範囲追加後、実行者が公開できない場合は `⏳ 管理者の自動公開を待っています`。注意が複数ある場合は ` ／ ` で続ける。F/G の書き間違いは該当行の状態と共有警告の両方に記録する。
- フィルタのあるシートだけフィルタによる非表示判定を行う。非表示の行と列、およびフィルタで除外される行を除いてから値・書式を出力する。
- J1:J4 は順に `Utsushi の状態`、`overallStatus_()` のアイコンと文言、次にすること（無ければ空欄）、更新時刻。J4 は `updatedUnix` をシートのタイムゾーンで `掲示板の内容の更新: yyyy/MM/dd HH:mm` と表示する。未公開なら `まだ公開していません`。定期確認時刻は書かない。
- 状態書き込みは `writeSheetStatus_()` に集約し、H 列と J1:J4 だけを対象にする。H1 が空なら `状態` を書く。現在値と違う場合だけ更新し、保護や権限による失敗は無視して公開を失敗させない。公開成功・失敗・内容変更なしの早期終了、`setPending_()`、`finishSetup`、`disconnectConfirmed`、`addSelectionToBoard()` から呼ぶ。トークンが無い `publish_` の早期終了では呼ばない。`onEditInstalled(e)` と `onChangeInstalled(e)` は、設定タブの H 列以降の編集を無視する。イベント引数や `e.range` が無い場合は従来どおり処理する。
- 公開に成功した範囲名は UTF-8 で 8 KB 以内の `publishedNames`（JSON 配列）として DocumentProperties に保存する。保存量を超える場合は一部を省き、状態画面に省いた件数を表示する。
- 設定タブを新規作成するときだけ、1 行目を固定し、A1:G1 に各列の書き方と例をメモとして付け、H 列を約 300 px、J 列を約 420 px にする。F・G 列（列幅の比・配置）は列グループにして折りたたむ。既存シートの書式は変更しない。

#### 6.3.1 全体の状態 `overallStatus_()`

上から順に最初に当てはまる状態を表示する。

| 条件 | level / icon | 文言 | 次にすること |
| --- | --- | --- | --- |
| `gistId` と `worldUrl` が両方ない | `none` / `○` | まだセットアップされていません | 管理者がメニュー「Utsushi → セットアップ・修復」を実行してください |
| `lastPublishOutcome == 'disconnected'` | `warn` / `⏸` | 管理者がGitHub連携を解除したため、自動公開は止まっています | 新しい管理者が Utsushi → セットアップ・修復 を実行してください |
| `lastPublishOutcome == 'reauth_required'` | `error` / `⚠` | GitHub連携の期限が切れたため、公開が止まっています | 管理者がメニュー「Utsushi → セットアップ・修復」を実行してください（URLは変わりません） |
| `lastPublishStatus == 'failure'` | `error` / `⚠` | 公開できませんでした: 直近のエラー文（時刻を除く） | エラー文に応じて行動を案内。不明なら状態と URL を確認する |
| 最終公開から 90 分超 | `warn` / `⚠` | 自動公開が止まっているようです | 管理者がメニュー「Utsushi → セットアップ・修復」をもう一度実行してください |
| `publishedNames` が空 | `warn` / `⚠` | 掲示板に出す範囲がありません | 設定タブの A 列で先頭の `#` を消す。表を選び「Utsushi → 選択範囲を掲示板に追加」から追加もできる |
| `pending` がある | `warn` / `⏳` | 編集の反映を待っています（数分以内に自動で公開されます） | なし |
| 上記以外 | `ok` / `✔` | 正常に公開されています | なし |

`ok` または `warn` のとき、行ごとの警告があれば文言末尾に `（お知らせがあります）` を付ける。

- 設定タブは `settingsSheetId` で追跡し、名前が変わっていた場合は同じタブの名前を戻す。以前公開した範囲があり今回 0 件となる場合は公開を止め、手動操作では確認を求める。
- 連携解除時は `disconnected` を記録し、自動公開が停止したことを表示する。

### 6.4 セットアップと GitHub 連携（OAuth Device Flow）

`startSetup` は「他の人の GitHub でセットアップ済み・自分は未連携」の確認を最初に行う。それ以外は、GitHub との連携が必要なときに `beginAuthorization_(true)`、不要なときに `showFinishDialog_()` を呼ぶ。連携が必要な条件は、token が無い、`reauthRequired === 'true'`、DocumentProperties の `lastPublishOutcome === 'reauth_required'`、または `lastPublishStatus === 'failure'` かつ `lastError` に 403（「（エラー番号 403）」または「許可がありません」）が含まれる場合。

`startSetup` と `startSetupConfirmed` は設定タブの準備前に選択範囲を記録する。アクティブなシートが設定タブ以外で、選択が 2 セル以上なら `シート名!A1形式` を UserProperties の `setupSelection` に保存し、それ以外は削除する。`finishSetup` は公開前に、保存した選択範囲があり設定タブに有効な行（A 列が空でなく `#` で始まらない行）が無い場合、`addRangeRow_` で `board1` などの行を追加する。保護された設定タブに書けない場合もセットアップは続ける。追加の有無にかかわらず `setupSelection` を消し、戻り値に `addedName` / `addedRange` を含める。

1. 設定タブを用意する。`gistId` があり実行者にトークンが無い場合は、接続前に現在の GitHub 所有者を示してスタッフは操作不要だと案内する。管理者引き継ぎを選んだときだけ続行する。
2. トークンが無ければ Device Flow: `POST https://github.com/login/device/code`（`client_id`、`scope=gist`）。`user_code` を大きく表示し、「GitHubを開く」「コードをコピー」と状態行を出す。ダイアログの JS が `interval` 秒ごとに `pollDeviceToken()` を呼ぶ。`authorization_pending` は継続、`slow_down` は +5 秒、`expired_token`「コードの有効期限（15 分）が切れました」、`access_denied`「GitHub で承認がキャンセルされました」、その他「連携できませんでした（エラー名）」でやり直しを案内。`google.script.run` の一時的な失敗は 3 回まで再試行する。
3. 認証コードを `POST https://github.com/login/oauth/access_token` に `client_id`、`device_code`、`grant_type=urn:ietf:params:oauth:grant-type:device_code` として交換する。`client_secret` は使わない。取得した access token で `GET /user` を行いログイン名を検証してから、返された `access_token`、`refresh_token`、`expires_in`、`refresh_token_expires_in` を UserProperties に保存し、期限は絶対時刻に変換する。失敗した認証試行は現在の保存済み連携を変更しない。
4. 5 分ごとの既存タイマー公開は、内容が変わらない場合も含め、access token の期限 5 分前から更新する。更新は `POST https://github.com/login/oauth/access_token` に `client_id`、`grant_type=refresh_token`、現在の `refresh_token` を送る。返された新しいトークン一式を直ちに保存してから API 呼び出しを続ける。UserLock で並行更新を防ぐ。GitHub API が 401 を返した場合も一度だけ更新して要求を再試行する。403 は権限不足として更新・再認証エラーと区別する。
5. refresh token が期限切れ・無効の場合は「セットアップ・修復」を案内し、旧版から更新した場合は「GitHub連携をやり直す」を実行し、失敗や一時的なネットワーク障害で保存済みトークンを削除しない。期限情報のない PAT と従来の無期限 OAuth token は期限なしとして扱う。PAT で再連携するとOAuth用メタデータを消す。
6. `GITHUB_CLIENT_ID` は配布元の OAuth App の公開 Client ID（秘密ではない）。空のときは「Personal access token（classic、gist 権限）を貼り付ける」ダイアログにし、`X-OAuth-Scopes` に `gist` が無ければ拒否する。PATで保存するとOAuth用メタデータは削除する。
 7. `finishSetup()`: 保存済みの Gist の所有者が実行者と違えば、トリガーを設置せず「この公開先は <所有者> さんの GitHub にあります。管理者を交代する場合は、メニュー「Utsushi → 管理者用 → 公開先を作り直す」を使ってください」と表示して終わる。それ以外はトリガーを設置し、強制公開し、URL を表示し、URL 変更のお知らせを消す。連携のやり直し（§6.2）でトリガーを設置し直すときも同じ所有者確認を行う。完了結果は `{url, rangeCount, repaired, addedName, addedRange}`。初回の完了画面は「✔ セットアップが完了しました」、修復時は「✔ 修復しました。掲示板URLは変わっていません。」とし、URL とコピー操作、「このURLをワールド作者に渡してください」を表示する。選択していた表を追加した場合は「選択していた表（{範囲}）を「{名前}」として掲示板に出しました。」と表示する。`rangeCount == 0` なら「公開しました」とは表示せず、表の選択・メニューからの追加（または B 列への手入力と A 列の `#` 削除）・反映待ちを「次にやること」として番号付きで示す。範囲があれば「公開しました（N 個の範囲）」と表示する。完了時は設定タブをアクティブにする。

### 6.5 Gist

- 作成: `POST /gists`、`public: false`、ファイル `sheetboard.json`、description `Utsushi`。gistId・所有者ログイン名・URL を DocumentProperties に保存する。
- 更新: `PATCH /gists/{id}`（`Authorization: Bearer`、`Accept: application/vnd.github+json`、`X-GitHub-Api-Version: 2022-11-28`）。
- URL: `https://gist.githubusercontent.com/<所有者>/<id>/raw/sheetboard.json`（コミット SHA を含めない）。
- **所有者の確認**: 実行者のログイン名が保存済みの Gist の所有者と違えば PATCH も作り直しもせず、「この公開先は <所有者> さんの GitHub にあります。管理者を交代する場合は、メニュー「Utsushi → 管理者用 → 公開先を作り直す」を使ってください」というエラーにする（2 人が交互に作り直し続ける事故を防ぐ）。所有者が未保存（旧版から更新した場合）なら、その時点の実行者を所有者として保存する。
- 所有者本人の PATCH が 404（Gist が消された）のときだけ作り直し、新しい URL を「URL 変更のお知らせ」に記録してメールする。このメールも種類ごとの 24 時間抑止を通す（成功時の抑止リセットは URL 変更の種類を対象外にする）。
- 作成時にログイン名が未保存なら、先に `GET /user` で取得して保存する（URL に null が入らないように）。
- **公開先を作り直す（管理者交代用）**: メニュー「公開先を作り直す」は、確認ダイアログの後、DocumentProperties の gistId・所有者・URL を消し、実行者の GitHub に新しい Gist を作って公開する（トークンが無ければ先に連携画面を開き、承認後にこのメニューをもう一度実行する）。公開成功後、新しい所有者の自動公開トリガーを不足時に設置する。元の Gist は消さない。新しい URL を表示し、ワールドの URL 差し替えが必要だと伝える。

### 6.6 公開処理 `publish(force)`

1. ロックを取る（トリガー経由は `tryLock(0)`、メニュー経由は `tryLock(10000)`）。取れなければ保留フラグを立てて終わる。
2. 実行者がトークンを持たなければ保留フラグを立てて終わる（§6.2）。
3. 設定タブを読み、各範囲の表示値と、書式を反映する範囲は書式（§5）を一括取得する。非表示の行・列は出力しない。チェックボックスは `■` / `□`。
   - 「書式を反映」が TRUE の範囲では、`VALUE_IN_LIST` / `VALUE_IN_RANGE` の入力規則をチップ情報として収集する。`showDropdown=false` とチェックボックス、その他の条件種別は対象外。`VALUE_IN_RANGE` は参照元範囲から表示値の一致するセルの背景色・文字色を借りる。同じ参照元はキャッシュし、取得時の例外は色なしとして扱う。参照セル数に固定上限は設けない。
   - Google Sheets API の `DataValidationRule` と Apps Script の `DataValidation` には、チップ自体の色や表示形式（チップ / 矢印 / 書式なし）を読む項目がない。そのため表示形式は判別せず、対象のドロップダウンをチップとして描く。追加の `oauthScopes` は必要なく、スコープは変更していない。
4. `rev` を計算する。`force` でなく保留フラグも無く、`rev` が前回と同じで、最終公開から 60 分未満なら終わる。
5. `rev` が変わったときだけ `updatedUnix` を現在時刻にする。`aliveUnix` は常に現在時刻。`updated` はシートのタイムゾーン（`sheetTimeZone()`: スプレッドシート → スクリプト → `Asia/Tokyo`）で ISO 8601。
6. JSON が 10 MB を超えたら公開を失敗にする。800 KB を超える場合は iPhone 版 VRChat で表示できないことがあるという注意を出すが、公開は続ける。§6.5 のとおり PATCH。 iPhone 向けの注意は、内容が変わらず PATCH を省いた回にも維持する。
7. 成功したら rev・時刻を保存し、直近のエラーと種類ごとのメール抑止をリセットし、保留フラグを下ろす。警告は内容が変わったときだけ書き込む。
8. 実行開始から 4 分 30 秒を超えた場合は PATCH の前に中断し、Gist の内容を変更せず失敗として記録・通知する。その他の失敗も直近のエラーを `<ISO 時刻> <文言>` で記録し、§6.8 の通知を行う。利用者向けの文言は、何が起きたかと誰が何をするかを 1〜2 文で書く。Gist、token、OAuth、HTTP、scope、Device Flow などの用語は出さない。番号を示す場合は文末を `（エラー番号 N）` とする。5xx は一時応答として自動再試行を伝え、403 は管理者の再承認を案内する。10 MB 超過は範囲を小さくするか分割するよう案内する。

### 6.7 トリガー

- インストール型 onEdit（`onEditInstalled`）と onChange（`onChangeInstalled`、`changeType` が `FORMAT`・行列の挿入削除・`OTHER`）: 前回の公開から 5 秒未満なら、保留フラグを立てて終わる（5 分ごとの確認で公開される）。それ以外は公開する。
- 時間主導 5 分ごと（`onTimer`）: 公開する（保留フラグがあれば内容が同じでも公開する）。
- 同じ時間主導処理で、公開データが変化しなくても UserProperties の access token 有効期限を確認し、期限 5 分前から UserLock 下で refresh する。一時障害は保留フラグを立て、次回の時間主導実行で再試行後に公開状態を復旧する。
- トリガー経由の公開は例外を投げない（Google 側の失敗メールと二重にならないように）。
- `ScriptApp.getProjectTriggers()` は実行者自身のトリガーしか返さないので、設置・解除は実行者のトリガーだけを扱う。
- 単純トリガー名 `onEdit` は使わない。

- トリガー設置時の所有者を `triggerOwner` に記録し、所有者を確認する。

### 6.8 失敗の通知

種類（HTTP ステータス、それ以外は `other`）ごとに 24 時間に 1 回まで、トリガー所有者へメールする。件名は `Utsushi: 掲示板の公開が止まっています`。本文は全体状態の「何が起きたか」、利用者が行う番号付き手順、スプレッドシートの URL で構成する。401 または refresh token 失効は「Utsushi → セットアップ・修復」を案内する。403 は同じメニューから GitHub で再承認するよう案内する。一時的な refresh 失敗では保存済み認証情報を維持し、次の自動確認で再試行する。

### 6.9 `appsscript.json`

V8、`Asia/Tokyo`、スコープは `spreadsheets.currentonly`、`script.external_request`、`script.scriptapp`、`script.container.ui`、`script.send_mail`、`userinfo.email` の 6 つ。

### 6.10 実装上の決まり

- 内部関数は名前の末尾に `_` を付けて `google.script.run` から呼べないようにする。メニュー・トリガー・ダイアログから呼ぶ関数だけ `_` なし。
- Google API に触れない純粋関数（`parseRangeRef`、`trimEmpty`、`parseRatioList`、`parseAlignList`、`normalizeColor`、`generalAlign`、`mapAlign`、`styleBits`、`hiddenFilter`、`remapMerges`、`buildRangePayload`、`stableStringify`、`isoWithOffset`）は名前を変えず `_` も付けない。Node の `vm` で読み込んでもトップレベルで例外にならないこと。
- トークンはログ・URL・メール・エラー文に出さない。ダイアログに差し込む動的な文字列はすべてエスケープする。
- 表記: 「設定タブ」に統一。文末の句点をそろえる。

### 6.11 セキュリティ上の前提

シートの編集者は設定タブを書き換えて任意のタブを公開でき、バインドされたスクリプトも書き換えられる（書き換えたコードは管理者のトリガー＝管理者のトークンで動く）。したがって**編集者は信頼できる人に限り、設定タブは保護し、GitHub は Utsushi 専用（運営の共用）アカウントを使う**ことを README で求める。

### 6.12 画面の文言

- Device Flow とトークン貼り付け画面の冒頭は「掲示板を自動で更新するために、あなたのGitHubアカウントと連携します。」とする。
- セットアップの最終確認は「設定タブを準備して、掲示板用のURLを作ります。」とする。完了画面では「掲示板に出るのは、設定タブに書いた範囲だけです。」を見出しの下に表示する。
- 範囲追加の成功は「数分以内に掲示板に反映されます。」と案内し、掲示板が複数ある場合は名前「boardN」をワールド作者に伝えるよう案内する。自動公開待ちは「受け付けました。数分以内に自動で公開されます。操作は不要です。」とする。失敗は原因と次の操作を示す。
- 自動公開を経由する場合の反映時間は「数分以内に掲示板に反映されます」、今すぐ公開の直後は「ワールドの掲示板には1〜2分で反映されます」とする。

#### 伝え方の方針

1. 状況と次の操作だけを伝え、操作が不要ならそう書く。
2. 利用者向け文には内部用語を使わない。
3. メニューを案内するときは、すべての階層を書く。
4. 手動の再試行は「もう一度お試しください」、自動で直る場合は自動再試行と操作不要を伝える。
5. スタッフだけで直せない場合は管理者に伝えるよう案内する。
6. 反映時間の表記は「数分以内」と「1〜2分」に揃える。
7. 同じ意味の文は定数または関数にまとめる。

## 7. エディター

### 7.1 パッケージに含めるメニュー（`Tools/TenteEEEE/Utsushi/`）

- `掲示板をシーンに置く`: データ元 1 つと掲示板 1 つを配置して配線する。既にデータ元があれば掲示板だけ追加し、既存の掲示板と重ならないよう X 方向にずらし、選択状態にしてログを出す。シーンビューがあれば pivot に置き、シーンビューから板が見える向きにする。TMP Essentials が無ければ「インポートする／キャンセル」を表示し、インポート後はもう一度メニューを実行するよう案内する。prefab が無ければダイアログで中止する（自動ビルドしない）。
- メニューは再生中は無効にする（validator）。
- 同梱の prefab・マテリアル・フォント・program asset はビルド済みで、利用者は Build を実行しない。

- パッケージの場所に依存せず、必要なアセットを解決する。

### 7.2 Source のインスペクター

- 「掲示板URL」には管理者から受け取った URL を貼る。`baseUrl` は前後の空白を除いて使い、Inspector で関連する欄を変更したとき、ビルド開始時、Undo/Redo、および再生開始時に `urls` へ自動反映する（Inspector を見るだけでは書き換えない）。`N = clamp(ceil(300 / max(30, refreshInterval)) + 1, 4, 64)` とし、URL の `?` / `&` に応じて `?v=0..N−1` または `&v=0..N−1` を付ける。`baseUrl` が空なら `urls` を空にする。ビルド開始時も有効なデータ元へ自動反映を試みる。
- 接続確認は Inspector の描画時に URL ごとに自動で 1 回行う。同じ URL では失敗しても自動再試行しない。ボタンは「もう一度確認」。非同期の `UnityWebRequest`（`redirectLimit = 0`、User-Agent 付き）で、信頼リスト（config の `stringHostUrlList`、失敗時は内蔵の既定リスト）、HTTP 200 でリダイレクトでないこと、`sheetboard/1` であること、gist URL にコミット SHA が無いこと、`http://` でないことを検査し、結果を平易な文言で表示する。確認中はボタンを無効にし、Poll は例外を捕まえて必ず終了処理をする。User-Agent の版数は定数 1 か所で持つ。Inspector と Apps Script（シート側）の UI は日本語のみ。
- URL が空かつ `urls` が空なら警告は出さない。
- Inspector の自動反映はダイアログを出さず、`ProgramsCompiledQuiet()` が false の間は反映しない。代わりに「UdonSharp のコンパイルエラーがあるため、掲示板URLを反映できません。Console のエラーを直してください。」と表示する。
- 手動で proxy を書き換える操作（§7.3 のデータ元の自動設定、§7.1 の掲示板設置）は、UdonSharp のプログラムが未コンパイルなら先に `UdonSharpCompilerV1.CompileSync` でコンパイルする。その後 `UdonSharpProgramAsset.AnyUdonSharpScriptHasError()` でプロジェクト全体を確認し、Utsushi 以外の UdonSharp エラーも含め、エラーまたは未生成のプログラムがあれば止めて Console のエラーを直すよう案内する。初回はプログラムアセット作成後にもう一度実行が必要。
- ビルド開始時に読み込み済みシーンの有効な Source について掲示板URLを自動反映し、最後のビルド検査では URL が無い場合は止める。無効な Source は検査しない。Source が設定されていない有効な Display には警告を出す。コンパイルエラーも通常のビルドを止める。Play 開始時（`report == null`）は例外で Play を止めず、エラーを Console に記録する。

### 7.3 Display のインスペクター

Display に `language` フィールドはなく、ワールド内の見出し・状態・更新ボタンの言語は常に Source に従う。Editor と Apps Script の UI は日本語のみ。

- 基本設定の先頭にデータ元と共通の URL 欄・接続結果を表示し、掲示板 Inspector から掲示板URLを入力できる。Source の参照欄のラベルは「データ元」。
- 範囲一覧は接続確認後に `SessionState` に保存し、スクリプト再コンパイル後も保持する。「表示する範囲」の横に、同じ Source の確認で得た範囲名を選べるリストを出す。選択肢の先頭は「（自動: 一番上の範囲）」。
- Display の Inspector では、`source` が空でシーンにデータ元が 1 つなら「同じシーンのデータ元につなぐ」ボタンを表示し、複数あれば「データ元が複数あります。上の欄で選んでください。」を表示する。シーンに無い場合は「データ元を作ってつなぐ」ボタンから作成する。
- `textScale` はスライダー（0.5〜3）。
- 基本設定の下に「掲示板のサイズ」を常時表示する。幅・高さ（m、最小 640 × 320 px 相当）を Enter で確定する入力欄と、横長（標準、1600 × 1000）・縦長（1000 × 1800）・正方形（1200 × 1200）のプリセットを用意する。表の Canvas の RectTransform を変更し、同じオブジェクトの BoxCollider の幅・高さ・中心も合わせる（奥行きは維持）。背景と表領域は既存のアンカーに追従する。Undo・Prefab override に対応する。Play Mode 中は変更不可。既存の Canvas サイズは操作するまで変更しない。サイズの直後にテーマ（ライト／ダーク）を選べる。

- 範囲なしは「表示する範囲がまだありません」、未接続は「データ元が設定されていません」、大きな表には「表が大きく文字が小さくなっています」と表示する。

### 7.4 開発用ツール（パッケージ外）

`Assets/UtsushiDev/`（パッケージに含めない）に置く:

- メニュー `Tools/TenteEEEE/Utsushi (Dev)/Build Prefabs`（`UtsushiDev.UtsushiDevMenu.BuildPrefabsBatch`）と `Tools/TenteEEEE/Utsushi (Dev)/Export UnityPackage`（`UtsushiDev.UtsushiDevMenu.ExportUnityPackageBatch`）。作者が出荷前に実行する。
- `UtsushiDev.UtsushiBatchBuild.RebuildAndPreview`: prefab 再生成、配線検証、権限マトリクス確認、サンプル JSON の撮影をまとめて実行するバッチ入口。
- `UtsushiDev.UtsushiPreview.RenderSampleBatch`: 配線検証（両 prefab の Udon 側の値、「掲示板をシーンに置く」、データ前のタイトル）とサンプル JSON の撮影（`Logs/UtsushiPreview*.png`）。
- `UtsushiDev.UtsushiPermissionMatrixCheck.RunPermissionMatrix`。メニュー `Tools/TenteEEEE/Utsushi (Dev)/Verify Permission Matrix`。
- サンプル JSON（`sample.json`、`sample_fidelity.json`）。
- dev 用 asmdef `Utsushi.Editor.Dev`（`Utsushi.Editor` は `InternalsVisibleTo("Utsushi.Editor.Dev")`）。

## 8. prefab

- `Utsushi Source.prefab`: `UtsushiSource` のみ。
- `Utsushi Display.prefab`（ライト、背景 `#FFFFFF`）と `Utsushi Display Dark.prefab`（ダーク、背景 `#12161C`）。同じレイアウト:
  - ワールド空間 Canvas 1600 × 1000 px、スケール 0.001（1.6 m × 1.0 m）、レイヤー Default、`GraphicRaycaster`・`VRCUiShape`・トリガーの BoxCollider（Canvas の px サイズ × 奥行き 6）。
  - Background（Image）、Title（TMP、上端 90 px）、TableArea（左右 40・上 110・下 80 の余白）とその中の ColumnTemplate / OverlayTemplate / OverlayNoWrapTemplate（RectMask2D の枠 → TMP、非アクティブ）・CellTemplate（Image、非アクティブ）、StatusDot と Status（左下）、RefreshButton（右下、「更新」）。
  - RefreshButton の Label を `refreshLabel` に配線する。すべての RectTransform はアンカーとオフセットで配置する。ボタン以外は `raycastTarget = false`。
- フォント: 同梱の Noto Sans JP から動的 TMP フォントアセットを作り、`m_ClearDynamicDataOnBuild = true`。出荷時は動的データを空にしておく。

## 9. 制限

- 反映: セル・書式の編集は数秒〜十数秒（5 秒以内の連続編集は次の確認で）。数式の結果だけの変化は最大約 5 分。ワールド側は約 1 分ごとに取得。
- 行数・列数・公開範囲数・オーバーレイ数に利用上の上限は設けない。大きな表ほど処理が重くなり、文字は小さくなる。以下は外部仕様または描画方式による技術的な天井であり、該当時は切り捨て・中断・注意表示で利用者に知らせる。
  - 1 セルに表示できる文字は 4,000 文字まで（TMP の 1 メッシュあたり 16,383 文字の天井に対する安全側の値）。これを超えるセルは末尾を `…` に置き換える。
  - Apps Script の公開処理は 1 回 4 分 30 秒を予算とする。超過時は PATCH 前に中断し、Gist を変更せず失敗を通知する。
  - JSON は 10 MB まで。超える場合は公開を失敗にする（Gist raw が全文を返せる上限）。
  - JSON が 800 KB を超えると iPhone 版 VRChat で表示できないことがあるため注意を出す。公開は続ける。
  - 5,000 セルを超える範囲には、公開に時間がかかり文字が小さくなる注意を出す。
  - 無料 Google アカウントのトリガー総実行時間は 1 日 90 分。大きな表を 5 分ごとに読み直すとこの枠を使い切るおそれがある（未計測）。
  - Apps Script の状態保存値は UTF-8 で 8 KB 以内。範囲名が多すぎる場合は一部を省き、件数を表示する。
- Gist の CDN キャッシュ（300 秒）は URL ローテーションで避けるが、他のクライアントが直前に読んだ同じ URL からは古い版が返りうる（§3.4 の巻き戻り防止で表示は戻らない）。
- URL には GitHub のユーザー名が入り、Gist の説明は `Utsushi` に固定する。
- 再現できないもの: 条件付き書式の色、罫線（代わりに格子。罫線を読むには Sheets API と全スプレッドシートの閲覧権限が必要なため、権限を広げない方針で見送り）、画像・グラフ・図形、絵文字やフォントに無い記号、フォントの種類、縦位置（中央固定）。
- チップ色は参照元セルの色からのみ再現できる。それ以外は名前ごとの自動配色または既定色になる。「矢印」「書式なし」の表示形式のドロップダウンもチップとして描く。
- Apps Script の無料アカウントのトリガー実行時間は 90 分 / 日。大きな範囲での実行時間は未計測。

## 10. Source ごとのワールド内閲覧権限

### 10.1 目的と公開範囲

Apps Script は 1 スプレッドシートにつき 1 つの URL を公開し、閲覧権限は各 Source で設定する。PermissionController はワールド内の取得・表示を制御し、GitHub 上のアクセス権は変更しない。

| Source の閲覧権限 | 一般（Visitor） | ワールドスタッフ（Collab） | ワールド管理者（Auth） |
| --- | --- | --- | --- |
| 全員 | 取得・表示 | 取得・表示 | 取得・表示 |
| ワールドスタッフ以上 | 取得しない・表示しない | 取得・表示 | 取得・表示 |
| ワールド管理者のみ | 取得しない・表示しない | 取得しない・表示しない | 取得・表示 |

- 閲覧権限は「閲覧に必要な最小権限」とする。ワールド管理者にはすべて、ワールドスタッフには全員とワールドスタッフ以上、一般には全員だけを認める。
- PermissionController が無いプロジェクトでも、従来の全員向け掲示板を単体で利用できる。制限付きの Source は連携先が無い場合に取得・表示せず、全員公開へ自動で切り替えない。
- 本節では各端末が自分に許可された公開先を取得する。§2.2 のネットワーク同期を使わない構成を維持し、権限者から他プレイヤーへ本文を中継する機能は含めない。

### 10.2 Apps Script の配信とデータ分離

- 設定タブは A〜G 列を使う。全ての有効行をまとめ、1 つの JSON に含める。H 列や JSON の audience は使用しない。
- 1 つのスプレッドシートに `gistId`、所有者、URL、rev、公開状態を保存する。すべての範囲の変更を同じ Gist へ公開し、最後の範囲を削除したときは確認後に `ranges: {}` を公開して最新版から内容を除く。
- 閲覧権限により異なるデータを配信する場合は、別のスプレッドシートと Apps Script を使い、各 URL を別の Source / Display に割り当てる。

### 10.3 Unity 側の設定と任意連携

- Source に閲覧権限と `UdonBehaviour permissionController` を設定する。既定は「全員」、連携先は未設定とする。1 Source は 1 URL を担当し、Display は従来どおり Source と範囲名を指定する。
- エディターでは、シーン内に対応する PermissionController が 1 つだけあれば連携先の自動設定を提供する。複数あるときは利用者が選ぶ。制限付きの Source に連携先が無い・型が違う・対応しない版の場合は、Inspector に理由を表示し、ワールドのビルドを止める。
- 「接続を確認」はデータ形式を検査し、Source のローカル閲覧権限や設定タブとの一致は要求しない。エディターでの接続確認は作者の端末で行う操作であり、ワールド内の閲覧権限制御の対象ではない。
- PermissionController のソース・prefab を変更・再配布せず、その C# 型やアセンブリを Utsushi の必須依存にしない。Force Respawn パッケージも必須依存にしない。
- 実行時はローカルプレイヤーの PlayerObject 群から、`Controller` が設定された連携先と一致する UdonBehaviour を探し、`CurrentPermission` を読む。候補の変数が存在するかは `GetProgramVariableType` で先に確認し、無関係な behaviour の変数参照エラーを出さない。探索結果をキャッシュし、未生成・破棄時は再探索する。
- PermissionController の権限値は `Auth = 0`、`Collab = 1`、`Visitor = 2`。値を明示的に対応付け、不明値をワールド管理者として扱わない。PlayerObject 未生成・権限の初期化や復元待ち・参照切れの間は、制限付きの取得・表示を許可しない。
- エディターでの型検出・対応版の検査と、実行時の変数参照方式の参考実装: [PermissionController Force Respawn](https://github.com/TenteEEEE/vrchat-world-permission-controller-force-respawn)。特に `Runtime/ForceRespawnPanel.cs` の `FindPermissionSource` / `ReadPermission` と、`Editor/ForceRespawnPanelEditor.cs` / `ForceRespawnBuildCheck.cs` を参照する。

### 10.4 取得と表示への適用

- Source は既存の 1 秒ごとのループで権限を再評価し、**取得直前にも**検査する。初回取得・定期更新・近接取得・手動更新のすべてに適用し、ボタンや Canvas を隠すだけの制御にしない。
- 取得成功コールバックと `ApplyJson` でも権限を検査し、許可されていない本文を採用しない。権限が無い間は `HasData()` は false、`GetRange()` は null、`GetFirstRangeName()` は空文字を返す。
- 権限不足の表示は「閲覧権限がありません」、判定待ちは「権限を確認中…」とし、範囲名・タイトル・URL・本文を表示しない。更新ボタンも操作不可にする。
- 全員向け Source は PermissionController の有無や権限判定待ちにかかわらず利用できる。全員向けと制限付きの Source が同時にあっても、互いの取得状態やデータを上書きしない。

### 10.5 権限変更と取得中の応答

- 権限付与時は、次の権限確認（通常 1 秒以内）で利用可能にし、近接条件を満たしたら初回取得する。手動更新の基本動作は §3.3 に従う。
- 権限剥奪時は、次の権限確認またはコールバックで取得を止め、保持 JSON・範囲・更新待ちを消し、revision を進めて Display の表・タイトル・描画用プール内の文字列も消す。権限剥奪は §3.4 の「失敗時は保持データを消さない」の例外とする。通常の通信失敗では従来どおり保持する。
- 剥奪前に始まった通信は、停止できなくても結果を破棄する。剥奪後に再付与された場合も、古い応答を新しい取得結果として採用しない。無効化・再有効化によって権限確認を飛ばしたり、古いデータが再表示されたりしないこと。
- 制限付きの Source があるのに PermissionController が無効・利用不能になった場合も、権限剥奪と同じ扱いにする。

### 10.6 保護できる範囲

この機能は、通常のクライアントにおける取得・表示と、通信に伴う URL の露出機会を制御する。閲覧制限はワールド内の表示と通常の取得を制限するもので、データの秘匿や GitHub 側の閲覧権限を変更する機能ではない。ワールド作者が閲覧制限を設定するときは、URL を知っていれば誰でも公開データを直接読めることを判断に含める。

- ワールドに焼き込まれた URL は全来訪者に配信されるため、解析による取り出しを防ぐ保証はない。取得しない端末でも、URL が一切ログに出ないとは保証しない。
- 閲覧権限のない端末では制限付き Source の JSON をダウンロードしない。VRChat 実機ログに URL が出ないことは未検証として扱う。
- ワールド内で権限を剥奪しても、知っている URL や取得済みのコピーを失効させることはできない。
- Gist の過去履歴は残る。暗号化・履歴消去・サーバー側の利用者認証は本節に含めない。Inspector と README では「非公開」「URL を完全に隠す」と説明せず、この範囲を明示する。

### 10.7 完了条件と検証

権限判定と JSON の単一形式は自動検証し、実 Udon 上の権限変更・描画・通信は Unity batch と ClientSim または VRChat 実機で確認する。実機ログについては観測した事実だけを記録する。

1. PermissionController 未導入のプロジェクトでもコンパイル・ビルドでき、既存設定の全員向け掲示板が従来どおり動く。
2. 3 権限 × 3 Source 閲覧権限の全組合せが §10.1 の表と一致し、制限付き Source は閲覧権限がない端末で取得しない。
3. Apps Script は単一 JSON / URL を公開し、最後の範囲の削除と公開失敗後の再試行を確認する。
4. 途中参加と PlayerObject の生成・権限復元待ちに制限付きの取得が始まらず、解決後は正しく取得する。未知の権限値や異なる Controller の PlayerObject では許可しない。
5. ワールド管理者→ワールドスタッフ→一般、および逆方向の変更で、取得可否と表示が追従する。権限剥奪時は保持データ・タイトル・プール内の文字列が消える。
6. 取得中の剥奪、応答到着前の再付与、無効化・再有効化、タイムアウト後の遅い応答でも旧内容を復活させない。
7. 閲覧権限がない状態や Controller の未設定・不明状態では、手動更新からも取得できない。
8. 全員向けと制限付きの掲示板を同時に使い、一般端末と権限者端末それぞれの表示・実際のダウンロード・ログを確認する。Unity のプレビューだけで URL の非露出を検証済みとしない。

