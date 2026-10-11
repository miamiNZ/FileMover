using System;
using System.Runtime.CompilerServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FileMover;

/// <summary>Raised when a server presents a host key that differs from the recorded one.</summary>
public sealed class HostKeyMismatchException(string host, int port, string knownHostsPath)
    : Exception($"Host key for {host}:{port} does not match the recorded key — possible man-in-the-middle attack. " +
                $"If the server was legitimately re-keyed, remove its entry from {knownHostsPath} and reconnect.")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
}

/// <summary>
/// Single place that builds SSH/SFTP clients so every connection gets the same timeout
/// and host-key verification.
/// </summary>
public static class SshConnectionFactory
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConditionalWeakTable<BaseClient, HostKeyGuard> _guards = new();

    public static SftpClient CreateSftp(EndpointSettings s, TimeSpan? timeout = null) =>
        Attach(new SftpClient(BuildConnectionInfo(s, timeout)), s);

    public static SshClient CreateSsh(EndpointSettings s, TimeSpan? timeout = null) =>
        Attach(new SshClient(BuildConnectionInfo(s, timeout)), s);

    /// <summary>Connects and converts a rejected host key into a <see cref="HostKeyMismatchException"/>.</summary>
    public static void ConnectVerified(this BaseClient client)
    {
        try
        {
            client.Connect();
        }
        catch (Exception ex) when (ex is SshConnectionException or SshException
                                   && _guards.TryGetValue(client, out var guard) && guard.Mismatch)
        {
            throw new HostKeyMismatchException(guard.Host, guard.Port, guard.Store.FilePath);
        }
    }

    internal static Renci.SshNet.ConnectionInfo BuildConnectionInfo(EndpointSettings s, TimeSpan? timeout)
    {
        if (string.IsNullOrWhiteSpace(s.Host))     throw new ArgumentException("SSH host is required.");
        if (string.IsNullOrWhiteSpace(s.Username)) throw new ArgumentException("SSH username is required.");

        AuthenticationMethod auth = s.UseKeyFile && !string.IsNullOrWhiteSpace(s.KeyFilePath)
            ? new PrivateKeyAuthenticationMethod(s.Username, new PrivateKeyFile(s.KeyFilePath))
            : new PasswordAuthenticationMethod(s.Username, s.Password);

        return new Renci.SshNet.ConnectionInfo(s.Host, s.Port, s.Username, auth)
        {
            Timeout = timeout ?? DefaultTimeout,
        };
    }

    private static T Attach<T>(T client, EndpointSettings s) where T : BaseClient
    {
        var guard = new HostKeyGuard(s.Host, s.Port, KnownHostsStore.Default);
        _guards.AddOrUpdate(client, guard);
        client.HostKeyReceived += guard.OnHostKeyReceived;
        return client;
    }

    private sealed class HostKeyGuard(string host, int port, KnownHostsStore store)
    {
        public string Host { get; } = host;
        public int Port { get; } = port;
        public KnownHostsStore Store { get; } = store;
        public bool Mismatch { get; private set; }

        public void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
        {
            var status = Store.CheckAndRecord(Host, Port, e.HostKeyName, e.FingerPrintSHA256);
            Mismatch   = status == HostKeyStatus.Mismatch;
            e.CanTrust = !Mismatch;
        }
    }
}
