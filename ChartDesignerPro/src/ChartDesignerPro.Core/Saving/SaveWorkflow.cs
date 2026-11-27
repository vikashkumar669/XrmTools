using System;
using System.Collections.Generic;
using System.Linq;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Comparison;
using ChartDesignerPro.Editing;
using ChartDesignerPro.ImportExport;
using ChartDesignerPro.Models;
using ChartDesignerPro.Validation;

namespace ChartDesignerPro.Saving
{
    /// <summary>Whether the open chart can be saved, before anything is sent to Dataverse.</summary>
    public sealed class SaveReadiness
    {
        public bool CanSave { get; set; }

        /// <summary>Plain-language reason when <see cref="CanSave"/> is false.</summary>
        public string BlockReason { get; set; }

        public IReadOnlyList<ValidationResult> Errors { get; set; } = new ValidationResult[0];
        public IReadOnlyList<ValidationResult> Warnings { get; set; } = new ValidationResult[0];

        /// <summary>Warnings don't block, but the user must tick a box to acknowledge them.</summary>
        public bool RequiresAcknowledgement => CanSave && Warnings.Count > 0;
    }

    public enum ConflictKind
    {
        /// <summary>The chart in Dataverse is the version that was opened.</summary>
        None,

        /// <summary>The chart no longer exists.</summary>
        Deleted,

        /// <summary>Someone (or something) changed the chart after it was opened.</summary>
        ChangedSinceOpened
    }

    public sealed class ConflictCheck
    {
        public ConflictKind Kind { get; set; }

        /// <summary>The chart as it is in Dataverse now (null when deleted).</summary>
        public ChartRecord Current { get; set; }

        public string Message { get; set; }
    }

    /// <summary>What the user confirmed in the save dialog.</summary>
    public sealed class SaveRequest
    {
        public ChartWorkspace Workspace { get; set; }

        /// <summary>The chart as re-retrieved just before saving. This is the version that is backed up and replaced.</summary>
        public ChartRecord CurrentInDataverse { get; set; }

        /// <summary>System charts only: publish the chart's table after the update.</summary>
        public bool Publish { get; set; } = true;

        public string EnvironmentName { get; set; }
        public string EnvironmentUrl { get; set; }
        public string ToolVersion { get; set; }
    }

    public enum VerifyOutcome
    {
        NotChecked,

        /// <summary>Dataverse returned exactly the text that was sent.</summary>
        Exact,

        /// <summary>Dataverse returned the same XML with different formatting.</summary>
        Reformatted,

        /// <summary>Dataverse returned different XML content.</summary>
        Different,

        /// <summary>The chart couldn't be read back.</summary>
        Unavailable
    }

    public sealed class SaveResult
    {
        /// <summary>True when the update reached Dataverse.</summary>
        public bool Saved { get; set; }

        public bool Published { get; set; }

        /// <summary>System chart saved without publishing (by choice or because publishing failed).</summary>
        public bool NeedsPublish { get; set; }

        public string BackupPath { get; set; }
        public Exception BackupError { get; set; }
        public Exception UpdateError { get; set; }
        public Exception PublishError { get; set; }
        public VerifyOutcome Verify { get; set; } = VerifyOutcome.NotChecked;
        public Exception VerifyError { get; set; }

        /// <summary>The chart as read back after the save; null when it couldn't be read.</summary>
        public ChartRecord SavedRecord { get; set; }

        /// <summary>The columns that were sent, e.g. "datadescription".</summary>
        public List<string> ColumnsSent { get; } = new List<string>();
    }

    /// <summary>
    /// The save sequence from the Step 2 architecture (section 8). The UI calls <see cref="CheckReadiness"/>,
    /// <see cref="CheckConflict"/> and shows the confirmation dialog; <see cref="Execute"/> then backs up, updates
    /// the changed columns only, publishes the table (system charts) and reads the chart back to verify it.
    /// Nothing here touches the UI, so every step is tested with fakes.
    /// </summary>
    public sealed class SaveWorkflow
    {
        private readonly IChartRepository _repository;
        private readonly BackupStore _backups;
        private readonly ILog _log;

        public SaveWorkflow(IChartRepository repository, BackupStore backups, ILog log = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _backups = backups ?? throw new ArgumentNullException(nameof(backups));
            _log = log;
        }

        /// <summary>Step ①–③: capability, well-formed XML, and validation errors block; warnings need acknowledging.</summary>
        public static SaveReadiness CheckReadiness(ChartWorkspace workspace, ValidationReport report)
        {
            if (workspace == null) return Blocked("No chart is open.");
            if (!workspace.Capability.CanEdit) return Blocked(workspace.Capability.Reason);
            if (!workspace.IsDirty) return Blocked("There are no changes to save.");
            foreach (var doc in new[] { workspace.Data, workspace.Presentation })
            {
                if (!doc.IsDirty) continue;
                doc.Parse(out var error);
                if (error != null) return Blocked($"The {doc.Title} isn't well-formed ({error}). Fix it before saving.");
            }
            if (report == null) return Blocked("The chart hasn't been validated yet.");

            var errors = report.Results.Where(r => r.Severity == Severity.Error).ToList();
            var warnings = report.Results.Where(r => r.Severity == Severity.Warning).ToList();
            if (errors.Count > 0)
                return new SaveReadiness
                {
                    CanSave = false,
                    BlockReason = $"Validation found {errors.Count} error(s). Fix them before saving (see the Validation tab).",
                    Errors = errors,
                    Warnings = warnings
                };
            return new SaveReadiness { CanSave = true, Warnings = warnings };
        }

