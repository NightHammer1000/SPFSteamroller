using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;

namespace SPFSteamroller
{
    /// <summary>
    /// Handles the parsing and reconstruction of SPF records.
    /// </summary>
    public class SpfParser
    {
        /// <summary>
        /// The maximum length allowed for an SPF record according to RFC 7208.
        /// </summary>
        private const int MaxSpfLength = 2000;

        /// <summary>
        /// Reconstructs and splits SPF records into multiple records if they exceed the maximum length.
        /// </summary>
        /// <param name="flattenedIps">The list of IP addresses and CIDR ranges to include in the SPF record.</param>
        /// <param name="originalDomain">The domain for which the SPF record is being created.</param>
        /// <returns>A list of SPF records that together form a complete SPF policy.</returns>
        /// <remarks>
        /// If the SPF record exceeds the maximum length, it will be split into multiple records
        /// using include mechanisms to chain them together.
        /// </remarks>
        public List<string> ReconstructAndSplitSpf(List<string> flattenedIps, string originalDomain)
        {
            var distinctIps = DeduplicateIps(flattenedIps)
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .ToList();

            Logger.Info($"Reconstructing SPF records with {distinctIps.Count} unique IPs after subnet deduplication");

            var records = new List<string>();
            var currentRecord = new StringBuilder("v=spf1");
            int splitCount = 0;
            const int MaxSplitLimit = 10;

            foreach (var ip in distinctIps)
            {
                string mechanism = GetMechanism(ip);

                if (currentRecord.Length + mechanism.Length + 1 > MaxSpfLength)
                {
                    currentRecord.Append(" -all");
                    records.Add(currentRecord.ToString());
                    currentRecord.Clear().Append("v=spf1");
                    splitCount++;

                    if (splitCount >= MaxSplitLimit)
                    {
                        Logger.Error($"SPF record requires more than {MaxSplitLimit} splits!");
                        Logger.Error("This exceeds the recommended limit for DNS lookups in SPF records.");
                        Logger.Error("Process aborted to prevent creating an invalid SPF configuration.");
                        throw new InvalidOperationException($"SPF record splitting exceeded maximum limit of {MaxSplitLimit} splits");
                    }
                }

                currentRecord.Append(" " + mechanism);
            }

            if (currentRecord.Length > "v=spf1".Length)
            {
                currentRecord.Append(" -all");
                records.Add(currentRecord.ToString());
                splitCount++;
            }

            return CreateFinalRecords(records, originalDomain, splitCount);
        }

        /// <summary>
        /// Creates the final set of SPF records with proper include chains if needed.
        /// </summary>
        /// <param name="records">The list of individual SPF record parts.</param>
        /// <param name="originalDomain">The domain for which the SPF record is being created.</param>
        /// <param name="splitCount">The number of splits needed.</param>
        /// <returns>A list of complete SPF records.</returns>
        private List<string> CreateFinalRecords(List<string> records, string originalDomain, int splitCount)
        {
            var finalRecords = new List<string>();

            if (splitCount > 1)
            {
                for (int i = 0; i < splitCount; i++)
                {
                    string record = records[i];
                    if (i < splitCount - 1)
                    {
                        record = record.Replace(" -all", $" include:{GetSpfSubdomain(originalDomain, i + 2)} -all");
                    }
                    finalRecords.Add(record);
                }

                finalRecords.Insert(0, $"v=spf1 include:{GetSpfSubdomain(originalDomain, 1)} -all");
            }
            else
            {
                finalRecords.Add(records[0]);
            }

            return finalRecords;
        }

        /// <summary>
        /// Converts an IP address or CIDR range into the appropriate SPF mechanism.
        /// </summary>
        /// <param name="ip">The IP address or CIDR range to convert.</param>
        /// <returns>An SPF mechanism string (ip4: or ip6:) with the address.</returns>
        /// <exception cref="Exception">Thrown when the IP address format is invalid.</exception>
        private string GetMechanism(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                Logger.Warning("Empty IP address provided to GetMechanism");
                return "";
            }

