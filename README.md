# Utsushi

Formerly "Sheet Board" (renamed in 0.6.0; same features).

Show a Google Sheets range on boards in a VRChat world, and update it **without re-uploading the world**. Built for event teams: staff edit the team's spreadsheet, and the boards in the world follow within a few minutes.

- Keeps the sheet's look: background and text colours, bold/italic/underline/strikethrough, merged cells, column widths, row heights, alignment.
- Columns and text auto-fit by default so cell contents stay inside the board; manual column ratios remain available.
- Visitors do not need "Allow Untrusted URLs".
- No network sync: every client reads the same published JSON, so late joiners see the same thing.

The full step-by-step guide (for non-technical event staff) is in Japanese: **`README.ja.md`**.

Each spreadsheet publishes all enabled ranges through one URL. Set in-world viewing access on each `Utsushi Source` with its PermissionController. If different audiences need different data, use separate spreadsheets and separate Sources/Displays.

## Requirements

- Unity 2022.3, VRChat Worlds SDK 3.10.0 or later (UdonSharp, TextMeshPro)
- A Google account for the spreadsheet, and one GitHub account for the team admin (a dedicated shared account is recommended)

## Install

Add `https://tenteeeee.github.io/vpm-repos/index.json` in VRChat Creator Companion (Settings → Packages → Add Repository), then add **Utsushi** to your project. The prefabs are prebuilt; no build step is needed. The placement menu offers to import TMP Essential Resources if needed.

