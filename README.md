# XrmTools

XrmToolBox tools for Microsoft Dataverse / Dynamics 365, by Vikash Kumar. Each tool lives in its own folder with its own solution, README and NuGet package.

## Tools

| Tool | What it does | NuGet package | Docs |
|---|---|---|---|
| <img src="ThemeMigrator/src/ThemeMigrator/Resources/icon32.png" width="20"> **Theme Migrator** | Migrates classic and modern (New look) model-driven app themes, with their logos and theme XML, from a source to a target environment, keeping the same GUIDs. Can optionally publish the theme and set it as the default. | `VikashKumar.XrmToolBox.ThemeMigrator` | [README](ThemeMigrator/README.md) |
| <img src="DataQualityStudio/src/DataQualityStudio/Resources/icon32.png" width="20"> **Data Quality Studio** | Dataverse Excel mapping and data quality studio: maps Excel/CSV migration templates to Dataverse columns, validates required fields, data types, choices and lookups, previews the transformed values, reports unmapped columns without stopping, and exports row-level errors with suggested corrections. Mappings are saved as reusable profiles. Read-only: never writes to Dataverse. | `VikashKumar.XrmToolBox.DataQualityStudio` | [README](DataQualityStudio/README.md) |
| <img src="DiagnosticsWorkbench/src/DiagnosticsWorkbench/Resources/icon32.png" width="20"> **Diagnostics Workbench** | Dataverse developer diagnostics: loads plug-in trace logs by date, table, message and plugin type, groups recurring errors by a normalized exception fingerprint, highlights slow and near-timeout steps against configurable thresholds, rebuilds correlation chains, flags step configuration smells, and exports CSV or a self-contained HTML report. Read-only: never writes to Dataverse. | `VikashKumar.XrmToolBox.DiagnosticsWorkbench` | [README](DiagnosticsWorkbench/README.md) |

## Installing
In XrmToolBox, open **Tool Library**, search for the tool name and click **Install**. Tools appear there after their NuGet package has been published and approved by XrmToolBox.

## Repository layout
Every tool is independent: its own folder, solution, NuGet package, version and workflow. Changing or releasing one tool never touches another.
```
ThemeMigrator/                  Theme Migrator
DataQualityStudio/              Data Quality Studio
DiagnosticsWorkbench/           Diagnostics Workbench
<ToolName>/                     one folder per tool
  <ToolName>.sln
  build/pack.ps1                local build + pack + push
  src/<ToolName>/               plugin project (.NET Framework 4.8) and .nuspec
.github/workflows/              one build/release workflow per tool
                                (theme-migrator.yml, data-quality-studio.yml,
                                diagnostics-workbench.yml)
```

## Releasing a tool
Each tool has a GitHub Actions workflow that builds and packs it on every change. When a push to `main` carries a new version (or a tag `<ToolName>-v<version>` is pushed), the workflow publishes the package to nuget.org through **Trusted Publishing**, so no API key is stored, and creates a GitHub release. Each tool needs a Trusted Publishing policy on nuget.org for its workflow file. Each tool's README has the full steps.

## License
MIT, unless a tool's folder says otherwise.
