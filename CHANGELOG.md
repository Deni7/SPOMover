# Changelog

All notable changes to SPOMover are listed here. Versions follow [Semantic Versioning](https://semver.org/); before 1.0 a minor version may change the state database or the CLI.

## [0.7.0] — 2026-11-09

First release published on GitHub.

### Added

- **Folder inventory without a full library listing.** When a profile names a folder of a library, the inventory walks only that folder, folder by folder (Graph `items/{id}/children`), instead of listing the whole library through `delta`, which SharePoint supports only from the library root. The folder queue and the page position are saved with every page, so the walk continues after a stop or a crash.

### Changed

- A "changes only" inventory update (`inventory --update`) walks such folders again in full: there is no delta token for a folder.
- The run dashboard totals (files, bytes, checked-out files) are calculated by one summary query and match the detailed lists.

## [0.6.0] — 2026-11-09

The feature set that 0.7.0 builds on (stages 0–6 of the [implementation plan](docs/IMPLEMENTATION_PLAN.md)); it was not published as a release.

- Inventory of the source and target libraries through Microsoft Graph `delta`, resumable, with incremental updates; per-folder totals; checked-out files.
- Mapping "source path → target library and path" with live validation and CSV/XLSX import; coverage check before moving.
- Plan of batches grouped into waves with a time forecast; plan comparison.
- Apply through the SharePoint Copy/Move API (`MoveCopyUtil`) with throttling awareness, a resource-unit budget, work windows, an app pool, retries, open-file checks, verification and resume.
- After the move: verification of the target libraries, a pilot report, removal of empty source folders, link-replacement rules (CSV).
- Desktop app (WPF + WebView2, Blazor/MudBlazor) and the `spo-mover` CLI; English and Ukrainian UI.
- MSI installer and a portable zip (self-contained, win-x64).

[0.7.0]: https://github.com/Deni7/SPOMover/releases/tag/v0.7.0
[0.6.0]: https://github.com/Deni7/SPOMover/commit/8a479dd
