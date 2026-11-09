# SPOMover

[Українська версія](README.uk.md)

Controlled splitting of a large SharePoint Online document library (1.5 million+ files) into several libraries of the same site, driven by a mapping table:

**inventory → plan (dry run) → move in waves → verify.**

The moves themselves are done by SharePoint (Copy/Move API, `MoveCopyUtil`), so version history, metadata and file IDs stay with the files. SPOMover takes care of everything around them: load control within the tenant's resource-unit budget, work windows, retries, locked and checked-out files, and resuming after a crash or a reboot.

## Features

- **Inventory** of the source and target libraries through Microsoft Graph `delta`, resumable after a stop, with incremental updates; per-folder totals; checked-out files detected through SharePoint REST.
- **Mapping** "source path → target library and path", validated while you type; import from CSV/XLSX.
- **Coverage check** before moving: what goes where, what stays, name conflicts, paths that would be too long.
- **Plan** of batches grouped into waves, with a time forecast; nothing is changed until you apply it.
- **Apply**: moves by SharePoint with throttling awareness (`429`/`503`, `Retry-After`), a resource-unit budget, work windows, an app pool, retries and verification.
- **After the move**: verification of the target libraries, a pilot report, removal of empty source folders, and link-replacement rules (CSV) for fixing links between Office files.
- **Desktop app** (WPF + WebView2, Blazor/MudBlazor) and a **CLI** for scheduled and scripted runs; both use the same engine and the same local state database.
- **English and Ukrainian UI**, switchable at runtime (the globe/translate button in the title bar; `--lang en|uk` in the CLI).

## Requirements

- Windows 10/11 with the WebView2 runtime.
- .NET SDK 10 to build (the release package is self-contained).
- An Entra ID app registration with `Sites.Selected` (Microsoft Graph and SharePoint, application permissions), `write` granted on the site, and a certificate in the Windows certificate store. No client secrets are stored by the app. Step-by-step setup: [user guide, section 1.5](docs/USER_GUIDE.md).

## Quick start

```bash
dotnet build -c Release
dotnet test
```

Demo without SharePoint (a Graph emulator, a 600-file source library and two target libraries):

```bash
dotnet run --project tools/SPOMover.DemoSeed -- D:\Temp\spo-mover-demo --open
```

`--large 1500000` adds a synthetic 1.5-million-file inventory to check GUI performance.

Sample settings: [samples/connection.example.json](samples/connection.example.json), [samples/split.profile.json](samples/split.profile.json).

CLI examples (the desktop app must be running; the CLI finds it through the data folder):

```bash
spo-mover connections import samples/connection.example.json
spo-mover profiles import samples/split.profile.json
spo-mover inventory split --wait
spo-mover map check split
spo-mover plan build split
spo-mover apply split --plan <plan> --wait
spo-mover verify <run> --update
```

Run `spo-mover --help` or `spo-mover <command> --help` for the full list.

## Language

The UI language is chosen on first start from the Windows display language (Ukrainian on a Ukrainian Windows, English otherwise) and can be changed from the title bar; the choice is saved in the data folder (`%LOCALAPPDATA%\SPOMover\language.txt`). The `SPO_MOVER_LANG` environment variable (`en` / `uk`) overrides it, and the CLI accepts `--lang`.

Translations live in `src/SPOMover.Core/Localization/<lang>/*.json` as `"English text": "translation"`; English is the source language in the code (`L.T("…")`). `LocalizationTests` fail when a key has no translation or Ukrainian text is left in the code.

## Documentation

- [User guide](docs/USER_GUIDE.md) (also built into the app as Help) — [Ukrainian](docs/USER_GUIDE.uk.md)
- Requirements (Ukrainian): [docs/FEATURES.md](docs/FEATURES.md)
- Implementation plan and stages (Ukrainian): [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md)
- Copy/Move API findings (Ukrainian): [docs/API_SPIKE.md](docs/API_SPIKE.md)

## Building a release

`installer\build.ps1` builds a self-contained release, the MSI installer (WiX) and a portable zip.

## Project layout

| Folder | Contents |
|---|---|
| `src/SPOMover.Core` | Profiles, mapping, planning, URL normalization, reports, localization |
| `src/SPOMover.Spo` | Microsoft Graph / SharePoint REST client, auth, throttling |
| `src/SPOMover.State` | SQLite state store |
| `src/SPOMover.Engine` | Run engine: inventory, apply, verify, cleanup |
| `src/SPOMover.Host` | Local REST API for the CLI |
| `src/SPOMover.Ui` | Blazor pages (MudBlazor) |
| `src/SPOMover.Desktop` | WPF + WebView2 desktop shell |
| `src/SPOMover.Cli` | `spo-mover` command line |
| `tools/` | Demo data generator, icon generator |
| `installer/` | WiX installer |

## License

[MIT](LICENSE). Bundled [uPlot](https://github.com/leeoniya/uPlot) is MIT-licensed (`src/SPOMover.Ui/wwwroot/lib/uplot/LICENSE`).