        private static SaveReadiness Blocked(string reason) => new SaveReadiness { CanSave = false, BlockReason = reason };

        /// <summary>
        /// Step ④: compares the chart as it is now with the version that was opened. A different modified-on time or
        /// different stored XML means someone else saved it in the meantime.
        /// </summary>
        public static ConflictCheck CheckConflict(ChartRecord opened, ChartRecord current)
        {
            if (opened == null) throw new ArgumentNullException(nameof(opened));
            if (current == null)
                return new ConflictCheck { Kind = ConflictKind.Deleted, Message = $"'{opened.Name}' no longer exists in this environment, so it can't be saved." };

            var timeChanged = opened.ModifiedOnUtc.HasValue && current.ModifiedOnUtc.HasValue && opened.ModifiedOnUtc.Value != current.ModifiedOnUtc.Value;
            var xmlChanged = current.HasXml && opened.HasXml &&
                             (!string.Equals(opened.DataXml ?? "", current.DataXml ?? "", StringComparison.Ordinal) ||
                              !string.Equals(opened.PresentationXml ?? "", current.PresentationXml ?? "", StringComparison.Ordinal));
            if (!timeChanged && !xmlChanged)
                return new ConflictCheck { Kind = ConflictKind.None, Current = current };

            var who = string.IsNullOrEmpty(current.ModifiedByName) ? "someone" : current.ModifiedByName;
            var when = current.ModifiedOnUtc.HasValue ? " on " + current.ModifiedOnUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "";
            var what = xmlChanged ? "Its XML is different from the version you opened." : "Its XML is the same, but other columns may have changed.";
            return new ConflictCheck
            {
                Kind = ConflictKind.ChangedSinceOpened,
                Current = current,
                Message = $"'{opened.Name}' was changed by {who}{when}, after you opened it. {what}"
            };
        }

        /// <summary>Steps ⑥–⑨. Never throws for Dataverse failures: each one is reported in the result.</summary>
        public SaveResult Execute(SaveRequest request)
        {
            if (request?.Workspace == null) throw new ArgumentNullException(nameof(request));
            var ws = request.Workspace;
            var record = ws.Record;
            var result = new SaveResult();

            // ⑥ Back up the version that is about to be replaced. No backup, no save.
            try
            {
                result.BackupPath = _backups.Save(request.CurrentInDataverse ?? record, request.EnvironmentName, request.EnvironmentUrl, request.ToolVersion);
            }
            catch (Exception ex)
            {
                result.BackupError = ex;
                _log?.Warn("Backup before saving chart {0} failed: {1}", record.Id, ex.Message);
                return result;
            }

            // ⑦ Update the changed columns only.
            var data = ws.Data.IsDirty ? ws.Data.Current ?? string.Empty : null;
            var presentation = ws.Presentation.IsDirty ? ws.Presentation.Current ?? string.Empty : null;
            if (data != null) result.ColumnsSent.Add("datadescription");
            if (presentation != null) result.ColumnsSent.Add("presentationdescription");
            try
            {
                _repository.Update(record.Scope, record.Id, data, presentation);
                result.Saved = true;
                _log?.Info("Saved chart {0} ({1}): {2}", record.Id, record.Scope, string.Join(", ", result.ColumnsSent));
            }
            catch (Exception ex)
            {
                result.UpdateError = ex;
                _log?.Error(ex, "Saving chart {0} failed", record.Id);
                return result; // nothing is published after a failed update
            }

            // ⑧ Publish the chart's table (system charts only).
            if (record.Scope == ChartScope.System)
            {
                if (request.Publish && !string.IsNullOrEmpty(record.TableLogicalName))
                {
                    try
                    {
                        _repository.PublishTable(record.TableLogicalName);
                        result.Published = true;
                    }
                    catch (Exception ex)
                    {
                        result.PublishError = ex;
                        result.NeedsPublish = true;
                        _log?.Error(ex, "Publishing {0} after saving chart {1} failed", record.TableLogicalName, record.Id);
                    }
                }
                else
                {
                    result.NeedsPublish = true;
                }
            }

            // ⑨ Read the chart back and compare with what was sent.
            try
            {
                var saved = _repository.Retrieve(record.Scope, record.Id);
                result.SavedRecord = saved;
                result.Verify = saved == null ? VerifyOutcome.Unavailable : Compare(data, presentation, saved);
            }
            catch (Exception ex)
            {
                result.VerifyError = ex;
                result.Verify = VerifyOutcome.Unavailable;
            }
            return result;
        }

        private static VerifyOutcome Compare(string sentData, string sentPresentation, ChartRecord saved)
        {
            var outcome = VerifyOutcome.Exact;
            foreach (var pair in new[] { Tuple.Create(sentData, saved.DataXml), Tuple.Create(sentPresentation, saved.PresentationXml) })
            {
                if (pair.Item1 == null) continue; // not sent
                if (string.Equals(pair.Item1, pair.Item2 ?? "", StringComparison.Ordinal)) continue;
                if (!LineDiff.Compare(pair.Item1, pair.Item2 ?? "", ignoreFormatting: true).HasChanges) { outcome = VerifyOutcome.Reformatted; continue; }
                return VerifyOutcome.Different;
            }
            return outcome;
        }
    }
}
