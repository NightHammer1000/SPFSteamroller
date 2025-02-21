
# SPFSteamroller

SPF Flattening Tool written in C#

## Usage
This Tool can Simply Flatten an SPF Record with more than 10 DNS Resolves, split it in multiple Records if the SPF Lenght exceeds the Limit of a TXT record and output the Finished and Split SPF to `output.txt`

OR

Flatten an SPF Record with more than 10 DNS Resolves, split it in multiple Records if the SPF Lenght exceeds the Limit of a TXT record and output the Finished and Split SPF to `output.txt` and automatically push the Records to Cloudflare.

This tool needs a so calles "Master SPF Record" in a TXT  Record at the Subdomain `_masterspf.`
This should contain the Dirty, Unclean, way to long SPF you want to Flatten. Here is also where you want to make future changes to your SPF.
After you introduced Changes to the Master SPF Record, simply rerun the tool to flatten it again.

This tool needs a Config File. A Example Config File will be created if you start the Tool for the first Time. Its pretty self-explanitory.
If you want to use it without Cloudflare, set your domain and `updateCloudflare=false`. This will simply output to `output.txt`
If you want to use this tool to automatically create/update Records on Cloudflare, set your Domain, The Cloudflare API Data and the Zone ID and `updateCloudflare=true`

## How it works
It resolves the whole SPF and turns every A, AAAA, MX, IP4, IP6 and Include and parses them down to their Base IPs and Subnets definde behind those Records.
After that a new SPF, only including the IPs and Subnets will be constructed.
If that new Record is to Long for a TXT Record, it gets split up into Subdomains like `_spf1, _spf2` and so on.
In that case a root SPF Record for the Domain will be created that only has an Include to `_spf1`.
`_spf1` then includes `_spf2`. And so on. Until the end is reached.
The Tool then, if it is enabled, uses the Cloudflare API to Automaticaly Publish the Records.
