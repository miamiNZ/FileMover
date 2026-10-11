using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FileMover;

public enum HostKeyStatus { Trusted, New, Mismatch }

/// <summary>
/// Trust-on-first-use store for SSH host keys. The first key seen for a host:port and key
/// type is recorded; any later connection presenting a different key of the same type is
/// rejected (possible man-in-the-middle).
/// File format, one entry per line: <c>host:port key-type SHA256:fingerprint</c>.
/// </summary>
public sealed class KnownHostsStore
{
    private readonly object _lock = new();

    public static KnownHostsStore Default { get; set; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileMover", "known_hosts"));

    public string FilePath { get; }

    public KnownHostsStore(string filePath) => FilePath = filePath;

    /// <summary>Checks a presented host key and records it when the host/key type is unknown.</summary>
    public HostKeyStatus CheckAndRecord(string host, int port, string keyType, string fingerprintSha256)
    {
        if (string.IsNullOrWhiteSpace(host))        throw new ArgumentException("Host is required.", nameof(host));
        if (string.IsNullOrWhiteSpace(keyType))     throw new ArgumentException("Key type is required.", nameof(keyType));
        if (string.IsNullOrWhiteSpace(fingerprintSha256)) throw new ArgumentException("Fingerprint is required.", nameof(fingerprintSha256));

        var hostKey     = FormatHost(host, port);
        var fingerprint = NormaliseFingerprint(fingerprintSha256);

        lock (_lock)
        {
            var entries = Load();
            var match   = entries.FirstOrDefault(e =>
                string.Equals(e.Host, hostKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.KeyType, keyType, StringComparison.Ordinal));

            if (match is not null)
                return string.Equals(match.Fingerprint, fingerprint, StringComparison.Ordinal)
                    ? HostKeyStatus.Trusted
                    : HostKeyStatus.Mismatch;

            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(FilePath, $"{hostKey} {keyType} {fingerprint}{Environment.NewLine}");
            return HostKeyStatus.New;
        }
    }

    /// <summary>Removes every recorded key for a host:port (e.g. after a legitimate server re-key).</summary>
    public int Forget(string host, int port)
    {
        var hostKey = FormatHost(host, port);
        lock (_lock)
        {
            var entries = Load();
            var kept    = entries.Where(e => !string.Equals(e.Host, hostKey, StringComparison.OrdinalIgnoreCase)).ToList();
            var removed = entries.Count - kept.Count;
            if (removed > 0)
                File.WriteAllLines(FilePath, kept.Select(e => $"{e.Host} {e.KeyType} {e.Fingerprint}"));
            return removed;
        }
    }

    internal static string FormatHost(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    private static string NormaliseFingerprint(string fp)
    {
        fp = fp.Trim();
        return fp.StartsWith("SHA256:", StringComparison.Ordinal) ? fp : "SHA256:" + fp;
    }

    private List<Entry> Load()
    {
        var list = new List<Entry>();
        if (!File.Exists(FilePath)) return list;

        foreach (var line in File.ReadAllLines(FilePath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3) list.Add(new Entry(parts[0], parts[1], parts[2]));
        }
        return list;
    }

    private sealed record Entry(string Host, string KeyType, string Fingerprint);
}
