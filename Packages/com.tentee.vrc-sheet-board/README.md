# Sheet Board

Show a Google Sheets range on boards in a VRChat world, and update it **without re-uploading the world**. Built for event teams: staff edit the team's spreadsheet, and the boards in the world follow within a few minutes.

- Keeps the sheet's look: background and text colours, bold/italic/underline/strikethrough, merged cells, column widths, row heights, alignment.
- Columns and text auto-fit by default so cell contents stay inside the board; manual column ratios remain available.
- Visitors do not need "Allow Untrusted URLs".
- No network sync: every client reads the same published JSON, so late joiners see the same thing.

The full step-by-step guide (for non-technical event staff) is in Japanese: **`README.ja.md`**.

Each spreadsheet publishes all enabled ranges through one URL. Set in-world viewing access on each `SheetBoard Source` with its PermissionController. If different audiences need different data, use separate spreadsheets and separate Sources/Displays.

## Requirements

- Unity 2022.3, VRChat Worlds SDK 3.10.0 or later (UdonSharp, TextMeshPro with TMP Essential Resources imported)
- A Google account for the spreadsheet, and one GitHub account for the team admin (a dedicated shared account is recommended)

## Install

Add `https://tenteeeee.github.io/vpm-repos/index.json` in VRChat Creator Companion (Settings → Packages → Add Repository), then add **Sheet Board** to your project. The prefabs are prebuilt; no build step is needed.

For manual installation, import the `.unitypackage` from [GitHub Releases](https://github.com/TenteEEEE/vrchat-world-sheet-board/releases). Its Apps Script files are under `Assets/SheetBoard/AppsScript/`.

After updating `SheetBoard.gs` in an existing spreadsheet, reconnect once using **SheetBoard → 管理者用 → GitHub連携をやり直す (Admin → Reconnect GitHub)**. Reconnect with the same GitHub account; existing Gist URLs remain unchanged.

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

1. **Admin** — install the bundled Apps Script in the spreadsheet, then run **SheetBoard → はじめにセットアップ (Initial setup)** and configure the ranges in **SheetBoard設定 (settings tab)**.
2. **World creator** — add the Source and Display prefabs, apply the URL, and set the viewing level if needed.

When formatting is enabled for a range (column E in the settings tab), dropdowns are displayed as chips.

The Japanese guide has the detailed setup steps, menu labels, status messages, and troubleshooting: [README.ja.md](README.ja.md#目次).

Before building, each active Source needs a non-empty published URL that has been applied. A restricted Source also needs a supported PermissionController. Inactive or disabled Sources are ignored. A Display without a Source produces a warning.

The Unity Editor and Apps Script interfaces are Japanese only.

## Privacy and security

- Anyone who has the URL can read published data directly. In-world viewing access limits normal loading and display; it does not keep the data confidential. This matters when a world creator decides to restrict viewing. URLs, Gist history, and downloaded copies cannot be hidden from people who know the URL or revoked.
- Spreadsheet editors can change what is published and can edit the bound script, which runs with the admin's GitHub authorisation. Only trusted people should be editors; protect the settings tab; use a GitHub account dedicated to Sheet Board.
- A spreadsheet publishes all enabled ranges through one URL. PermissionController controls viewing inside the world and does not change GitHub access. Keep private information out of published ranges.

## Limits and technical ceilings

Conditional-format colours, borders, images/charts, emoji and font families are not reproduced. Borders are omitted to avoid expanding spreadsheet access permissions; thin grid lines are shown instead. Dropdown chip colours can be read from the source cells when a dropdown uses values from a range and those cells have colours. Otherwise, chips are coloured automatically by name; turn off the Display option for colouring chips by name to disable automatic colours. Dropdowns are displayed as chips regardless of their Sheets display style.

There is no configured maximum number of ranges, rows, columns, cells, or overlays. Large tables take longer to publish and draw, and the text becomes smaller to fit. Pools grow as needed.

A single cell displays up to 4,000 characters. Longer cell text is shortened with an ellipsis. Google Apps Script publishing has a 4 minute 30 second time budget; if it is exceeded, publishing fails and the existing Gist is left unchanged. JSON larger than 10 MB cannot be published. A warning appears when a range contains more than 5,000 visible cells. Free Google accounts have a 90 minute daily trigger runtime quota, so publishing large tables every five minutes may use up that allowance; this has not been measured.

The bundled Noto Sans JP font does not include Hangul, Simplified Chinese, or emoji glyphs, so those characters appear as □.

## Display language

In-world Source and Display messages can use Japanese or English. Auto follows each visitor's VRChat language: Japanese when it starts with `ja`, and English otherwise. The Editor and Apps Script interfaces are Japanese only.

## Upgrading from 0.x

- The Display fields `maxRows`, `maxCols`, and `maxOverlays` were removed. `maxChips` was removed in 0.3.1, and `refreshBusyText` was removed in 0.2.1.
- Source and Display text fields now override the Japanese and English defaults. Leave a field empty, or set it to its old Japanese default, to use the default for the selected display language.
- When moving from a `.unitypackage` install to VPM, the old `Assets/SheetBoard` folder is cleaned up. Do not install the package by both methods.

## Troubleshooting

- **No URL is set:** Paste the published URL into the Source and click `URL を適用` (Apply URL).
- **Range not found:** Check the range name in the spreadsheet settings and the Display. An empty Display range name uses the first configured range.
- **Auto-publish has stopped:** Open the Japanese SheetBoard menu and check the publishing status and URL. Reconnect the same GitHub account if authorization has expired.
- **□ boxes:** The bundled font lacks some scripts and emoji. Use Japanese or English text supported by Noto Sans JP.

## VRChat String Loading

VRChat allows one String Loading request every five seconds across the entire world. Sheet Board shares this limit with every other world feature that uses String Loading. Manual refreshes and other requests may wait.

## iOS

VRChat on iPhone may truncate String Loading data above about 800 KB, based on community reports. Sheet Board warns when published JSON exceeds this size but continues publishing; smaller ranges are more reliable on iPhone.

The design and the JSON format are specified in `docs/SPEC.md`.

## License

MIT. Bundled font Noto Sans JP is under the SIL Open Font License 1.1 (`Fonts/OFL-1.1.txt`).
