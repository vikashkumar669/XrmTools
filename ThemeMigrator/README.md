<p align="center"><img src="https://raw.githubusercontent.com/vikashkumar669/XrmTools/main/ThemeMigrator/src/ThemeMigrator/Resources/icon128.png" width="96" alt="Theme Migrator icon"></p>

# Theme Migrator for XrmToolBox

Copy **model-driven app themes** from one Dataverse environment to another, with the same configuration, logos and **GUIDs**.

Classic themes and the modern (New look) theme settings both move across together with the web resources they depend on. Before anything runs, you can preview each theme and see what will be created or updated in the target.

| | |
|---|---|
| **NuGet package** | `VikashKumar.XrmToolBox.ThemeMigrator` |
| **Current version** | 1.2026.10.1 |
| **Requires** | XrmToolBox 1.2025.10.74 or later (.NET Framework 4.8) |
| **License** | MIT |

---

## Features

### What gets migrated

| Kind | Stored in Dataverse as | What the tool copies |
|---|---|---|
| **Classic themes** | `theme` table | Every color column (navigation bar, header, links, process control, table colors, accents…), name, logo tooltip, and the **logo image web resource** |
| **Modern themes (New look)** | `organizationsetting` (environment value) and `appsetting` (per app) for **Custom theme definition** and **Override app header color** | The setting value, the **theme XML web resource** it points to, and the **logo web resource** named in the XML's `logoWebResource` attribute |

### How records are matched
- Themes, settings and web resources are created in the target **with the source GUID**, or updated if that GUID already exists. Running the tool again updates the target in place.
- Two cases can't keep the source GUID, and the log tells you when they happen:
  - **Web resources:** names must be unique. If the target already has the same name under another GUID, that record is updated.
  - **Settings:** only one value is allowed per setting (or per setting and app). An existing value under another GUID is updated.
- App-level modern values are matched to apps by **unique name**, so the app must already exist in the target.

### Safety
- The **In target** column shows *New → create* or *Exists → update* for every row before you migrate.
- The confirmation dialog lists each item by name, along with its action and whether anything will be published.
- **Microsoft system themes** (CRM Default, Blue, Orange) are greyed out and skipped by *Select all*. They have the same GUIDs in every environment, so migrating them would overwrite the target's copy. If you tick one yourself, you're warned first.
- Double-clicking a row doesn't change its tick.
- Nothing is published unless you turn on **Publish and set as default**.

### Publish and set as default (toolbar toggle, off by default)
When it's on, after migrating the tool:
1. Publishes the migrated web resources.
2. Publishes the selected classic theme (`PublishTheme`), which makes it the target's **default theme**. Only one classic theme can be ticked in this mode.
3. Runs **Publish all customizations** if any modern settings were migrated. Microsoft requires this before setting values take effect.

### Preview
Selecting a row shows a mock app header with the theme's colors and logo, plus a swatch for every color. For modern settings it also shows the theme XML.

---

## How to use it

1. In XrmToolBox, **connect to the source** environment and open **Theme Migrator**.
2. Click **Select target** and choose the target connection.
3. Click **Load themes**. The *Classic themes* and *Modern themes* tabs fill up, and the *In target* column shows what each item will do.
4. Select rows to preview them, then **tick** what you want to migrate.
5. Optionally switch on **☐ Publish and set as default**.
6. Click **Migrate selected**, check the confirmation, and follow the color-coded **Log**.

**Permissions:** System Customizer or System Administrator in both environments.

**Modern themes:** apps must use the *New look* for the modern theme to show. After publishing, refresh any open app.

---

## Tested scenarios

Tested on 18 Oct 2026 between two Dataverse trial environments with XrmToolBox 1.2026.9.78:

| Scenario | Result |
|---|---|
| New custom classic theme with a PNG logo | Created in the target with the same GUIDs for the theme and logo; colors and logo link verified through the Web API |
| Environment-level *Custom theme definition* with `logoWebResource` | Theme XML, logo and setting value created with the same GUIDs; XML content identical |
| Re-running an already migrated theme | Updated in place (0 created, 2 updated, 0 failed) |
| Publish and set as default | Theme published and became the target's default theme |
| System themes | Greyed out and skipped by *Select all*; warning shown if ticked |

---

## Build and debug

