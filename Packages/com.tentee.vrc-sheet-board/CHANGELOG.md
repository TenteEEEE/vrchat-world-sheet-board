# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses [Semantic Versioning](https://semver.org/).

## [0.5.0] - 2026-11-12

### Added

- Add Display Inspector controls for board width and height, landscape/portrait/square presets, and world size in metres. Resizing updates the Canvas and collider together and supports Undo and prefab overrides.
- Add the script version to the Apps Script status dialog.
- Edit several boards or data sources at once in the Inspector.
- Warn in the Apps Script before publishing an empty board, and stop automatic publishing that would empty all boards.
- Expose `GetStatusLevel()` value `3` for the preparing state.

### Changed

- Apply the board URL automatically when it is edited and right before a build; remove the "URL を適用" button.
- Check the connection automatically and show the result in plain language; keep the range list across script reloads.
- Edit the board URL from the Display Inspector; rename user-facing labels to 掲示板URL / データ元 / 表示する範囲.
- Choose the theme (Light / Dark) from the Display Inspector.
- Rename the install menu to "掲示板をシーンに置く", place the board in front of the Scene view, and offer to import TMP Essential Resources.
- Rename the Apps Script menu item to "セットアップ・修復" and route every recovery message to it.
- Publish the table selected when setup starts as the first board.
- Show loading and permission-checking states with the busy colour instead of the error colour.
- A Display always follows its Source language.
- Update status wording, show a warning for large tables, and use the fixed Gist description `Sheet Board`.

### Fixed

- Locate package assets regardless of package installation path.
- Preserve published ranges when repairing after the settings tab is renamed.
- Show disconnected status after GitHub is unlinked rather than reporting normal operation.
- Show an error colour when a requested range is missing.
- Show retry status correctly in proximity mode.
- Prevent text from overflowing dropdown chips.

### Removed

- **Breaking:** Remove Display `language` and replace Source public fields `staleMinutes`, `manualCooldown`, and `startDelayMax` with constants.
- Remove the "直近のエラー" section from the status dialog; the status block already shows it.

## [0.4.0] - 2026-11-09

### Added

- Add automatic Japanese or English in-world messages, following each visitor's VRChat language by default.
- Warn when a published range exceeds 5,000 visible cells or JSON exceeds about 800 KB for iPhone compatibility.
- Add build checks for active Sources with missing or unapplied URLs, and warn when a Display has no Source.

### Changed

- Remove configured limits on range count, table rows and columns, and overlay count. Display pools now grow as needed.
- Exclude manually hidden rows and columns from published output; also exclude filtered rows when the sheet has a filter.
- Enforce a 4 minute 30 second publishing budget, a 10 MB JSON ceiling, and an 8 KB UTF-8 limit for saved state values. Publishing over the time or JSON limit fails visibly and leaves the existing Gist unchanged.
- Use text fields as overrides: blank fields and values matching their old Japanese defaults use the selected language's default text. Display up to 4,000 characters per cell and split column text into row-block segments.

### Removed

- Remove the Display fields `maxRows`, `maxCols`, and `maxOverlays`.

### Fixed

- Keep wrapping and merged-cell rendering when overlays are used; empty cells no longer consume overlays.
- Stop rendering the literal eight-character `</alpha>` closing tag as transparent text.
- Apply hidden-row and filter removal before building the complete range output instead of truncating to 100 rows first.

## [0.3.1] - 2026-11-08

### Fixed

- Draw every dropdown chip. The chip pool stopped at 64, so later dropdown cells in larger ranges were shown as plain text.

### Changed

- Size the chip and arrow pools from `maxRows` × `maxCols`, still creating objects only when needed; removed Display `maxChips`.

## [0.3.0] - 2026-11-05

### Added

- Draw dropdown chips and arrows in the Display.
- Add the `chip` layer to published JSON.
- Use source cell colours for dropdowns that use values from a range.
- Add automatic chip colours by name, enabled by default with `chipAutoPalette`.
- Add Display fields `maxChips`, `chipColor`, `chipArrowColor`, `chipAutoPalette`, `chipPalette`, `chipTemplate`, and `arrowTemplate`.

### Changed

- Make bold text heavier with TMP material `_WeightBold` set to 1.25.
- Correct vertical centring for cells whose font size differs from the base size and for columns whose first row is larger.

## [0.2.1] - 2026-11-04

### Changed

- Kept the refresh button label unchanged while fetching and indicated its disabled state with disabled colours; removed Display `refreshBusyText`.
- Reduced the default manual refresh cooldown from 30 seconds to 5 seconds, matching VRChat String Loading's limit of one request per five seconds.
- Updated the default refresh button and status lamp colours for readable contrast in both themes.

## [0.2.0] - 2026-11-04

### Changed

- Displays automatically fit column widths and text to the table area so headings and cells are no longer clipped; manual column ratios are still supported and keep their ratios while text shrinks to fit.
- The new Display option `autoFitColumns` (**列幅を自動調整**) is on by default, so existing boards re-layout after upgrading. Turn it off on a Display to keep the previous column widths.

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
