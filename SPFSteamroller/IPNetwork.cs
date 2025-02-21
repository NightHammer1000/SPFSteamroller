using System;
using System.Net;
using System.Net.Sockets;

namespace SPFSteamroller
{
    /// <summary>
    /// Represents an IP network with CIDR notation support.
    /// </summary>
    public class IPNetwork
    {
        public IPAddress NetworkAddress { get; }
        public int PrefixLength { get; }
        private readonly byte[] _networkBytes;
        private readonly byte[] _maskBytes;

        public IPNetwork(IPAddress address, int prefixLength)
        {
            NetworkAddress = address;
            PrefixLength = prefixLength;
            _networkBytes = address.GetAddressBytes();
            _maskBytes = CreateMask(address.AddressFamily, prefixLength);
        }

        public static IPNetwork Parse(string cidr)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2)
                throw new ArgumentException("Invalid CIDR format", nameof(cidr));

            if (!IPAddress.TryParse(parts[0], out var address))
                throw new ArgumentException("Invalid IP address", nameof(cidr));

            if (!int.TryParse(parts[1], out var prefixLength))
                throw new ArgumentException("Invalid prefix length", nameof(cidr));

            int maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > maxPrefix)
                throw new ArgumentException($"Prefix length must be between 0 and {maxPrefix}", nameof(cidr));

            return new IPNetwork(address, prefixLength);
        }

        private static byte[] CreateMask(AddressFamily addressFamily, int prefixLength)
        {
            int length = addressFamily == AddressFamily.InterNetwork ? 4 : 16;
            var mask = new byte[length];
            
            for (int i = 0; i < length; i++)
            {
                if (prefixLength >= 8)
                {
                    mask[i] = 0xFF;
                    prefixLength -= 8;
                }
                else if (prefixLength > 0)
                {
                    mask[i] = (byte)(0xFF << (8 - prefixLength));
                    prefixLength = 0;
                }
                else
                {
                    mask[i] = 0x00;
                }
            }
            
            return mask;
        }

        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != NetworkAddress.AddressFamily)
                return false;

            var addressBytes = address.GetAddressBytes();
            
            for (int i = 0; i < _networkBytes.Length; i++)
            {
                if ((_networkBytes[i] & _maskBytes[i]) != (addressBytes[i] & _maskBytes[i]))
                    return false;
            }
            
            return true;
        }

        public bool Contains(IPNetwork other)
        {
            if (other.NetworkAddress.AddressFamily != NetworkAddress.AddressFamily)
                return false;

            if (PrefixLength > other.PrefixLength)
                return false;

            return Contains(other.NetworkAddress);
        }
    }
}
