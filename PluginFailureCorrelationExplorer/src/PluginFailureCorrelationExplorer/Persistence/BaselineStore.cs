using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PluginFailureCorrelationExplorer.Analysis;
using PluginFailureCorrelationExplorer.Models;
using XrmToolBox.Extensibility;

namespace PluginFailureCorrelationExplorer.Persistence
{
    /// <summary>
    /// Local, opt-in store of aggregate baseline snapshots, one JSON file per connection in the XrmToolBox settings
    /// folder. Snapshots hold counts and statistics only (see <see cref="BaselineSnapshot"/>). Nothing is written to Dataverse.
    /// </summary>
    public sealed class BaselineStore
    {
        /// <summary>Snapshots kept per connection; the oldest are dropped.</summary>
        public const int MaxSnapshots = 20;

        private readonly string _path;

        public BaselineStore(string connectionKey)
        {
            var safe = new string((connectionKey ?? "default").Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
            var folder = Path.Combine(Paths.SettingsPath, "PluginFailureCorrelationExplorer");
            _path = Path.Combine(folder, "baselines-" + safe + ".json");
        }

        public string FilePath => _path;

        public List<BaselineSnapshot> Load()
        {
            try
            {
                return File.Exists(_path)
                    ? BaselineService.Deserialize(File.ReadAllText(_path, Encoding.UTF8)).OrderByDescending(s => s.CapturedUtc).ToList()
                    : new List<BaselineSnapshot>();
            }
            catch (IOException) { return new List<BaselineSnapshot>(); }
            catch (UnauthorizedAccessException) { return new List<BaselineSnapshot>(); }
        }

        public void Add(BaselineSnapshot snapshot)
        {
            if (snapshot == null) return;
            var list = Load();
            list.Insert(0, snapshot);
            Save(list.OrderByDescending(s => s.CapturedUtc).Take(MaxSnapshots).ToList());
        }

        public void Clear()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        private void Save(List<BaselineSnapshot> list)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var temp = _path + ".tmp";
            File.WriteAllText(temp, BaselineService.Serialize(list), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(temp, _path);
        }
    }
}