For manual installation, import the `.unitypackage` from [GitHub Releases](https://github.com/TenteEEEE/vrchat-world-utsushi/releases). Its Apps Script files are under `Assets/Utsushi/AppsScript/`.

After replacing `Utsushi.gs` in an existing spreadsheet, the admin must run **Utsushi → 管理者用 → GitHub連携をやり直す** to reconnect with the same GitHub account; existing Gist URLs remain unchanged. If authorization expires, run **Utsushi → セットアップ・修復**.

## How it works

```
Team spreadsheet ──(bundled Apps Script: on edit / format change / every 5 min if changed, hourly heartbeat)──▶ GitHub Gist ──▶ boards in the world
```

VRChat's String Loading only reads from an allowlist of hosts and Google is not on it, so the Apps Script mirrors the selected ranges as JSON to a GitHub Gist (allowlisted), and the world reads the Gist. String Loading does not follow redirects; use the exact URL the script shows.

## Roles

| Role | Who | Does | GitHub |
| --- | --- | --- | --- |
| Admin | one person on the team | installs the script in the sheet, connects GitHub once, protects the settings tab | account needed |
| World creator | one person | places the boards in Unity, pastes the URL, uploads once | not needed |
| Staff | trusted editors | edit the sheet | not needed |
| Visitors | — | nothing | not needed |

## Setup (short version)

1. **Admin** — install the bundled Apps Script in the spreadsheet, then select the table first and run **Utsushi → セットアップ・修復 (Setup / Repair)**; the selected table is published as the first board. Configure additional ranges in **Utsushi設定 (settings tab)**.
2. **World creator** — run **Tools → TenteEEEE → Utsushi → 掲示板をシーンに置く (Place a board in the scene)**, then paste the board URL into the board's Inspector. The URL is applied and checked automatically.

When formatting is enabled for a range (column E in the settings tab), dropdowns are displayed as chips.

The Japanese guide has the detailed setup steps, menu labels, status messages, and troubleshooting: [README.ja.md](README.ja.md#目次).

The board URL is applied automatically, including right before a build. A build stops only if an active Source has no URL.

Selecting a Source with a board URL in the Inspector accesses that URL once to check the connection when no result is available yet.

The Unity Editor and Apps Script interfaces are Japanese only.

## Privacy and security

- Anyone who has the URL can read published data directly. In-world viewing access limits normal loading and display; it does not keep the data confidential. This matters when a world creator decides to restrict viewing. URLs, Gist history, and downloaded copies cannot be hidden from people who know the URL or revoked.
- Spreadsheet editors can change what is published and can edit the bound script, which runs with the admin's GitHub authorisation. Only trusted people should be editors; protect the settings tab; use a GitHub account dedicated to Utsushi.
- A spreadsheet publishes all enabled ranges through one URL. PermissionController controls viewing inside the world and does not change GitHub access. Keep private information out of published ranges.

- The board URL contains the GitHub username. If sensitive data was published accidentally, remove it from the sheet and delete the Gist to remove its history. A new Gist has a different URL, so the world creator must replace the URL and re-upload the world; copies already obtained cannot be recalled.

## Limits and technical ceilings

To change the board shape, select a Display and use **掲示板のサイズ** (Board size) in its Inspector. Choose landscape, portrait, or square, or enter the width and height in metres. The Canvas and its collider resize together, with Undo and prefab overrides supported. Choose the theme (Light / Dark) in the Inspector. For tall tables, increase the height or split the published range across multiple boards; the whole range fits on one board without scrolling.

Conditional-format colours, borders, images/charts, emoji and font families are not reproduced. Borders are omitted to avoid expanding spreadsheet access permissions; thin grid lines are shown instead. Dropdown chip colours can be read from the source cells when a dropdown uses values from a range and those cells have colours. Otherwise, chips are coloured automatically by name; turn off the Display option for colouring chips by name to disable automatic colours. Dropdowns are displayed as chips regardless of their Sheets display style.

There is no configured maximum number of ranges, rows, columns, cells, or overlays. Large tables take longer to publish and draw, and the text becomes smaller to fit. Pools grow as needed.

A single cell displays up to 4,000 characters. Longer cell text is shortened with an ellipsis. Google Apps Script publishing has a 4 minute 30 second time budget; if it is exceeded, publishing fails and the existing Gist is left unchanged. JSON larger than 10 MB cannot be published. A warning appears when a range contains more than 5,000 visible cells. Free Google accounts have a 90 minute daily trigger runtime quota, so publishing large tables every five minutes may use up that allowance; this has not been measured.

The bundled Noto Sans JP font does not include Hangul, Simplified Chinese, or emoji glyphs, so those characters appear as □.

## Display language

In-world Source and Display messages can use Japanese or English. Auto follows each visitor's VRChat language: Japanese when it starts with `ja`, and English otherwise. A Display always follows its Source. The Editor and Apps Script interfaces are Japanese only.

## Upgrading from 0.x

### Migrating from Sheet Board

When you add Utsushi in VCC, the old package (`com.tentee.vrc-sheet-board`) is removed automatically. Boards and data sources already in your scene keep working because the script contents are unchanged. If you installed with a unitypackage, delete `Assets/SheetBoard` before installing Utsushi.

The spreadsheet script keeps working without being pasted again. If you paste it again, the menu is named “Utsushi” and the settings tab is automatically renamed to “Utsushi設定”. The board URL does not change.

- The Display fields `maxRows`, `maxCols`, and `maxOverlays` were removed. `maxChips` was removed in 0.3.1, and `refreshBusyText` was removed in 0.2.1.
- Source and Display text fields now override the Japanese and English defaults. Leave a field empty, or set it to its old Japanese default, to use the default for the selected display language.
- When moving from a `.unitypackage` install to VPM, the old `Assets/Utsushi` folder is cleaned up. Do not install the package by both methods.

- Updating the Unity package does not update the script in the spreadsheet. The old menu remains until the new `Utsushi.gs` is pasted into it.
- Old → new names: **はじめにセットアップ** → **セットアップ・修復**; **Install Sample into Current Scene** → **掲示板をシーンに置く**; **URL を適用** was removed because the board URL is applied automatically.
- Check the script version at the end of **状態とURLを表示** (スクリプトの版: X).
- Breaking change: Display `language` was removed; Source `staleMinutes`, `manualCooldown`, and `startDelayMax` are now constants.

## Troubleshooting

- **No URL is set:** Paste the board URL into the board's Inspector.
- **Range not found:** Check the range name in the spreadsheet settings and the Display. An empty Display range name uses the first configured range.
- **Auto-publish has stopped:** Open the Japanese Utsushi menu and check the publishing status and URL. Run **Utsushi → セットアップ・修復** if authorization has expired.
- **□ boxes:** The bundled font lacks some scripts and emoji. Use Japanese or English text supported by Noto Sans JP.

In-world status examples: `Updated 21:05 (just now)`, `Updated 21:05 (2 h ago) · may be out of date`, `Updated 21:05 (3 min ago) · retrying`, `Loading…`, `Come closer to load`, and `Couldn't load · retrying in 10s (timed out)`. Errors can also say `invalid data` or `HTTP 404`.

Other messages include `No range has been published yet`, `No data source is set`, `You don't have permission to view this`, `Checking permission…`, and `· table is too large to read comfortably`.

## VRChat String Loading

VRChat allows one String Loading request every five seconds across the entire world. Utsushi shares this limit with every other world feature that uses String Loading. Manual refreshes and other requests may wait.

## iOS

VRChat on iPhone may truncate String Loading data above about 800 KB, based on community reports. Utsushi warns when published JSON exceeds this size but continues publishing; smaller ranges are more reliable on iPhone.

The design and the JSON format are specified in `docs/SPEC.md`.

## License

MIT. Bundled font Noto Sans JP is under the SIL Open Font License 1.1 (`Fonts/OFL-1.1.txt`).
