using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using DataQualityStudio.Models;

namespace DataQualityStudio.Services
{
    /// <summary>Saves mapping profiles as readable JSON files in the XrmToolBox settings folder (or any file the user picks).</summary>
    public static class ProfileStore
    {
        public const string Extension = ".dqprofile.json";

        public static string Folder
        {
            get
            {
                string root;
                try { root = XrmToolBox.Extensibility.Paths.SettingsPath; }
                catch { root = null; }
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MscrmTools", "XrmToolBox", "Settings");
                var folder = Path.Combine(root, "DataQualityStudio", "Profiles");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public static List<string> ListProfiles()
        {
            try
            {
                return Directory.GetFiles(Folder, "*" + Extension)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        public static string NameFromPath(string path)
        {
            var name = Path.GetFileName(path) ?? "";
            return name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - Extension.Length) : Path.GetFileNameWithoutExtension(name);
        }

        public static string PathForName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            if (safe.Length == 0) safe = "Profile";
            return Path.Combine(Folder, safe + Extension);
        }

        public static void Save(MappingProfile profile, string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(MappingProfile));
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
            {
                serializer.WriteObject(writer, profile);
                writer.Flush();
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static MappingProfile Load(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(MappingProfile));
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var profile = (MappingProfile)serializer.ReadObject(fs);
                if (profile == null) throw new InvalidDataException("The profile file is empty.");
                profile.Mappings = profile.Mappings ?? new List<ColumnMapping>();
                if (string.IsNullOrEmpty(profile.Name)) profile.Name = NameFromPath(path);
                if (profile.HeaderRow < 1) profile.HeaderRow = 1;
                return profile;
            }
        }
    }
}
