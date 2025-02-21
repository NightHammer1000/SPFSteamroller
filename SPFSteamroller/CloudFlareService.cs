using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CloudFlare.Client;
using CloudFlare.Client.Api.Zones;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using System.IO;

namespace SPFSteamroller
{
    /// <summary>
    /// Manages interactions with the CloudFlare API for SPF record management.
    /// </summary>
    public class CloudFlareService
    {
        private readonly CloudFlareClient _client;
        private readonly string _domain;
        private string _zoneId;

        /// <summary>
        /// Initializes a new instance of the CloudFlareService class.
        /// </summary>
        /// <param name="email">The CloudFlare account email.</param>
        /// <param name="apiKey">The CloudFlare API key.</param>
        /// <param name="domain">The domain to manage.</param>
        /// <param name="zoneId">Optional zone ID if known in advance.</param>
        public CloudFlareService(string email, string apiKey, string domain, string zoneId = null)
        {
            _client = new CloudFlareClient(email, apiKey);
            _domain = domain;
            _zoneId = zoneId;
        }

        /// <summary>
        /// Initializes the service by retrieving the zone ID if not provided.
        /// </summary>
        /// <returns>True if initialization was successful, false otherwise.</returns>
        public async Task<bool> Initialize()
        {
            Logger.Info("Initializing CloudFlare service...");
            if (string.IsNullOrEmpty(_zoneId))
            {
                Logger.Info($"No zone ID provided, attempting to retrieve for domain {_domain}...");
                _zoneId = await GetZoneId();
                if (!string.IsNullOrEmpty(_zoneId))
                {
                    Logger.Info($"Successfully retrieved zone ID: {_zoneId}");
                    return true;
                }
                return false;
            }
            Logger.Info($"Using provided zone ID: {_zoneId}");
            return true;
        }

        /// <summary>
        /// Retrieves the zone ID for the domain from CloudFlare.
        /// </summary>
        /// <returns>The zone ID if found, null otherwise.</returns>
        private async Task<string> GetZoneId()
        {
            Logger.Info("Requesting zones from CloudFlare API...");
            var zones = await _client.Zones.GetAsync();
            if (!zones.Success)
            {
                Logger.Error($"Failed to retrieve zones. Response success: {zones.Success}");
                return null;
            }

            Logger.Info($"Retrieved {zones.Result.Count()} zones");
            var zone = zones.Result.FirstOrDefault(z => z.Name == _domain);
            if (zone == null)
            {
                Logger.Error($"Domain {_domain} not found in available zones");
                return null;
            }
            return zone.Id;
        }

        /// <summary>
        /// Updates the SPF records in CloudFlare with new values.
        /// </summary>
        /// <param name="spfRecords">The new SPF records to set.</param>
        /// <exception cref="Exception">Thrown when CloudFlare API operations fail.</exception>
        public async Task UpdateSpfRecords(List<string> spfRecords)
        {
            Logger.Info($"Beginning SPF record update process for {_domain}");
            Logger.Info($"Retrieving all TXT records for domain and subdomains...");
            
            // Get all TXT records for the domain and its subdomains
            var existingTxtRecords = await _client.Zones.DnsRecords.GetAsync(_zoneId, new DnsRecordFilter { Type = DnsRecordType.Txt });
            if (!existingTxtRecords.Success)
            {
                Logger.Error($"Failed to get TXT records. API Response: {existingTxtRecords.Errors?.FirstOrDefault()?.Message}");
                throw new Exception($"Failed to get TXT records: {existingTxtRecords.Errors?.FirstOrDefault()?.Message}");
            }
            Logger.Info($"Found {existingTxtRecords.Result.Count()} total TXT records");

            // Filter records for both main domain and SPF subdomains
            var existingSpfRecords = FilterSpfRecords(existingTxtRecords.Result);
            Logger.Info($"Found {existingSpfRecords.Count()} SPF-related records");

            if (existingSpfRecords.Any())
            {
                Logger.Info("Found existing SPF records, creating backup...");
                await BackupExistingRecords(existingSpfRecords);
            }

            await DeleteExistingSpfRecords(existingTxtRecords.Result);
            await CreateNewSpfRecords(spfRecords);
        }