            try
            {
                if (ip.Contains("/"))
                {
                    return ip.Contains(":") ? $"ip6:{ip}" : $"ip4:{ip}";
                }
                
                if (IPAddress.TryParse(ip, out var address))
                {
                    return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork 
                        ? $"ip4:{ip}" 
                        : $"ip6:{ip}";
                }
                
                Logger.Warning($"Invalid IP address format: {ip}");
                return "";
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing IP address {ip}: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Deduplicates IP addresses by checking if they're contained within any CIDR ranges.
        /// </summary>
        private IEnumerable<string> DeduplicateIps(List<string> ips)
        {
            var cidrs = new List<(IPNetwork Network, string Original)>();
            var standaloneIps = new List<string>();
            var invalidEntries = new List<string>();

            // First pass: validate and separate CIDRs and standalone IPs
            foreach (var ip in ips.Where(ip => !string.IsNullOrWhiteSpace(ip)))
            {
                if (ip.Contains("/"))
                {
                    try
                    {
                        var parts = ip.Split('/');
                        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address))
                        {
                            Logger.Warning($"Invalid IP in CIDR range: {ip}");
                            invalidEntries.Add(ip);
                            continue;
                        }

                        if (!int.TryParse(parts[1], out var prefix))
                        {
                            Logger.Warning($"Invalid prefix in CIDR range: {ip}");
                            invalidEntries.Add(ip);
                            continue;
                        }

                        int maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                        if (prefix < 0 || prefix > maxPrefix)
                        {
                            Logger.Warning($"Invalid prefix length in CIDR range: {ip} (must be between 0 and {maxPrefix})");
                            invalidEntries.Add(ip);
                            continue;
                        }

                        var network = IPNetwork.Parse(ip);
                        cidrs.Add((network, ip));
                        Logger.Info($"Valid CIDR range: {ip}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Invalid CIDR format {ip}: {ex.Message}");
                        invalidEntries.Add(ip);
                    }
                }
                else
                {
                    if (IPAddress.TryParse(ip, out var address))
                    {
                        standaloneIps.Add(ip);
                        Logger.Info($"Valid IP address: {ip}");
                    }
                    else
                    {
                        Logger.Warning($"Invalid IP address format: {ip}");
                        invalidEntries.Add(ip);
                    }
                }
            }

            if (invalidEntries.Any())
            {
                Logger.Warning($"Found {invalidEntries.Count} invalid IP entries that will be ignored:");
                foreach (var entry in invalidEntries)
                {
                    Logger.Warning($"  - {entry}");
                }
            }

            // Sort CIDRs by prefix length (most specific first)
            cidrs = cidrs.OrderByDescending(x => x.Network.PrefixLength).ToList();

            // Log CIDR statistics
            if (cidrs.Any())
            {
                Logger.Info($"Processing {cidrs.Count} CIDR ranges:");
                foreach (var cidr in cidrs)
                {
                    Logger.Info($"  - {cidr.Original} (/{cidr.Network.PrefixLength})");
                }
            }

            // Check for overlapping CIDRs
            var finalCidrs = new List<string>();
            for (int i = 0; i < cidrs.Count; i++)
            {
                bool isContained = false;
                for (int j = 0; j < cidrs.Count; j++)
                {
                    if (i != j && cidrs[j].Network.Contains(cidrs[i].Network))
                    {
                        Logger.Info($"CIDR {cidrs[i].Original} is contained within {cidrs[j].Original}");
                        isContained = true;
                        break;
                    }
                }
                if (!isContained)
                {
                    finalCidrs.Add(cidrs[i].Original);
                }
            }

            // Check standalone IPs against CIDRs
            var finalIps = new List<string>();
            foreach (var ip in standaloneIps)
            {
                if (IPAddress.TryParse(ip, out var address))
                {
                    bool isContained = false;
                    foreach (var cidr in cidrs)
                    {
                        if (cidr.Network.Contains(address))
                        {
                            Logger.Info($"IP {ip} is contained within {cidr.Original}");
                            isContained = true;
                            break;
                        }
                    }
                    if (!isContained)
                    {
                        finalIps.Add(ip);
                    }
                }
            }

            var result = finalCidrs.Concat(finalIps).ToList();
            Logger.Info($"Final results:");
            Logger.Info($"  - Original entries: {ips.Count}");
            Logger.Info($"  - Invalid entries: {invalidEntries.Count}");
            Logger.Info($"  - Valid unique entries after deduplication: {result.Count}");
            return result;
        }

        /// <summary>
        /// Generates a subdomain name for split SPF records.
        /// </summary>
        /// <param name="domain">The base domain name.</param>
        /// <param name="count">The sequence number for the split.</param>
        /// <returns>A subdomain name in the format _spfN.domain.</returns>
        public static string GetSpfSubdomain(string domain, int count)
        {
            return $"_spf{count}.{domain}";
        }
    }
}
