# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.1] - 2026-11-04

### Added

- JSON now includes the configured range order, so Displays with an empty range name use the first configured range. All-default `style`, `color`, `bg`, `ha`, and `wrap` layers may be omitted; `size` remains present.
- Per-range status is shown in settings column H, with overall publishing status in column J.
- The spreadsheet menu includes **選択範囲を掲示板に追加**, and administrator actions are grouped under **管理者用**.
- Boards show an updating state (status-line suffix, blinking lamp, disabled "更新中…" button) during manual and automatic refreshes, and the refresh button stays disabled during the manual cooldown.
- Device Flow refresh credentials are checked on the five-minute timer, and the administrator is guided through reconnection when authorization expires.

### Changed

- Each spreadsheet publishes all configured ranges through one URL. Viewing access is configured per `SheetBoard Source`; different datasets use separate spreadsheets.
- When a new administrator connects GitHub on a sheet published by someone else, the connection is reported as successful and points to **管理者用 → 公開先を作り直す** (which creates a new URL).
- Dialogs and errors provide plain language, copy buttons, and next steps; failure emails include resolution steps.
- Setup, publishing, and status messages use consistent wording and distinguish an immediate publish from a queued automatic publish. Update times are described as "数分以内" for automatic publishing and "1〜2分" after "今すぐ公開".
- Permission is checked on the one-second Source loop and at fetch/callback boundaries, reducing repeated work when multiple Displays read one Source.
- All Source and Display wording fields are available in their Inspectors.

### Fixed

- A timed-out URL is retired for twice the request timeout. If every URL is retired, the Source waits for the earliest expiry and clears the pending manual-refresh state instead of remaining stuck.
- The former administrator's timer no longer updates shared publishing state or sends failure mail after ownership transfers.
- Failed and expired Device Flow attempts give the correct next step for setup and handoff.
- Administrator handoff installs automatic publishing triggers after the new destination is published; missing GitHub authorization opens the connection dialog.

## [0.1.0] - 2026-11-02

### Fixed

- A timed-out URL is retired for twice the request timeout. If every URL is retired, the Source waits for the earliest expiry and clears the pending manual-refresh state instead of remaining stuck.
- The former administrator's timer no longer updates shared publishing state or sends failure mail after ownership transfers.
- Failed and expired Device Flow attempts give the correct next step for setup and handoff.
- Administrator handoff installs automatic publishing triggers after the new destination is published; missing GitHub authorization opens the connection dialog.
- UnityPackage exports include both compiled Udon programs and clear dynamic font data, including glyphs populated by previews.
- Reordering configured ranges changes the revision so displays with an empty range name switch to the new first range.

### Added

- Google Sheets → Apps Script → GitHub Gist → String Loading pipeline: boards update without re-uploading the world, and visitors need no "Allow Untrusted URLs".
- Sheet-faithful rendering: background and text colours, bold/italic/underline/strikethrough, font sizes, merged cells, column widths, row heights, per-cell alignment, text overflow into empty neighbours, wrapping, checkboxes, hidden rows and columns skipped.
- Light (sheet-like) and dark board prefabs, grid lines, text scale.
- Apps Script setup wizard with GitHub Device Flow (classic token fallback); publishes on edit, on format change and every 5 minutes when the content changed, plus an hourly heartbeat so boards can tell "no edits" from "publisher stopped"; daily failure mail with the sheet name and URL.
- Settings rows starting with `#` are ignored, so the example row is not published before the range is chosen.
- Editor: URL apply with cache-busting variants, connection check (allowlist, redirects, pinned gist revisions, JSON format), range-name picker, Install Sample.
