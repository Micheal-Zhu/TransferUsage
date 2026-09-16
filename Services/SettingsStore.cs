using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using BalanceDock.Models;

namespace BalanceDock.Services
{
    public sealed class SettingsStore
    {
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private readonly string _root;
        private readonly string _stationsPath;
        private readonly string _settingsPath;

        public string WebViewDataFolder { get { return Path.Combine(_root, "WebView2"); } }

        public SettingsStore()
        {
            string overrideRoot = Environment.GetEnvironmentVariable("BALANCEDOCK_DATA_DIR");
            _root = string.IsNullOrWhiteSpace(overrideRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalanceDock")
                : Path.GetFullPath(overrideRoot);
            _stationsPath = Path.Combine(_root, "stations.json");
            _settingsPath = Path.Combine(_root, "settings.json");
            Directory.CreateDirectory(_root);
        }

        public IList<StationRecord> LoadStations()
        {
            return Load(_stationsPath, new List<StationRecord>());
        }

        public void SaveStations(IEnumerable<StationRecord> stations)
        {
            Save(_stationsPath, new List<StationRecord>(stations));
        }

        public AppSettings LoadSettings()
        {
            return Load(_settingsPath, new AppSettings());
        }

        public void SaveSettings(AppSettings settings)
        {
            Save(_settingsPath, settings);
        }

        private T Load<T>(string path, T fallback)
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                string json = File.ReadAllText(path, Encoding.UTF8);
                var value = _serializer.Deserialize<T>(json);
                return value == null ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }

        private void Save<T>(string path, T value)
        {
            string temporary = path + ".tmp";
            string json = _serializer.Serialize(value);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
