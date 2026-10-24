using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MouseWithoutBorders.Class;

// Addresses are hints, never credentials: every connection still performs the full handshake.
internal static class PeerAddressCache
{
    private static readonly object Gate = new();
    internal sealed class Entry
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string KeyFingerprint { get; set; }
        public DateTimeOffset VerifiedAt { get; set; }
        public bool OutboundVerified { get; set; }
    }
    private static string PathFor(string directory) => Path.Combine(directory, "connection-cache.json");
    private static string Fingerprint(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    internal static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    private static bool Usable(IPAddress address) => !IPAddress.IsLoopback(address)
        && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.Broadcast)
        && (address.AddressFamily == AddressFamily.InterNetwork ? address.GetAddressBytes()[0] < 224
            : address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6Multicast && !address.IsIPv6LinkLocal);
    private static List<Entry> Read(string directory)
    {
        string path = PathFor(directory);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 32768) return new();
        return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new();
    }
    internal static IPAddress Get(string directory, string peer, string key, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (Gate)
        {
            try
            {
                var entry = Read(directory).FirstOrDefault(e => e != null && string.Equals(e.Name, peer, StringComparison.OrdinalIgnoreCase)
                    && e.KeyFingerprint == Fingerprint(key) && now >= e.VerifiedAt && now - e.VerifiedAt < TimeSpan.FromDays(30));
                return entry != null && IPAddress.TryParse(entry.Address, out var ip) && Usable(Normalize(ip)) ? Normalize(ip) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
    }
    internal static void Remember(string directory, string peer, string key, IPAddress address, DateTimeOffset now, bool outgoing = true)
    {
        address = Normalize(address);
        if (string.IsNullOrWhiteSpace(peer) || string.IsNullOrWhiteSpace(key) || !Usable(address)) return;
        lock (Gate)
        {
            try
            {
                List<Entry> entries;
                try { entries = Read(directory); } catch (JsonException) { entries = new(); }
                string fingerprint = Fingerprint(key);
                // An inbound IPv6 address may not be reachable in the opposite direction.
                // Never let it replace an address we successfully dialed ourselves.
                if (!outgoing && entries.Any(e => e != null && string.Equals(e.Name, peer, StringComparison.OrdinalIgnoreCase)
                    && e.OutboundVerified && e.KeyFingerprint == fingerprint && now >= e.VerifiedAt && now - e.VerifiedAt < TimeSpan.FromDays(30))) return;
                if (entries.Any(e => e != null && string.Equals(e.Name, peer, StringComparison.OrdinalIgnoreCase)
                    && e.Address == address.ToString() && e.KeyFingerprint == fingerprint && (!outgoing || e.OutboundVerified) && now >= e.VerifiedAt && now - e.VerifiedAt < TimeSpan.FromMinutes(5))) return;
                entries = entries.Where(e => e != null && !string.Equals(e.Name, peer, StringComparison.OrdinalIgnoreCase)
                    && e.KeyFingerprint == fingerprint && now >= e.VerifiedAt && now - e.VerifiedAt < TimeSpan.FromDays(30))
                    .OrderByDescending(e => e.VerifiedAt).Take(3).ToList();
                entries.Insert(0, new() { Name = peer, Address = address.ToString(), KeyFingerprint = fingerprint, VerifiedAt = now, OutboundVerified = outgoing });
                Save(directory, entries);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Core.Logger.Log("Connection address cache could not be saved: " + e.GetType().Name); }
        }
    }
    internal static void Forget(string directory, string peer, IPAddress address)
    {
        if (address == null) return;
        lock (Gate)
        {
            try
            {
                var entries = Read(directory);
                entries.RemoveAll(e => e == null || (string.Equals(e.Name, peer, StringComparison.OrdinalIgnoreCase)
                    && e.Address == Normalize(address).ToString()));
                Save(directory, entries);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
    private static void Save(string directory, List<Entry> entries)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(directory), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
