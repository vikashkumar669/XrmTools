# Publishing checklist (XrmToolBox Tool Library)

## Before the first release

- [ ] The [integration test plan](INTEGRATION-TEST-PLAN.md) passed on a non-production environment, and the result of C4 is recorded
- [ ] The unit tests pass on Windows: `dotnet run --project tests\PluginFailureCorrelationExplorer.Tests`
- [ ] A Release build loads in the current XrmToolBox version, with the name, description and both icons shown in the tool list
- [ ] The README describes the features, permissions, known limitations and release notes for this version
- [ ] The README image uses an absolute `raw.githubusercontent.com` URL (nuget.org doesn't render relative images)
- [ ] The nuget.org Trusted Publishing policy exists: owner `VikashKumar0491`, repository `vikashkumar669/XrmTools`, workflow `plugin-failure-correlation-explorer.yml`

## Package metadata (`src/PluginFailureCorrelationExplorer/PluginFailureCorrelationExplorer.nuspec`)

- [ ] `id` is `VikashKumar.XrmToolBox.PluginFailureCorrelationExplorer` (never change it after the first publication)
- [ ] `version` equals `<Version>` in the csproj, in the format `1.YYYY.M.rev` (the workflow and `pack.ps1` refuse a mismatch)
- [ ] `XrmToolBox` is the **first** tag
- [ ] The dependency on `XrmToolBox` matches the `XrmToolBoxPackage` version used to build
- [ ] `iconUrl` points to `Resources/icon128.png` on `main`, and the file is pushed before publishing
- [ ] The DLL is at `lib\net48\Plugins\PluginFailureCorrelationExplorer.dll`, and the README is at `docs\README.md` (the workflow checks both)
- [ ] `releaseNotes` describes this version
- [ ] `license` is `MIT`; `projectUrl` and `repository` are correct

## Release

1. Bump the version in the csproj and the nuspec, and add release notes to the README and CHANGELOG.
2. Merge to `main`. The workflow builds, tests, packs and checks the layout, then publishes the version if it isn't on nuget.org yet, and creates the GitHub release `PluginFailureCorrelationExplorer-v<version>`.
3. Wait until the package is **listed** on nuget.org. This usually takes 10–60 minutes.
4. First release only: submit `VikashKumar.XrmToolBox.PluginFailureCorrelationExplorer` on the XrmToolBox tool submission page.
5. After approval, install the tool from the Tool Library on a clean XrmToolBox and run a quick smoke test (steps C1, C2 and R1 of the test plan).

## After each release

- [ ] Check that the GitHub release has the `.nupkg` attached
- [ ] Respond to issues and reviews on the Tool Library page
