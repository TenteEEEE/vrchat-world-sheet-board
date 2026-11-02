# Sheet Board

Show a Google Sheets range on boards in a VRChat world, and update it **without re-uploading the world**. Built for event teams: staff edit the team's spreadsheet, and the boards in the world follow within a minute or two.

- Keeps the sheet's look: background and text colours, bold/italic/underline/strikethrough, merged cells, column widths, row heights, alignment.
- Visitors do not need "Allow Untrusted URLs".
- No network sync: every client reads the same published JSON, so late joiners see the same thing.

The full step-by-step guide (for non-technical event staff) is in Japanese: **`README.ja.md`**.

## Requirements

- Unity 2022.3, VRChat Worlds SDK 3.8 or later (UdonSharp, TextMeshPro with TMP Essential Resources imported)
- A Google account for the spreadsheet, and one GitHub account for the team admin (a dedicated shared account is recommended)

## Install

Add `https://tenteeeee.github.io/vpm-repos/index.json` in VRChat Creator Companion (Settings → Packages → Add Repository), then add **Sheet Board** to your project. The prefabs are prebuilt; no build step is needed.

For manual installation, import the `.unitypackage` from [GitHub Releases](https://github.com/TenteEEEE/vrchat-world-sheet-board/releases). Its Apps Script files are under `Assets/SheetBoard/AppsScript/`.

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

1. **Admin** — in the spreadsheet: **Extensions → Apps Script** (it must be bound to the sheet), paste `AppsScript/SheetBoard.gs` (from `Packages/com.tentee.vrc-sheet-board/AppsScript/`), replace `appsscript.json` with the bundled one, reload the sheet. No deployment is needed.
2. **Admin** — **SheetBoard → はじめにセットアップ** (setup): authorise, enter the 8-character code at github.com/login/device, and copy the URL it shows. In the **SheetBoard設定** tab, set the range (`Sheet!A1:F19`) and remove the leading `#` from the name to publish it. Protect the settings tab and share the sheet with trusted staff as editors.
3. **World creator** — **Tools → TenteEEEE → Sheet Board → Install Sample into Current Scene**. Paste the URL into `SheetBoard Source`, press **URL を適用** (apply) and **接続を確認** (check). Each `SheetBoard Display` shows its range name, or the first range when left empty. Scale the board up for tables with many rows.

## Privacy and security

- Published data is readable by anyone who has the URL (secret gists are not searchable, but the URL is visible in VRChat logs), gists keep their revision history, the URL contains the GitHub user name and the gist description contains the spreadsheet name. Keep personal or private information out of the published ranges.
- Spreadsheet editors can change what is published and can edit the bound script, which runs with the admin's GitHub authorisation. Only trusted people should be editors; protect the settings tab; use a GitHub account dedicated to Sheet Board.

## Limits

Conditional-format colours, borders, images/charts, emoji and font families are not reproduced. Up to 20 ranges, 100 × 20 cells each. Apps Script trigger runtime on consumer accounts is capped at 90 min/day; the runtime of very large ranges has not been measured.

The design and the JSON format are specified in `docs/SPEC.md`.

## License

MIT. Bundled font Noto Sans JP is under the SIL Open Font License 1.1 (`Fonts/OFL-1.1.txt`).
