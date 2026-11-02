# Changelog

All notable changes to this package are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-02

### Fixed

- Administrator handoff installs automatic publishing triggers after the new destination is published; missing GitHub authorization opens the connection dialog.
- VPM and UnityPackage distributions include both compiled Udon programs and clear dynamic font data, including glyphs populated by previews.
- Reordering configured ranges changes the revision so displays with an empty range name switch to the new first range.

### Added

- Google Sheets → Apps Script → GitHub Gist → String Loading pipeline: boards update without re-uploading the world, and visitors need no "Allow Untrusted URLs".
- Sheet-faithful rendering: background and text colours, bold/italic/underline/strikethrough, font sizes, merged cells, column widths, row heights, per-cell alignment, text overflow into empty neighbours, wrapping, checkboxes, hidden rows and columns skipped.
- Light (sheet-like) and dark board prefabs, grid lines, text scale.
- Apps Script setup wizard with GitHub Device Flow (classic token fallback); publishes on edit, on format change and every 5 minutes when the content changed, plus an hourly heartbeat so boards can tell "no edits" from "publisher stopped"; daily failure mail with the sheet name and URL.
- Settings rows starting with `#` are ignored, so the example row is not published before the range is chosen.
- Editor: URL apply with cache-busting variants, connection check (allowlist, redirects, pinned gist revisions, JSON format), range-name picker, Install Sample.
