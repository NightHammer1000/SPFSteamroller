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
    class Program
    {
        private static readonly LookupClient _lookupClient = new LookupClient();
        private const int MaxSpfLength = 255;

        static async Task Main(string[] args)
        {
            var config = new Config();

            if (string.IsNullOrEmpty(config.Email) || string.IsNullOrEmpty(config.ApiKey))
            {
                Console.WriteLine("Error: Email and API Key must be set in config.ini");
                return;
            }

            try
            {
                using (var client = new CloudFlareClient(config.Email, config.ApiKey))
                {
                    var zoneId = await GetZoneId(client, config.Domain, config.ZoneId);
                    if (string.IsNullOrEmpty(zoneId)) return;

                    Console.WriteLine($"Fetching and flattening SPF record for {config.Domain}...");
                    var flattenedIps = await FlattenSpf(config.Domain);

                    if (!flattenedIps.Any())
                    {
                        Console.WriteLine($"No SPF record found or flattening failed for {config.Domain}.");
                        return;
                    }

                    DisplayRecords(flattenedIps, "Flattened SPF Record(s)");

                    List<string> reconstructedSpfRecords = ReconstructAndSplitSpf(flattenedIps, config.Domain);

                    DisplayRecords(reconstructedSpfRecords, "Reconstructed SPF Record(s)");

                    await OutputToFile(reconstructedSpfRecords, "output.txt");

                    if (config.UpdateCloudflare)
                    {
                        await UpdateCloudflareDns(client, zoneId, config.Domain, reconstructedSpfRecords);
                    }
                    else
                    {
                        Console.WriteLine("Cloudflare update is disabled in config.ini");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }

            Console.WriteLine("Press any key to exit.");
            Console.Read();
        }

        static async Task<string> GetZoneId(CloudFlareClient client, string domain, string zoneId)
        {
            if (string.IsNullOrEmpty(zoneId))
            {
                Console.WriteLine("Zone ID not provided. Attempting to retrieve it...");
                var zones = await client.Zones.GetAsync();
                if (!zones.Success)
                {
                    Console.WriteLine($"Error fetching zones: {zones.Errors?.FirstOrDefault()?.Message}");
                    return null;
                }

                var zone = zones.Result.FirstOrDefault(z => z.Name == domain);
                if (zone == null)
                {
                    Console.WriteLine($"Error: Could not find zone for domain '{domain}' in your Cloudflare account.");
                    return null;
                }
                zoneId = zone.Id;
                Console.WriteLine($"Found Zone ID: {zoneId}");
            }
            return zoneId;
        }

        static void DisplayRecords(IEnumerable<string> records, string title)
        {
            Console.WriteLine($"\n{title}:");
            foreach (var record in records)
            {
                Console.WriteLine(record);
            }
        }

        static async Task UpdateCloudflareDns(CloudFlareClient client, string zoneId, string domain, List<string> spfRecords)
        {
            Console.WriteLine("\nUpdating Cloudflare DNS records...");

            var existingTxtRecords = await client.Zones.DnsRecords.GetAsync(zoneId, new DnsRecordFilter { Type = DnsRecordType.Txt, Name = domain });
            if (!existingTxtRecords.Success) throw new Exception($"Failed to get TXT records: {existingTxtRecords.Errors?.FirstOrDefault()?.Message}");
            var existingSpfSubRecords = await client.Zones.DnsRecords.GetAsync(zoneId, new DnsRecordFilter { Type = DnsRecordType.Txt, Name = $"_spf*.{domain}" });
            if (!existingSpfSubRecords.Success) throw new Exception($"Failed to get SPF sub-records: {existingSpfSubRecords.Errors?.FirstOrDefault()?.Message}");

            await DeleteExistingSpfRecords(client, zoneId, existingTxtRecords.Result.Concat(existingSpfSubRecords.Result), domain);

            await CreateNewSpfRecords(client, zoneId, domain, spfRecords);

            Console.WriteLine("Cloudflare DNS update complete.");
        }

        static async Task DeleteExistingSpfRecords(CloudFlareClient client, string zoneId, IEnumerable<DnsRecord> records, string domain)
        {
            Regex spfRegex = new Regex(@"^v=spf1\s", RegexOptions.IgnoreCase);
            Regex spfSubdomainRegex = new Regex(@"^_spf\d+\." + Regex.Escape(domain) + "$");

            foreach (var record in records)
            {
                if (spfRegex.IsMatch(record.Content) || spfSubdomainRegex.IsMatch(record.Name))
                {
                    Console.WriteLine($"Deleting existing record: {record.Name} ({record.Content})");
                    var deleteResult = await client.Zones.DnsRecords.DeleteAsync(zoneId, record.Id);
                    if (!deleteResult.Success)
                        throw new Exception($"Failed to delete record: {deleteResult.Errors?.FirstOrDefault()?.Message}");
                }
            }
        }

        static async Task CreateNewSpfRecords(CloudFlareClient client, string zoneId, string domain, List<string> spfRecords)
        {
            for (int i = 0; i < spfRecords.Count; i++)
            {
                string recordName = (i == 0) ? domain : GetSpfSubdomain(domain, i);
                string content = "\"" + spfRecords[i] + "\"";

                Console.WriteLine($"Creating or updating record: {recordName} ({content})");

                var existingRecord = await client.Zones.DnsRecords.GetAsync(zoneId, new DnsRecordFilter { Type = DnsRecordType.Txt, Name = recordName });
                if (existingRecord.Success && existingRecord.Result.Any())
                {
                    // Update existing record
                    var recordId = existingRecord.Result.First().Id;
                    var updateRecord = new ModifiedDnsRecord
                    {
                        Type = DnsRecordType.Txt,
                        Name = recordName,
                        Content = content,
                        Ttl = 1,
                        Proxied = false
                    };
                    var updateResult = await client.Zones.DnsRecords.UpdateAsync(zoneId, recordId, updateRecord);

                    if (!updateResult.Success)
                        throw new Exception($"Failed to update DNS record: {updateResult.Errors?.FirstOrDefault()?.Message}");
                }
                else
                {
                    // Create new record
                    var newRecord = new NewDnsRecord
                    {
                        Type = DnsRecordType.Txt,
                        Name = recordName,
                        Content = content,
                        Ttl = 1,
                        Proxied = false
                    };
                    var createResult = await client.Zones.DnsRecords.AddAsync(zoneId, newRecord);

                    if (!createResult.Success)
                        throw new Exception($"Failed to create DNS record: {createResult.Errors?.FirstOrDefault()?.Message}");
                }
            }
        }

        static string GetSubDomainFromInclude(string spfRecord, string originalDomain)
        {
            Match includeMatch = Regex.Match(spfRecord, @"include:([^\s]+)");
            return includeMatch.Success ? includeMatch.Groups[1].Value : "";
        }

        static async Task<List<string>> GetAllSpfRecordsForDomain(string domain)
        {
            // Query DNS for TXT records
            var result = await _lookupClient.QueryAsync(domain, QueryType.TXT);
            if (result.HasError)
            {
                Console.WriteLine($"DNS lookup error for {domain}: {result.ErrorMessage}");
                return new List<string>();
            }

            // Merge chunked TXT parts and filter only v=spf1
            var spfRecords = result.Answers
                .OfType<TxtRecord>()
                .Select(txtRecord => string.Concat(txtRecord.Text)) // Merge without extra spaces
                .Where(full => full.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
                .Select(full => Regex.Replace(full, @"\s+", " ").Trim()) // Normalize whitespace
                .Distinct()
                .ToList();

            // Debug logging
            foreach (var record in spfRecords)
            {
                Console.WriteLine($"[GetAllSpfRecordsForDomain] Merged SPF record: {record}");
            }

            return spfRecords;
        }

        static async Task<List<string>> FlattenSpf(string domain, int depth = 0, HashSet<string> visitedDomains = null)
        {
            // Limit recursion to avoid loops
            if (depth > 10)
            {
                Console.WriteLine("Recursion limit exceeded - potential SPF loop.");
                return new List<string>();
            }
            visitedDomains ??= new HashSet<string>();

            var spfRecords = await GetAllSpfRecordsForDomain(domain);
            var allFlattenedIps = new List<string>();

            foreach (var spfRecord in spfRecords)
            {
                // Extract flattened IPs from each SPF record
                var flattenedIps = await ProcessSpfRecord(domain, spfRecord, depth, visitedDomains);
                allFlattenedIps.AddRange(flattenedIps);
            }

            return allFlattenedIps
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct()
                .ToList();
        }

        static async Task<List<string>> ProcessSpfRecord(string originalDomain, string spfRecord, int depth, HashSet<string> visitedDomains)
        {
            var flattenedIps = new List<string>();

            try
            {
                // Split on spaces
                var terms = spfRecord.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                foreach (var term in terms)
                {
                    // Skip mechanisms with only -all, etc.
                    if (term is "-all" or "~all" or "?all" or "+all") continue;

                    var parts = term.Split(':', 2);
                    var mechanism = parts[0].ToLowerInvariant();
                    var value = (parts.Length > 1) ? parts[1]?.Trim() : null;

                    // Mechanisms that don't need explicit value default to original domain
                    if (mechanism is "a" or "mx")
                    {
                        value ??= originalDomain;
                    }

                    // Skip invalid terms that can't be parsed
                    if (string.IsNullOrWhiteSpace(value) && mechanism != "v=spf1")
                    {
                        Console.WriteLine($"[ProcessSpfRecord] Invalid term encountered: {term}");
                        continue;
                    }

                    switch (mechanism)
                    {
                        case "v=spf1":
                            break;
                        case "include":
                            if (!string.IsNullOrEmpty(value) && !visitedDomains.Contains(value))
                            {
                                visitedDomains.Add(value);
                                var includedIps = await FlattenSpf(value, depth + 1, visitedDomains);
                                flattenedIps.AddRange(includedIps);
                                visitedDomains.Remove(value);
                            }
                            break;
                        case "a":
                            await AddARecords(flattenedIps, value);
                            break;
                        case "mx":
                            await AddMxRecords(flattenedIps, value);
                            break;
                        case "ip4":
                        case "ip6":
                            if (!string.IsNullOrWhiteSpace(value)) flattenedIps.Add(value);
                            break;
                        case "ptr":
                            Console.WriteLine("[ProcessSpfRecord] 'ptr' mechanism is deprecated.");
                            break;
                        case "exists":
                            Console.WriteLine("[ProcessSpfRecord] 'exists' mechanism is deprecated.");
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProcessSpfRecord] Error: {ex.Message}");
            }

            return flattenedIps;
        }

        static async Task AddARecords(List<string> flattenedIps, string domain)
        {
            var aResult = await _lookupClient.QueryAsync(domain, QueryType.A);
            if (!aResult.HasError)
            {
                flattenedIps.AddRange(aResult.Answers.OfType<ARecord>().Select(a => a.Address.ToString()));
            }

            var aaaaResult = await _lookupClient.QueryAsync(domain, QueryType.AAAA);
            if (!aaaaResult.HasError)
            {
                flattenedIps.AddRange(aaaaResult.Answers.OfType<AaaaRecord>().Select(aaaa => aaaa.Address.ToString()));
            }
        }

        static async Task AddMxRecords(List<string> flattenedIps, string domain)
        {
            var mxResult = await _lookupClient.QueryAsync(domain, QueryType.MX);
            if (!mxResult.HasError)
            {
                foreach (var mxRecord in mxResult.Answers.OfType<MxRecord>())
                {
                    var mxHost = mxRecord.Exchange.Value.TrimEnd('.');
                    await AddARecords(flattenedIps, mxHost);
                }
            }
        }

        static List<string> ReconstructAndSplitSpf(List<string> flattenedIps, string originalDomain)
        {
            // Ensure these items are distinct and non-empty
            var distinctIps = flattenedIps
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct()
                .ToList();

            var records = new List<string>();
            var currentRecord = new StringBuilder("v=spf1");
            int splitCount = 0;
            const int maxSpfLength = 2000; // Updated limit for Cloudflare

            foreach (var ip in distinctIps)
            {
                string mechanism = GetMechanism(ip);

                if (currentRecord.Length + mechanism.Length + 1 > maxSpfLength)
                {
                    currentRecord.Append(" -all");
                    records.Add(currentRecord.ToString());
                    currentRecord.Clear().Append("v=spf1");
                    splitCount++;
                }

                currentRecord.Append(" " + mechanism);
            }

            // Add the final batch of IPs
            if (currentRecord.Length > "v=spf1".Length) // Check if we added anything beyond "v=spf1"
            {
                currentRecord.Append(" -all"); // and finish the record
                records.Add(currentRecord.ToString());
                splitCount++;
            }

            return CreateFinalRecords(records, originalDomain, splitCount);
        }

        static List<string> CreateFinalRecords(List<string> records, string originalDomain, int splitCount)
        {
            var finalRecords = new List<string>();

            if (splitCount > 1)
            {
                // Create chained SPF records
                for (int i = 0; i < splitCount; i++)
                {
                    string record = records[i];
                    if (i < splitCount - 1)
                    {
                        // Add include for the next record, except for the last one
                        record = record.Replace(" -all", $" include:{GetSpfSubdomain(originalDomain, i + 2)} -all");
                    }
                    finalRecords.Add(record);
                }

                // Ensure the root SPF record only contains an include to the first split
                finalRecords.Insert(0, $"v=spf1 include:{GetSpfSubdomain(originalDomain, 1)} -all");
            }
            else
            {
                // Single record case
                finalRecords.Add(records[0]); // Already has v=spf1 and -all
            }

            // Ensure the include chain stays under 10 includes
            if (finalRecords.Count > 10)
            {
                throw new Exception("SPF record chain exceeds 10 includes, which is not allowed.");
            }

            return finalRecords;
        }

        private static string GetSpfSubdomain(string domain, int count)
        {
            return $"_spf{count}.{domain}";
        }

        static string GetMechanism(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return "";

            try
            {
                if (ip.Contains("/"))
                {
                    // Handle CIDR notation
                    return ip.Contains(":") ? $"ip6:{ip}" : $"ip4:{ip}";
                }
                
                if (IPAddress.TryParse(ip, out var address))
                {
                    return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork 
                        ? $"ip4:{ip}" 
                        : $"ip6:{ip}";
                }
                
                Console.WriteLine($"Warning: Invalid IP address format: {ip}");
                return "";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing IP address '{ip}': {ex.Message}");
                return "";
            }
        }

        static async Task OutputToFile(List<string> records, string filePath)
        {
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                foreach (var record in records)
                {
                    await writer.WriteLineAsync(record);
                }
            }
            Console.WriteLine($"Output written to {filePath}");
        }
    }
}