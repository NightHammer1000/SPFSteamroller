using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;
using System.Text.RegularExpressions;

namespace SPFSteamroller
{
    /// <summary>
    /// Resolves and flattens SPF records by processing their mechanisms and includes.
    /// </summary>
    public class SpfResolver
    {
        private readonly LookupClient _lookupClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpfResolver"/> class.
        /// </summary>
        public SpfResolver()
        {
            _lookupClient = new LookupClient();
        }

        /// <summary>
        /// Flattens an SPF record into a list of IP addresses and CIDR ranges.
        /// </summary>
        /// <param name="domain">The domain whose SPF record should be flattened.</param>
        /// <returns>A list of IP addresses and CIDR ranges from the SPF record.</returns>
        public async Task<List<string>> FlattenSpf(string domain)
        {
            return await FlattenSpf(domain, 0, new HashSet<string>());
        }

        /// <summary>
        /// Recursively processes an SPF record and its includes.
        /// </summary>
        /// <param name="domain">The domain to process.</param>
        /// <param name="depth">The current recursion depth.</param>
        /// <param name="visitedDomains">Set of domains already processed to detect circular references.</param>
        /// <returns>A list of IP addresses and CIDR ranges.</returns>
        /// <remarks>
        /// This method handles circular references and maintains a list of visited domains to prevent infinite loops.
        /// </remarks>
        private async Task<List<string>> FlattenSpf(string domain, int depth, HashSet<string> visitedDomains)
        {
            Logger.Info($"{"  ".Repeat(depth)}Processing SPF for domain: {domain} (depth: {depth})");

            if (visitedDomains.Contains(domain))
            {
                Logger.Warning($"{"  ".Repeat(depth)}Circular reference detected for domain: {domain}");
                // Process the domain once even if it's a circular reference
            }
            else
            {
                visitedDomains.Add(domain);
            }

            var spfRecords = await GetAllSpfRecordsForDomain(domain);
            var allFlattenedIps = new List<string>();

            foreach (var spfRecord in spfRecords)
            {
                var flattenedIps = await ProcessSpfRecord(domain, spfRecord, depth, visitedDomains);
                allFlattenedIps.AddRange(flattenedIps);
            }

            var distinctIps = allFlattenedIps
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Distinct()
                .ToList();

            Logger.Info($"{"  ".Repeat(depth)}Found {distinctIps.Count} unique IPs for {domain}");
            return distinctIps;
        }

