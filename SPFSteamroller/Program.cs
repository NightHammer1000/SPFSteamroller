using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;
using CloudFlare.Client;
using CloudFlare.Client.Api.Zones;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using System.Text.RegularExpressions;
using System.IO;

namespace SPFSteamroller
{
    /// <summary>
    /// Main entry point for the SPF Steamroller application.
    /// Handles the orchestration of SPF record flattening and updating.
    /// </summary>
    class Program
    {
        private static Config config;

        /// <summary>
        /// Entry point of the application.
        /// </summary>
        /// <param name="args">Command line arguments (not used).</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        static async Task Main(string[] args)
        {
            try
            {
                Logger.Info("Starting SPF Steamroller");
                
                // Check for restore switch
                if (args.Length >= 2 && args[0].Equals("-RestoreBackup", StringComparison.OrdinalIgnoreCase))
                {
                    await RestoreFromBackup(args[1]);
                    return;
                }

                config = new Config();

                if (string.IsNullOrEmpty(config.Domain))
                {
                    Logger.Error("Domain is required in configuration");
                    return;
                }

                if (string.IsNullOrEmpty(config.Email) || string.IsNullOrEmpty(config.ApiKey))
                {
                    Logger.Error("Email and API Key are required in the configuration");
                    return;
                }

                Logger.Info($"Processing domain: {config.Domain}");

                try
                {
                    var cloudFlareService = new CloudFlareService(config.Email, config.ApiKey, config.Domain, config.ZoneId);
                    if (!await cloudFlareService.Initialize())
                    {
                        Logger.Error("Failed to initialize CloudFlare service. Aborting.");
                        return;
                    }

                    Logger.Info("Flattening SPF records...");
                    var spfResolver = new SpfResolver();
                    var flattenedIps = await spfResolver.FlattenSpf(config.SourceDomain);

                    if (!flattenedIps.Any())
                    {
                        Logger.Error("No IP addresses found in SPF records");
                        return;
                    }

                    Logger.Info($"Found {flattenedIps.Count} unique IP addresses");
                    var spfParser = new SpfParser();
                    List<string> reconstructedSpfRecords = spfParser.ReconstructAndSplitSpf(flattenedIps, config.Domain);

                    try
                    {
                        await OutputToFile(reconstructedSpfRecords, "output.txt");
                    }
                    catch (IOException ex)
                    {
                        Logger.Error($"Failed to write to output file: {ex.Message}");
                        return;
                    }

                    if (config.UpdateCloudflare)
                    {
                        Logger.Info("Updating CloudFlare DNS records...");
                        await cloudFlareService.UpdateSpfRecords(reconstructedSpfRecords);
                        Logger.Info("CloudFlare DNS records updated successfully");
                    }
                }
                catch (CloudFlare.Client.Exceptions.PersistenceUnavailableException ex)
                {
                    Logger.Error($"CloudFlare API error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Unexpected error during CloudFlare operations: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Critical error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Writes the processed SPF records to a file.
        /// </summary>
        /// <param name="records">The SPF records to write.</param>
        /// <param name="filePath">The path to the output file.</param>
        /// <exception cref="UnauthorizedAccessException">Thrown when access to the file is denied.</exception>
        /// <exception cref="IOException">Thrown when an I/O error occurs.</exception>
        static async Task OutputToFile(List<string> records, string filePath)
        {
            if (records == null || !records.Any())
            {
                Logger.Warning("No records to write to file");
                return;
            }

            try
            {
                Logger.Info($"Writing SPF records to {filePath}");
                using (StreamWriter writer = new StreamWriter(filePath))
                {
                    for (int i = 0; i < records.Count; i++)
                    {
                        string domain = i == 0 ? config.Domain : SpfParser.GetSpfSubdomain(config.Domain, i);
                        await writer.WriteLineAsync($"[{domain}]");
                        await writer.WriteLineAsync(records[i]);
                        await writer.WriteLineAsync();
                    }
                }
                Logger.Info($"Successfully wrote {records.Count} records to {filePath}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error($"Access denied writing to {filePath}: {ex.Message}");
                throw;
            }
            catch (IOException ex)
            {
                Logger.Error($"IO error writing to {filePath}: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"Unexpected error writing to {filePath}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Restores SPF records from a backup file.
        /// </summary>
        /// <param name="backupPath">Path to the backup file.</param>
        private static async Task RestoreFromBackup(string backupPath)
        {
            try
            {
                if (!File.Exists(backupPath))
                {
                    Logger.Error($"Backup file not found: {backupPath}");
                    return;
                }

                config = new Config();
                if (string.IsNullOrEmpty(config.Email) || string.IsNullOrEmpty(config.ApiKey))
                {
                    Logger.Error("Email and API Key are required in the configuration");
                    return;
                }

                var cloudFlareService = new CloudFlareService(config.Email, config.ApiKey, config.Domain, config.ZoneId);
                if (!await cloudFlareService.Initialize())
                {
                    Logger.Error("Failed to initialize CloudFlare service. Aborting.");
                    return;
                }

                Logger.Info($"Restoring from backup: {backupPath}");
                await cloudFlareService.RestoreFromBackup(backupPath);
                Logger.Info("Restore completed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to restore from backup: {ex.Message}");
            }
        }
    }
}