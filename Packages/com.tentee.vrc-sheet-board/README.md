# Sheet Board

Show a Google Sheets range on boards in a VRChat world, and update it **without re-uploading the world**. Built for event teams: staff edit the team's spreadsheet, and the boards in the world follow within a few minutes.

- Keeps the sheet's look: background and text colours, bold/italic/underline/strikethrough, merged cells, column widths, row heights, alignment.
- Columns and text auto-fit by default so cell contents stay inside the board; manual column ratios remain available.
- Visitors do not need "Allow Untrusted URLs".
- No network sync: every client reads the same published JSON, so late joiners see the same thing.

The full step-by-step guide (for non-technical event staff) is in Japanese: **`README.ja.md`**.

Each spreadsheet publishes all enabled ranges through one URL. Set in-world viewing access on each `SheetBoard Source` with its PermissionController. If different audiences need different data, use separate spreadsheets and separate Sources/Displays.

## Requirements

- Unity 2022.3, VRChat Worlds SDK 3.8 or later (UdonSharp, TextMeshPro with TMP Essential Resources imported)
- A Google account for the spreadsheet, and one GitHub account for the team admin (a dedicated shared account is recommended)

## Install

Add `https://tenteeeee.github.io/vpm-repos/index.json` in VRChat Creator Companion (Settings → Packages → Add Repository), then add **Sheet Board** to your project. The prefabs are prebuilt; no build step is needed.

For manual installation, import the `.unitypackage` from [GitHub Releases](https://github.com/TenteEEEE/vrchat-world-sheet-board/releases). Its Apps Script files are under `Assets/SheetBoard/AppsScript/`.

After updating `SheetBoard.gs` in an existing spreadsheet, reconnect once using **SheetBoard → 管理者用 → GitHub連携をやり直す**. Reconnect with the same GitHub account; existing Gist URLs remain unchanged.

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

1. **Admin** — install the bundled Apps Script in the spreadsheet, then run **SheetBoard → はじめにセットアップ** and configure the ranges in **SheetBoard設定**.
2. **World creator** — add the Source and Display prefabs, apply the URL, and set the viewing level if needed.

When formatting is enabled for a range (column E in the settings tab), dropdowns are displayed as chips.

The Japanese guide has the detailed setup steps, menu labels, status messages, and troubleshooting: [README.ja.md](README.ja.md#目次).

## Privacy and security

- Anyone who has the URL can read published data directly. In-world viewing access limits normal loading and display; it does not keep the data confidential. This matters when a world creator decides to restrict viewing. URLs, Gist history, and downloaded copies cannot be hidden from people who know the URL or revoked.
- Spreadsheet editors can change what is published and can edit the bound script, which runs with the admin's GitHub authorisation. Only trusted people should be editors; protect the settings tab; use a GitHub account dedicated to Sheet Board.
- A spreadsheet publishes all enabled ranges through one URL. PermissionController controls viewing inside the world and does not change GitHub access. Keep private information out of published ranges.

## Limits

Conditional-format colours, borders, images/charts, emoji and font families are not reproduced. Borders are omitted to avoid expanding spreadsheet access permissions; thin grid lines are shown instead. Dropdown chip colours can be read from the source cells when a dropdown uses values from a range and those cells have colours. Otherwise, chips are coloured automatically by name; turn off the Display option for colouring chips by name to disable automatic colours. Dropdowns are displayed as chips regardless of their Sheets display style. Up to 20 ranges, 100 × 20 cells each. Apps Script trigger runtime on consumer accounts is capped at 90 min/day; the runtime of very large ranges has not been measured.

The design and the JSON format are specified in `docs/SPEC.md`.

## License

MIT. Bundled font Noto Sans JP is under the SIL Open Font License 1.1 (`Fonts/OFL-1.1.txt`).