1. Open `ThemeMigrator.sln` in Visual Studio 2022 or 2026 with the *.NET desktop development* workload.
2. **Close XrmToolBox completely**, then build in **Debug**. A post-build step copies `ThemeMigrator.dll` to `%APPDATA%\MscrmTools\XrmToolBox\Plugins`. If XrmToolBox is running, the copy fails with *MSB3027 (file locked)*.
3. To debug with F5, set `executablePath` in `src/ThemeMigrator/Properties/launchSettings.json` to your `XrmToolBox.exe` and use the **XrmToolBox** launch profile.

---

## Releasing to the XrmToolBox Tool Library

The Tool Library is fed from **nuget.org**. The package must:
- put `XrmToolBox` first in its tags,
- contain the DLL under `lib\net48\Plugins\`,
- and depend on the `XrmToolBox` package.

`ThemeMigrator.nuspec` already does all three.

### Option A: GitHub Actions (recommended)
Publishing uses **nuget.org Trusted Publishing**: GitHub proves the workflow's identity to nuget.org, which issues a one-hour key for that run, so no API key is stored anywhere. The policy is already set up on nuget.org (*Account → Trusted Publishing*): owner `VikashKumar0491`, repository `vikashkumar669/XrmTools`, workflow `theme-migrator.yml`, limited to the `VikashKumar.XrmToolBox.ThemeMigrator` package.

1. Bump the version in **both** `src/ThemeMigrator/ThemeMigrator.csproj` and `src/ThemeMigrator/ThemeMigrator.nuspec`, using the format `1.YYYY.M.rev`, and update `<releaseNotes>`.
2. Commit, then push a tag that matches the version:
   ```bash
   git tag ThemeMigrator-v1.2026.10.1
   git push origin ThemeMigrator-v1.2026.10.1
   ```
3. The **Theme Migrator** workflow then:
   - builds the Release DLL,
   - checks that the csproj, nuspec and tag versions match,
   - packs the `.nupkg`, logs in to nuget.org through Trusted Publishing and pushes it,
   - and attaches the package to a GitHub release.

Every push to `main` that touches `ThemeMigrator/` also builds and packs, so a test `.nupkg` is always available under the workflow run's **Artifacts**.

If you rename the workflow file or move the repo, update the Trusted Publishing policy on nuget.org to match.

### Option B: from your PC
```powershell
cd C:\XrmToolBox\ThemeMigrator
.\build\pack.ps1                                   # build + pack -> .\artifacts\
.\build\pack.ps1 -Push -ApiKey <your-nuget-api-key> # build + pack + push
```

Option B needs a personal API key from **nuget.org → Account → API Keys** (scope *Push*, glob `VikashKumar.XrmToolBox.ThemeMigrator`). Since August 2026, nuget.org limits new keys to 30 days, so Option A is the better default. Keep any key private.

### Register the tool with XrmToolBox (one time, after the first push)
1. Wait until the package shows as *listed* on nuget.org (validation usually takes 10–60 minutes).
2. Sign in at https://www.xrmtoolbox.com, open the tool submission page, and submit the package id **`VikashKumar.XrmToolBox.ThemeMigrator`**.
3. After approval it appears in **XrmToolBox → Tool Library**. Later versions reach users automatically.

The portal checks **every listed version** of the package. If a version is ever pushed without the XrmToolBox dependency, unlist it on nuget.org.

---

## Project layout
```
ThemeMigrator/
  ThemeMigrator.sln
  build/pack.ps1                       build, pack, push from a PC
  src/ThemeMigrator/
    ThemeMigrator.csproj               net48, XrmToolBoxPackage (compile-only)
    ThemeMigrator.nuspec               Tool Library package definition
    ThemeMigratorPlugin.cs             MEF export: name, description, icons, colors
    ThemeMigratorControl.cs            UI (source = main connection, target = additional connection)
    Services/ThemeReader.cs            queries: themes, settings, web resources, target lookups
    Services/MigrationEngine.cs        same-GUID create/update + publish
    Services/EntityCopier.cs           metadata-driven attribute filtering for the target
    Controls/ThemePreviewPanel.cs      header + swatch preview
    Controls/CheckListView.cs          list that ignores double-click ticks
    Models/                            view models, color helpers
    Resources/icon32|80|128|256.png    tool icons
```

## Release notes

**1.2026.10.1**: first release
- Classic themes, with their logo web resource
- Modern theme settings (*Custom theme definition*, *Override app header color*) at environment and app level, with their theme XML and logo web resources
- Same-GUID create or update, with a preview of what will be created or updated in the target
- Optional *Publish and set as default*
- Protection for system themes, named confirmation, double-click guard

## License
MIT. See [LICENSE](https://github.com/vikashkumar669/XrmTools/blob/main/ThemeMigrator/LICENSE).
