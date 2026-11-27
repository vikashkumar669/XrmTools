# Architecture

Chart Designer Pro is an XrmToolBox plugin for .NET Framework 4.8 (WinForms), built like the other tools in this repository.

## Projects

| Project | Target | Purpose |
|---|---|---|
| `src/ChartDesignerPro` | net48 | The plugin: MEF export, WinForms UI, Dataverse access. Compiles the core sources in, so the tool ships as one DLL. |
| `src/ChartDesignerPro.Core` | net48; net8.0 | Chart logic with no Dataverse SDK or WinForms dependency, so it can be tested anywhere. |
| `tests/ChartDesignerPro.Tests` | net48 (Windows); net8.0 | Dependency-free test runner. Never connects to an environment. |

## Layers

```
UI (ChartDesignerProControl, Controls/)            WinForms, WorkAsync for every Dataverse call
  │
Core services (ChartDesignerPro.Core)               no SDK, no UI
  Xml/          SafeXml (no DTDs, no resolver, size cap), read-only projections of chart XML
  Explorer/     list filtering and sorting, metadata cache with expiry, favourites and recent
  Summary/      plain-language description of a chart
  Editing/      ChartWorkspace: original snapshot, current text, undo history, find/replace
  Validation/   ValidationEngine + RuleCatalog (25 rules), Unified Interface capability data
  Comparison/   XML normalizer and line diff
  ImportExport/ chart JSON v1, <visualization> XML, CSV inventory, backup store
  Saving/       SaveWorkflow: readiness, conflict check, backup → update → publish → verify
  │
Data (plugin, Data/)                                the only code that calls the Dataverse SDK
  DataverseChartRepository    list (paged), retrieve, update XML columns, publish one table
  DataverseMetadataService    tables and columns
```

## Principles

- **The stored text is the source of truth.** XML is parsed for display and validation only. Opening, viewing and exporting never rewrite it, so unknown elements and attributes are always kept. Only text you edit is saved, exactly as edited.
- **Safe XML.** DTDs are prohibited, no external resources are resolved and documents have a size cap.
- **Writes are limited.** The repository can only update the two XML columns of one chart and publish one table. There is no create, delete or *Publish all*.
- **Nothing is written without confirmation.** The save dialog names the environment and URL and shows the diff. A backup is written first; no backup, no save.
- **Honest results.** Metadata checks that couldn't run say *not checked*, and saves are read back and compared.

## Save sequence

1. **Readiness.** The chart must be editable, changed and well-formed. Validation errors block; warnings need acknowledging.
2. **Conflict check.** The chart is read again and compared with the version that was opened (modified time and XML).
3. **Confirmation dialog.** Shows the environment, URL, diff, warnings and the publish choice.
4. **Backup** of the version being replaced.
5. **Update** only the changed columns.
6. **Publish** the chart's table, for system charts when chosen.
7. **Verify.** The chart is read back and compared with what was sent: exact, reformatted or different.
8. **Reopen** the saved version read-only.

A failure at any step is reported for that step. A failed backup saves nothing, and a failed update publishes nothing.

## Settings and files

- Settings are kept per connection through the XrmToolBox `SettingsManager`. Nothing secret is stored.
- Backups are written to `%APPDATA%\MscrmTools\XrmToolBox\Settings\ChartDesignerPro\Backups\<environment>\`.

## Build and release

- **CI.** `.github/workflows/chart-designer-pro.yml` runs the tests, builds the Release DLL, checks the versions and packs the `.nupkg` on every push and pull request.
- **Publishing.** When the version on `main` isn't on nuget.org yet, the workflow publishes it through NuGet Trusted Publishing.
- **From a PC.** `build/pack.ps1` does the same locally.