        /// <summary>
        /// Filters DNS records to only include SPF records.
        /// </summary>
        private IEnumerable<DnsRecord> FilterSpfRecords(IEnumerable<DnsRecord> records)
        {
            Logger.Info("Filtering SPF records from DNS records...");
            Regex spfRegex = new Regex(@"v=spf1\s", RegexOptions.IgnoreCase);
            Regex spfSubdomainRegex = new Regex(@"^_spf\d+\." + Regex.Escape(_domain) + "$");

            var filteredRecords = records.Where(record =>
            {
                // Skip _masterspf records
                if (record.Name.StartsWith("_masterspf."))
                {
                    Logger.Info($"Skipping master SPF record: {record.Name}");
                    return false;
                }

                // Check if it's a split SPF subdomain
                if (spfSubdomainRegex.IsMatch(record.Name))
                {
                    Logger.Info($"Found split SPF record: {record.Name}");
                    Logger.Info($"  Content: {record.Content}");
                    return true;
                }

                // Check if it's the main domain's SPF record
                if (record.Name == _domain)
                {
                    var content = record.Content.Trim('"');
                    if (content.Contains("v=spf1"))
                    {
                        Logger.Info($"Found main domain SPF record:");
                        Logger.Info($"  Content: {content}");
                        return true;
                    }
                }

                return false;
            });

            var result = filteredRecords.ToList();
            Logger.Info($"Found {result.Count} SPF records after filtering (excluding _masterspf)");
            return result;
        }

