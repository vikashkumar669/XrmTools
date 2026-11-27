# Troubleshooting

Technical details of every error are written to the XrmToolBox log (**Help › Open log file**). The tool never logs passwords, tokens or connection strings.

## Installing and loading

**The tool doesn't appear in XrmToolBox.**
Check that `ChartDesignerPro.dll` is in `%APPDATA%\MscrmTools\XrmToolBox\Plugins` and that XrmToolBox is 1.2025.10.74 or later. Restart XrmToolBox after installing.

**Building in Visual Studio fails with MSB3027 / MSB3021 (file locked).**
XrmToolBox is still running and has the DLL loaded. Close every XrmToolBox window and build again.

## Listing charts

**No personal charts are listed.**
Dataverse returns only your own personal charts and those shared with you. If your role can't read personal charts at all, the system charts still load and a warning explains why.

**A chart I just created isn't in the list.**
Click **▶ Load charts** (F5) to read the list again.

## Editing

**✎ Edit is greyed out.**
Hover over it to see why. Power BI and web-resource visualizations, and managed charts whose *IsCustomizable* is false, are read-only.

**Validation says a column check was "not checked".**
The table's metadata couldn't be read. Click **⟳ Metadata**, open the chart again, and check that you can read the table.

## Saving

**💾 Save is greyed out.**
Save needs edit mode, unsaved changes and a connection. Click **✎ Edit**, make a change, and Save becomes available.

**"The chart can't be saved yet."**
Validation found errors. The Validation tab opens with them listed; double-click one to go to the line. Errors block saving because Dataverse would reject the chart or the chart wouldn't load.

**"The chart changed" when saving.**
Someone saved the chart after you opened it. Choose **No** to stop. To start from their version, export your edits (⇩ Export), discard them and open the chart again. Choose **Yes** to replace their version; it is backed up first.

**"… wasn't saved" with a privilege message.**
Saving a system chart needs write access to *System Chart* (`savedqueryvisualization`); a personal chart needs write access to your *User Chart* (`userqueryvisualization`). Nothing was changed, and your edits are still open.

**"Not published" after saving a system chart.**
The update is saved but users won't see it until the table is published. Click **⇪ Publish table** to try again, or publish the table from the maker portal.

**The app still shows the old chart.**
- System charts need publishing. Check the save message said the table was published.
- Refresh the browser page; the app caches charts.
- If colours didn't change, the chart may need `CustomColorOverride="true"` (rule CDP-U006).

**"The chart read back from Dataverse differs from what was sent."**
The save went through, but Dataverse stored something different. Open the chart and compare it with the backup.

## Going back to an earlier version

There is no automatic undo. Every save first backs up the version in Dataverse to:

`%APPDATA%\MscrmTools\XrmToolBox\Settings\ChartDesignerPro\Backups\<environment>\`

To restore one:
1. Open the chart.
2. Click **⇧ Import…** and choose the backup.
3. Check the Diff, then click **💾 Save**.

**⇩ Export › Open backup folder** opens the folder.

## Import and export

**The app's Import Chart rejects a file ("Required nodes missing … visualizationid").**
The file has no `<visualizationid>`. Export it again with this version of the tool, which writes one.

**The app's Import Chart rejects a large file.**
The app limits chart files to 16,000 base64 characters (rule CDP-S020). Remove presentation properties the app doesn't use (rule CDP-U002 lists them), or save with this tool instead, which has no such limit.
