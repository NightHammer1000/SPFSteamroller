using System;
using System.IO;
using System.Collections.Generic;

namespace SPFSteamroller
{
    public class Config
    {
        private Dictionary<string, string> _settings = new Dictionary<string, string>();
        private const string DefaultConfigPath = "config.ini";

        public string Email => GetSetting("email");
        public string ApiKey => GetSetting("apiKey");
        public string ZoneId => GetSetting("zoneId");
        public string Domain => GetSetting("domain");
        public bool UpdateCloudflare => bool.Parse(GetSetting("updateCloudflare", "false"));

        public Config()
        {
            if (!File.Exists(DefaultConfigPath))
            {
                CreateDefaultConfig();
            }
            LoadConfig();
        }

        private void CreateDefaultConfig()
        {
            var defaultConfig = @"[Cloudflare]
email=administrator@example.com
apiKey=your-api-key-here
zoneId=your-zone-id
domain=example.com
updateCloudflare=false";

            File.WriteAllText(DefaultConfigPath, defaultConfig);
            Console.WriteLine($"Created default config file at {DefaultConfigPath}");
            Console.WriteLine("Please edit the config file with your settings and restart the application.");
            Environment.Exit(0);
        }

        private void LoadConfig()
        {
            foreach (var line in File.ReadAllLines(DefaultConfigPath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("[") || line.StartsWith(";"))
                    continue;

                var parts = line.Split(new[] { '=' }, 2);
                if (parts.Length == 2)
                {
                    _settings[parts[0].Trim().ToLower()] = parts[1].Trim();
                }
            }
        }

        private string GetSetting(string key, string defaultValue = "")
        {
            return _settings.TryGetValue(key.ToLower(), out var value) ? value : defaultValue;
        }
    }
}