        /// <summary>
        /// Creates a backup of existing SPF records.
        /// </summary>
        private async Task BackupExistingRecords(IEnumerable<DnsRecord> records)
        {
            try
            {
                string backupFile = $"spfRecordBackup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
                Logger.Info($"Creating backup in {backupFile}");

                using (StreamWriter writer = new StreamWriter(backupFile))
                {
                    await writer.WriteLineAsync($"# SPF Record Backup for {_domain}");
                    await writer.WriteLineAsync($"# Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    await writer.WriteLineAsync();

                    foreach (var record in records)
                    {
                        await writer.WriteLineAsync($"[{record.Name}]");
                        await writer.WriteLineAsync(record.Content.Trim('"'));
                        await writer.WriteLineAsync();
                    }
                }

                Logger.Info("Backup created successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to create backup: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Deletes existing SPF records from CloudFlare.
        /// </summary>
        /// <param name="records">The records to evaluate for deletion.</param>
        private async Task DeleteExistingSpfRecords(IEnumerable<DnsRecord> records)
        {
            Logger.Info("Beginning deletion of existing SPF records...");
            Regex spfSubdomainRegex = new Regex(@"^_spf\d+\." + Regex.Escape(_domain) + "$");

            int deletedCount = 0;
            foreach (var record in records)
            {
                // Skip _masterspf records
                if (record.Name.StartsWith("_masterspf."))
                {
                    Logger.Info($"Preserving master SPF record: {record.Name}");
                    continue;
                }

                var content = record.Content.Trim('"');
                bool shouldDelete = false;

                // Check for split SPF subdomains
                if (spfSubdomainRegex.IsMatch(record.Name))
                {
                    shouldDelete = true;
                }
                // Check main domain SPF record
                else if (record.Name == _domain && content.Contains("v=spf1"))
                {
                    shouldDelete = true;
                }

                if (shouldDelete)
                {
                    Logger.Info($"Deleting record for {record.Name}...");
                    Logger.Info($"Content: {content}");
                    
                    var deleteResult = await _client.Zones.DnsRecords.DeleteAsync(_zoneId, record.Id);
                    if (!deleteResult.Success)
                    {
                        Logger.Error($"Failed to delete record {record.Name}. API Response: {deleteResult.Errors?.FirstOrDefault()?.Message}");
                        throw new Exception($"Failed to delete record: {deleteResult.Errors?.FirstOrDefault()?.Message}");
                    }
                    deletedCount++;
                    Logger.Info($"Successfully deleted record for {record.Name}");
                }
            }
            Logger.Info($"Completed deletion of {deletedCount} SPF records (preserved _masterspf)");
        }

        /// <summary>
        /// Creates new SPF records in CloudFlare.
        /// </summary>
        /// <param name="spfRecords">The SPF records to create.</param>
        private async Task CreateNewSpfRecords(List<string> spfRecords)
        {
            for (int i = 0; i < spfRecords.Count; i++)
            {
                string recordName = (i == 0) ? _domain : SpfParser.GetSpfSubdomain(_domain, i);
                string content = "\"" + spfRecords[i] + "\"";

                var existingRecord = await _client.Zones.DnsRecords.GetAsync(_zoneId, new DnsRecordFilter { Type = DnsRecordType.Txt, Name = recordName });
                if (existingRecord.Success && existingRecord.Result.Any())
                {
                    await UpdateDnsRecord(existingRecord.Result.First().Id, recordName, content);
                }
                else
                {
                    await CreateDnsRecord(recordName, content);
                }
            }
        }

        /// <summary>
        /// Updates an existing DNS record in CloudFlare.
        /// </summary>
        /// <param name="recordId">The ID of the record to update.</param>
        /// <param name="name">The new record name.</param>
        /// <param name="content">The new record content.</param>
        private async Task UpdateDnsRecord(string recordId, string name, string content)
        {
            Logger.Info($"Updating existing DNS record for {name}...");
            Logger.Info($"Record ID: {recordId}");
            Logger.Info($"New content: {content}");

            var updateRecord = new ModifiedDnsRecord
            {
                Type = DnsRecordType.Txt,
                Name = name,
                Content = content,
                Ttl = 1,
                Proxied = false
            };
            
            var updateResult = await _client.Zones.DnsRecords.UpdateAsync(_zoneId, recordId, updateRecord);
            if (!updateResult.Success)
            {
                Logger.Error($"Failed to update DNS record for {name}. API Response: {updateResult.Errors?.FirstOrDefault()?.Message}");
                throw new Exception($"Failed to update DNS record: {updateResult.Errors?.FirstOrDefault()?.Message}");
            }
            Logger.Info($"Successfully updated DNS record for {name}");
        }

        /// <summary>
        /// Creates a new DNS record in CloudFlare.
        /// </summary>
        /// <param name="name">The record name.</param>
        /// <param name="content">The record content.</param>
        private async Task CreateDnsRecord(string name, string content)
        {
            Logger.Info($"Creating new DNS record for {name}...");
            Logger.Info($"Record content: {content}");
            
            var newRecord = new NewDnsRecord
            {
                Type = DnsRecordType.Txt,
                Name = name,
                Content = content,
                Ttl = 1,
                Proxied = false
            };
            
            var createResult = await _client.Zones.DnsRecords.AddAsync(_zoneId, newRecord);
            if (!createResult.Success)
            {
                Logger.Error($"Failed to create DNS record for {name}. API Response: {createResult.Errors?.FirstOrDefault()?.Message}");
                throw new Exception($"Failed to create DNS record: {createResult.Errors?.FirstOrDefault()?.Message}");
            }
            Logger.Info($"Successfully created DNS record for {name}");
        }

        /// <summary>
        /// Restores SPF records from a backup file.
        /// </summary>
        /// <param name="backupPath">Path to the backup file.</param>
        public async Task RestoreFromBackup(string backupPath)
        {
            var records = ParseBackupFile(backupPath);
            if (!records.Any())
            {
                throw new Exception("No valid SPF records found in backup file");
            }

            // Delete existing SPF records first
            var existingRecords = await GetExistingSpfRecords();
            await DeleteExistingSpfRecords(existingRecords);

            // Restore records from backup
            foreach (var (name, content) in records)
            {
                await CreateDnsRecord(name, $"\"{content}\"");
                Logger.Info($"Restored record for {name}");
            }
        }

        /// <summary>
        /// Gets all existing SPF records.
        /// </summary>
        private async Task<IEnumerable<DnsRecord>> GetExistingSpfRecords()
        {
            var txtRecords = await _client.Zones.DnsRecords.GetAsync(_zoneId, new DnsRecordFilter { Type = DnsRecordType.Txt });
            if (!txtRecords.Success)
            {
                throw new Exception($"Failed to get existing records: {txtRecords.Errors?.FirstOrDefault()?.Message}");
            }

            return FilterSpfRecords(txtRecords.Result);
        }

        /// <summary>
        /// Parses a backup file into a list of domain/content pairs.
        /// </summary>
        private List<(string name, string content)> ParseBackupFile(string backupPath)
        {
            var records = new List<(string name, string content)>();
            string currentDomain = null;
            var lines = File.ReadAllLines(backupPath);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentDomain = line.Trim('[', ']');
                }
                else if (line.StartsWith("v=spf1") && currentDomain != null)
                {
                    records.Add((currentDomain, line));
                    currentDomain = null;
                }
            }

            return records;
        }
    }
}
