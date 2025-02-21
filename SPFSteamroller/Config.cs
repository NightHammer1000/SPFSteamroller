using System;
using System.IO;
using System.Collections.Generic;

namespace SPFSteamroller
{
    /// <summary>
    /// Manages configuration settings for the SPF Steamroller application.
    /// </summary>
    public class Config
    {
        private Dictionary<string, string> _settings = new Dictionary<string, string>();
        
        /// <summary>
        /// The default path for the configuration file.
        /// </summary>
        private const string DefaultConfigPath = "config.ini";

        /// <summary>
        /// Gets the email address for CloudFlare authentication.
        /// </summary>
        public string Email => GetSetting("email");

        /// <summary>
        /// Gets the API key for CloudFlare authentication.
        /// </summary>
        public string ApiKey => GetSetting("apiKey");

        /// <summary>
        /// Gets the CloudFlare zone ID for the domain.
        /// </summary>
        public string ZoneId => GetSetting("zoneId");

        /// <summary>
        /// Gets the domain for SPF updates.
        /// </summary>
        public string Domain => GetSetting("domain");

        /// <summary>
        /// Gets the source domain with "_masterspf." prefix for fetching original SPF.
        /// </summary>
        public string SourceDomain => $"_masterspf.{Domain}";

        /// <summary>
        /// Gets whether to update CloudFlare DNS records.
        /// </summary>
        public bool UpdateCloudflare => bool.Parse(GetSetting("updateCloudflare", "false"));

        /// <summary>
        /// Initializes a new instance of the Config class.
        /// </summary>
        /// <remarks>
        /// Creates a default configuration file if none exists and exits the application.
        /// </remarks>
        public Config()
        {
            if (!File.Exists(DefaultConfigPath))
            {
                CreateDefaultConfig();
            }
            LoadConfig();
        }

        /// <summary>
        /// Creates a default configuration file with example values.
        /// </summary>
        private void CreateDefaultConfig()
        {
            var defaultConfig = @"[Cloudflare]
email=administrator@example.com
apiKey=your-api-key-here
zoneId=your-zone-id
domain=example.com
updateCloudflare=false";

            File.WriteAllText(DefaultConfigPath, defaultConfig);
            Environment.Exit(0);
        }

        /// <summary>
        /// Loads configuration settings from the config file.
        /// </summary>
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

        /// <summary>
        /// Retrieves a setting value by its key.
        /// </summary>
        /// <param name="key">The key of the setting to retrieve.</param>
        /// <param name="defaultValue">The default value to return if the key is not found.</param>
        /// <returns>The value of the setting, or the default value if not found.</returns>
        private string GetSetting(string key, string defaultValue = "")
        {
            return _settings.TryGetValue(key.ToLower(), out var value) ? value : defaultValue;
        }
    }
}
