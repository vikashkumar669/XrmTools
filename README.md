# XrmTools

XrmToolBox tools for Microsoft Dataverse / Dynamics 365, by Vikash Kumar. Each tool lives in its own folder with its own solution, README and NuGet package.

## Tools

| Tool | What it does | NuGet package | Docs |
|---|---|---|---|
| <img src="ThemeMigrator/src/ThemeMigrator/Resources/icon32.png" width="20"> **Theme Migrator** | Migrates classic and modern (New look) model-driven app themes, with their logos and theme XML, from a source to a target environment, keeping the same GUIDs. Can optionally publish the theme and set it as the default. | `VikashKumar.XrmToolBox.ThemeMigrator` | [README](ThemeMigrator/README.md) |

## Installing
In XrmToolBox, open **Tool Library**, search for the tool name and click **Install**. Tools appear there after their NuGet package has been published and approved by XrmToolBox.

## Repository layout
```
<ToolName>/                     one folder per tool
  <ToolName>.sln
  build/pack.ps1                local build + pack + push
  src/<ToolName>/               plugin project (.NET Framework 4.8) and .nuspec
.github/workflows/              one build/release workflow per tool
```

## Releasing a tool
Each tool has a GitHub Actions workflow that builds and packs it on every change. When a push to `main` carries a new version (or a tag `<ToolName>-v<version>` is pushed), the workflow publishes the package to nuget.org through **Trusted Publishing**, so no API key is stored, and creates a GitHub release. Each tool needs a Trusted Publishing policy on nuget.org for its workflow file. Each tool's README has the full steps.

## License
MIT, unless a tool's folder says otherwise.
