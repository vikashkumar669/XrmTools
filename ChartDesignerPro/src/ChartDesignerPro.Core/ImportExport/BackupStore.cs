using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChartDesignerPro.Abstractions;
using ChartDesignerPro.Models;

namespace ChartDesignerPro.ImportExport
{
    /// <summary>A backup file on disk.</summary>
    public sealed class BackupEntry
    {
        public string Path { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string ChartName { get; set; }
        public Guid ChartId { get; set; }
    }

    /// <summary>Minimal file system access, so the backup store can be tested without touching the disk.</summary>
    public interface IFileStore
    {
        void WriteAllText(string path, string text);
        string ReadAllText(string path);
        bool DirectoryExists(string path);
        void CreateDirectory(string path);
        IEnumerable<string> GetFiles(string directory, string pattern);
    }

    public sealed class DiskFileStore : IFileStore
    {
        public void WriteAllText(string path, string text) => File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        public string ReadAllText(string path) => File.ReadAllText(path);
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public IEnumerable<string> GetFiles(string directory, string pattern) => Directory.Exists(directory) ? Directory.GetFiles(directory, pattern) : new string[0];
    }

    /// <summary>
    /// Keeps the chart as it was in Dataverse before a change, as Chart Designer Pro JSON files:
    /// {root}\{environment}\{table} - {chart} - {id8} - {yyyyMMdd-HHmmss}.json. A backup is a recovery copy, not a
    /// rollback: restoring means importing the file into the chart and saving it again.
    /// </summary>
    public sealed class BackupStore
    {
        private readonly IFileStore _files;
        private readonly IClock _clock;

        public BackupStore(string root, IFileStore files = null, IClock clock = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentNullException(nameof(root));
            Root = root;
            _files = files ?? new DiskFileStore();
            _clock = clock ?? SystemClock.Instance;
        }

        public string Root { get; }

        public string FolderFor(string environmentName) => Path.Combine(Root, ChartFileFormat.SafeFileName(environmentName ?? "environment", 80));

        /// <summary>Writes the stored (original) XML of the chart and returns the file path.</summary>
        public string Save(ChartRecord original, string environmentName, string environmentUrl, string toolVersion)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (!original.HasXml) throw new InvalidOperationException("The chart's XML wasn't retrieved, so there is nothing to back up.");
            var now = _clock.UtcNow;
            var folder = FolderFor(environmentName);
            if (!_files.DirectoryExists(folder)) _files.CreateDirectory(folder);
            var name = $"{ChartFileFormat.SafeFileName(original.TableLogicalName, 40)} - {ChartFileFormat.SafeFileName(original.Name, 60)} - {original.Id.ToString("N").Substring(0, 8)} - {now:yyyyMMdd-HHmmss}.json";
            var path = Path.Combine(folder, name);
            var file = ChartFile.From(original, original.DataXml, original.PresentationXml, environmentName, environmentUrl, toolVersion, now);
            _files.WriteAllText(path, ChartFileFormat.WriteJson(file));
            return path;
        }

        /// <summary>Backups of one chart in one environment, newest first. Unreadable files are skipped.</summary>
        public IReadOnlyList<BackupEntry> List(string environmentName, Guid chartId)
        {
            var folder = FolderFor(environmentName);
            var marker = " - " + chartId.ToString("N").Substring(0, 8) + " - ";
            var result = new List<BackupEntry>();
            foreach (var path in _files.GetFiles(folder, "*.json").Where(p => Path.GetFileName(p).Contains(marker)))
            {
                try
                {
                    var file = ChartFileFormat.ReadJson(_files.ReadAllText(path));
                    if (!Guid.TryParse(file.Source?.Id, out var id) || id != chartId) continue;
                    DateTime.TryParse(file.ExportedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var created);
                    result.Add(new BackupEntry { Path = path, ChartId = id, ChartName = file.Name, CreatedUtc = created });
                }
                catch (ChartFileException) { }
                catch (IOException) { }
            }
            return result.OrderByDescending(b => b.CreatedUtc).ToList();
        }
    }
}