        /// <summary>
        /// Retrieves all SPF records for a given domain.
        /// </summary>
        /// <param name="domain">The domain to query.</param>
        /// <returns>A list of SPF record strings.</returns>
        /// <remarks>
        /// Only returns records that start with "v=spf1". Multiple records may exist but are uncommon.
        /// </remarks>
        private async Task<List<string>> GetAllSpfRecordsForDomain(string domain)
        {
            try
            {
                Logger.Info($"Querying TXT records for domain: {domain}");
                var result = await _lookupClient.QueryAsync(domain, QueryType.TXT);
                
                if (result == null)
                {
                    Logger.Error($"DNS query returned null for domain {domain}");
                    return new List<string>();
                }

                if (result.HasError)
                {
                    Logger.Error($"DNS query failed for domain {domain}: {result.ErrorMessage}");
                    return new List<string>();
                }

                var spfRecords = result.Answers
                    .OfType<TxtRecord>()
                    .Select(txtRecord => string.Concat(txtRecord.Text))
                    .Where(full => full.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
                    .Select(full => Regex.Replace(full, @"\s+", " ").Trim())
                    .Distinct()
                    .ToList();

                if (spfRecords.Any())
                {
                    Logger.Info($"Found {spfRecords.Count} SPF record(s) for {domain}:");
                    foreach (var record in spfRecords)
                    {
                        Logger.Info($"  {record}");
                    }
                }
                else
                {
                    Logger.Warning($"No SPF records found for {domain}");
                }

                return spfRecords;
            }
            catch (DnsResponseException ex)
            {
                Logger.Error($"DNS response error for {domain}: {ex.Message}");
                return new List<string>();
            }
            catch (Exception ex)
            {
                Logger.Error($"Unexpected error querying DNS for {domain}: {ex.Message}");
                return new List<string>();
            }
        }

        /// <summary>
        /// Processes a single SPF record and extracts all IP addresses.
        /// </summary>
        /// <param name="originalDomain">The original domain being processed.</param>
        /// <param name="spfRecord">The SPF record to process.</param>
        /// <param name="depth">Current recursion depth.</param>
        /// <param name="visitedDomains">Set of already visited domains.</param>
        /// <returns>A list of IP addresses and CIDR ranges from the record.</returns>
        private async Task<List<string>> ProcessSpfRecord(string originalDomain, string spfRecord, int depth, HashSet<string> visitedDomains)
        {
            Logger.Info($"{"  ".Repeat(depth)}Processing SPF record: {spfRecord}");
            var flattenedIps = new List<string>();

            try
            {
                var terms = spfRecord.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var term in terms)
                {
                    if (term is "-all" or "~all" or "?all" or "+all")
                    {
                        Logger.Info($"{"  ".Repeat(depth)}Found ALL qualifier: {term}");
                        continue;
                    }

                    await ProcessSpfTerm(term, originalDomain, depth, visitedDomains, flattenedIps);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{"  ".Repeat(depth)}Error processing SPF record: {ex.Message}");
                throw;
            }

            return flattenedIps;
        }

        /// <summary>
        /// Processes a single SPF mechanism term.
        /// </summary>
        /// <param name="term">The SPF term to process.</param>
        /// <param name="originalDomain">The original domain being processed.</param>
        /// <param name="depth">Current recursion depth.</param>
        /// <param name="visitedDomains">Set of already visited domains.</param>
        /// <param name="flattenedIps">List to store found IP addresses.</param>
        /// <remarks>
        /// Handles various SPF mechanisms including: include, a, mx, ip4, ip6.
        /// PTR and EXISTS mechanisms are logged but ignored as per SPF RFC.
        /// </remarks>
        private async Task ProcessSpfTerm(string term, string originalDomain, int depth, HashSet<string> visitedDomains, List<string> flattenedIps)
        {
            Logger.Info($"{"  ".Repeat(depth)}Processing raw term: {term}");
            
            // Handle singular mechanisms that implicitly reference their own domain
            if (term == "a" || term == "+a" || term == "mx" || term == "+mx")
            {
                Logger.Info($"{"  ".Repeat(depth)}Processing implicit {term} mechanism for domain {originalDomain}");
                if (term.Contains("a"))
                {
                    await ProcessARecords(originalDomain, flattenedIps);
                }
                else
                {
                    await ProcessMxRecords(originalDomain, flattenedIps);
                }
                return;
            }
            
            var parts = term.Split(':', 2);
            var mechanism = parts[0].ToLowerInvariant();
            var value = (parts.Length > 1) ? parts[1]?.Trim() : null;
        
            Logger.Info($"{"  ".Repeat(depth)}Split into mechanism: {mechanism}, value: {value}");

            if (mechanism is "a" or "mx")
            {
                value ??= originalDomain;
            }

            if (string.IsNullOrWhiteSpace(value) && mechanism != "v=spf1")
            {
                return;
            }

            Logger.Info($"{"  ".Repeat(depth)}Processing mechanism: {mechanism}{(value != null ? $":{value}" : "")}");

            switch (mechanism)
            {
                case "v=spf1":
                    break;
                case "include":
                    await ProcessInclude(value, depth, visitedDomains, flattenedIps);
                    break;
                case "a":
                    await ProcessARecords(value, flattenedIps);
                    break;
                case "mx":
                    await ProcessMxRecords(value, flattenedIps);
                    break;
                case "ip4":
                case "ip6":
                    ProcessIpAddress(mechanism, value, flattenedIps);
                    break;
                case "ptr":
                    Logger.Warning($"{"  ".Repeat(depth)}PTR mechanism is deprecated and ignored: {value}");
                    break;
                case "exists":
                    Logger.Warning($"{"  ".Repeat(depth)}EXISTS mechanism is not supported: {value}");
                    break;
                default:
                    Logger.Warning($"{"  ".Repeat(depth)}Unknown mechanism: {mechanism}");
                    break;
            }
        }

        /// <summary>
        /// Processes an SPF include mechanism.
        /// </summary>
        /// <param name="domain">The domain to include.</param>
        /// <param name="depth">Current recursion depth.</param>
        /// <param name="visitedDomains">Set of already visited domains.</param>
        /// <param name="flattenedIps">List to store found IP addresses.</param>
        private async Task ProcessInclude(string domain, int depth, HashSet<string> visitedDomains, List<string> flattenedIps)
        {
            if (!string.IsNullOrEmpty(domain) && !visitedDomains.Contains(domain))
            {
                visitedDomains.Add(domain);
                var includedIps = await FlattenSpf(domain, depth + 1, visitedDomains);
                flattenedIps.AddRange(includedIps);
            }
        }

        /// <summary>
        /// Processes an IP address mechanism (ip4 or ip6).
        /// </summary>
        /// <param name="mechanism">The mechanism type (ip4 or ip6).</param>
        /// <param name="value">The IP address or CIDR range.</param>
        /// <param name="flattenedIps">List to store found IP addresses.</param>
        private void ProcessIpAddress(string mechanism, string value, List<string> flattenedIps)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var parts = value.Split('/');
                if (IPAddress.TryParse(parts[0], out _))
                {
                    // If it has a CIDR part, validate it
                    if (parts.Length > 1)
                    {
                        if (int.TryParse(parts[1], out int cidr))
                        {
                            // Validate CIDR range (0-32 for IPv4, 0-128 for IPv6)
                            int maxCidr = mechanism == "ip4" ? 32 : 128;
                            if (cidr >= 0 && cidr <= maxCidr)
                            {
                                Logger.Info($"Adding {mechanism} address with CIDR: {value}");
                                flattenedIps.Add(value);
                                return;
                            }
                        }
                        Logger.Warning($"Invalid CIDR notation in: {value}");
                        return;
                    }
                    
                    Logger.Info($"Adding {mechanism} address: {value}");
                    flattenedIps.Add(value);
                }
                else
                {
                    Logger.Warning($"Invalid IP address format: {value}");
                }
            }
        }

        /// <summary>
        /// Resolves A and AAAA records for a domain.
        /// </summary>
        /// <param name="domain">The domain to resolve.</param>
        /// <param name="flattenedIps">List to store found IP addresses.</param>
        private async Task ProcessARecords(string domain, List<string> flattenedIps)
        {
            try
            {
                var aResult = await _lookupClient.QueryAsync(domain, QueryType.A);
                if (aResult.HasError)
                {
                    Logger.Warning($"Failed to query A records for {domain}: {aResult.ErrorMessage}");
                }
                else
                {
                    var ipv4s = aResult.Answers.OfType<ARecord>().Select(a => a.Address.ToString()).ToList();
                    Logger.Info($"Found {ipv4s.Count} IPv4 addresses for {domain}");
                    flattenedIps.AddRange(ipv4s);
                }

                var aaaaResult = await _lookupClient.QueryAsync(domain, QueryType.AAAA);
                if (aaaaResult.HasError)
                {
                    Logger.Warning($"Failed to query AAAA records for {domain}: {aaaaResult.ErrorMessage}");
                }
                else
                {
                    var ipv6s = aaaaResult.Answers.OfType<AaaaRecord>().Select(aaaa => aaaa.Address.ToString()).ToList();
                    Logger.Info($"Found {ipv6s.Count} IPv6 addresses for {domain}");
                    flattenedIps.AddRange(ipv6s);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error resolving A/AAAA records for {domain}: {ex.Message}");
            }
        }

        /// <summary>
        /// Resolves MX records for a domain and their corresponding A/AAAA records.
        /// </summary>
        /// <param name="domain">The domain to resolve MX records for.</param>
        /// <param name="flattenedIps">List to store found IP addresses.</param>
        private async Task ProcessMxRecords(string domain, List<string> flattenedIps)
        {
            try 
            {
                var mxResult = await _lookupClient.QueryAsync(domain, QueryType.MX);
                if (!mxResult.HasError)
                {
                    var mxRecords = mxResult.Answers.OfType<MxRecord>().ToList();
                    Logger.Info($"Found {mxRecords.Count} MX records for {domain}");
                    
                    foreach (var mxRecord in mxRecords)
                    {
                        var mxHost = mxRecord.Exchange.Value.TrimEnd('.');
                        if (!string.IsNullOrWhiteSpace(mxHost))
                        {
                            Logger.Info($"Resolving MX host: {mxHost}");
                            
                            // First try to resolve the MX host directly
                            await ProcessARecords(mxHost, flattenedIps);

                            // Then check if the MX host has its own SPF record
                            var spfRecords = await GetAllSpfRecordsForDomain(mxHost);
                            if (spfRecords.Any())
                            {
                                Logger.Info($"Found SPF records for MX host {mxHost}, processing them...");
                                foreach (var spfRecord in spfRecords)
                                {
                                    var mxSpfIps = await ProcessSpfRecord(mxHost, spfRecord, 0, new HashSet<string> { domain });
                                    flattenedIps.AddRange(mxSpfIps);
                                }
                            }
                        }
                    }
                }
                else
                {
                    Logger.Warning($"Failed to query MX records for {domain}: {mxResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error processing MX records for {domain}: {ex.Message}");
            }
        }
    }
}
