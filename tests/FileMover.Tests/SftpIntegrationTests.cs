using Renci.SshNet;

namespace FileMover.Tests;

/// <summary>
/// Runs the engine against a real SSH/SFTP server. Skipped unless FILEMOVER_SFTP_HOST is set.
/// <c>make test-sftp</c> starts a throwaway OpenSSH container and sets the variables:
/// FILEMOVER_SFTP_HOST, FILEMOVER_SFTP_PORT, FILEMOVER_SFTP_USER, FILEMOVER_SFTP_PASS, FILEMOVER_SFTP_ROOT.
/// </summary>
public class SftpIntegrationTests
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("FILEMOVER_SFTP_HOST");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EndpointSettings Remote(string path) => new()
    {
        Type     = EndpointType.SSH,
        Host     = Host!,
        Port     = int.TryParse(Environment.GetEnvironmentVariable("FILEMOVER_SFTP_PORT"), out var p) ? p : 22,
        Username = Environment.GetEnvironmentVariable("FILEMOVER_SFTP_USER") ?? "",
        Password = Environment.GetEnvironmentVariable("FILEMOVER_SFTP_PASS") ?? "",
        Path     = path,
    };

    private static string NewRemoteDir() =>
        $"{(Environment.GetEnvironmentVariable("FILEMOVER_SFTP_ROOT") ?? "/tmp").TrimEnd('/')}/fm-{Guid.NewGuid():N}";

    private static void RequireServer() =>
        Assert.SkipWhen(string.IsNullOrEmpty(Host), "FILEMOVER_SFTP_HOST not set — SFTP integration tests skipped.");

    private static SftpClient Connect(EndpointSettings s)
    {
        var c = SshConnectionFactory.CreateSftp(s);
        c.ConnectVerified();
        return c;
    }

    [Fact]
    public async Task TestConnection_Succeeds_AndRecordsHostKey()
    {
        RequireServer();
        var s = Remote("/");
        var result = await new CopyHub(null!).TestSshConnection(new EndpointDto
        {
            Type = "SSH", Host = s.Host, Port = s.Port, Username = s.Username, Password = s.Password,
        });

        Assert.True(result.Success, result.Error);
        Assert.Contains(KnownHostsStore.FormatHost(s.Host, s.Port), File.ReadAllText(KnownHostsStore.Default.FilePath));
    }

    [Fact]
    public async Task LocalToSsh_ThenSshToLocal_RoundTripsWithVerifyAndChmod()
    {
        RequireServer();
        using var src  = new TempDir();
        using var back = new TempDir();
        src.WriteFile("a.txt", "alpha");
        src.WriteFile("sub/b.bin", RandomBytes(5 * 1024 * 1024 + 7));
        var remoteDir = NewRemoteDir();

        var upload = new CopyJob
        {
            Source         = new EndpointSettings { Type = EndpointType.Local, Path = src.Path },
            Destination    = Remote(remoteDir),
            Mode           = CopyMode.Folder,
            MaxThreads     = 2,
            VerifyFiles    = true,
            SetPermissions = true,
            PermissionMode = "640",
        };
        var logs = new List<string>();
        var engine = new CopyEngine();
        engine.LogMessage += (_, e) => { lock (logs) logs.Add(e.Message); };
        Assert.True(await engine.ExecuteAsync(upload, Ct), string.Join("\n", logs));

        using (var sftp = Connect(Remote("/")))
        {
            var attrs = sftp.GetAttributes($"{remoteDir}/a.txt");
            var mode = (attrs.OwnerCanRead, attrs.OwnerCanWrite, attrs.OwnerCanExecute,
                        attrs.GroupCanRead, attrs.GroupCanWrite, attrs.GroupCanExecute,
                        attrs.OthersCanRead, attrs.OthersCanWrite, attrs.OthersCanExecute);
            Assert.Equal((true, true, false, true, false, false, false, false, false), mode); // 640

            var dir = sftp.GetAttributes($"{remoteDir}/sub");
            Assert.Equal((true, true, true, true, false, true, true, false, true), // 755
                (dir.OwnerCanRead, dir.OwnerCanWrite, dir.OwnerCanExecute,
                 dir.GroupCanRead, dir.GroupCanWrite, dir.GroupCanExecute,
                 dir.OthersCanRead, dir.OthersCanWrite, dir.OthersCanExecute));
            Assert.True(sftp.Exists($"{remoteDir}/sub/b.bin"));
        }

        var download = new CopyJob
        {
            Source      = Remote(remoteDir),
            Destination = new EndpointSettings { Type = EndpointType.Local, Path = back.Path },
            Mode        = CopyMode.Folder,
            MaxThreads  = 2,
            VerifyFiles = true,
            OperationMode = OperationMode.Move,
        };
        logs.Clear();
        Assert.True(await engine.ExecuteAsync(download, Ct), string.Join("\n", logs));

        Assert.Equal("alpha", File.ReadAllText(back.Combine("a.txt")));
        Assert.Equal(File.ReadAllBytes(src.Combine("sub", "b.bin")), File.ReadAllBytes(back.Combine("sub", "b.bin")));

        using (var sftp = Connect(Remote("/")))
            Assert.False(sftp.Exists($"{remoteDir}/a.txt")); // moved
    }

    [Fact]
    public async Task Browse_And_FreeSpace_Work()
    {
        RequireServer();
        var s = Remote("/");
        var dto = new EndpointDto { Type = "SSH", Host = s.Host, Port = s.Port, Username = s.Username, Password = s.Password };

        var browse = await new CopyHub(null!).BrowseSsh(dto, "/");
        Assert.Null(browse.Error);
        Assert.NotEmpty(browse.Entries);

        Assert.True(await DiskSpaceHelper.GetSshFreeSpaceAsync(s, "/tmp") > 0);
    }

    [Fact]
    public void ChangedHostKey_IsRejected()
    {
        RequireServer();
        Assert.SkipUnless(Host is "localhost" or "127.0.0.1", "Needs a local server reachable by both localhost and 127.0.0.1.");
        // Pre-seed bogus keys under an alias host name so the shared store's real entry is untouched.
        var s = Remote("/");
        s.Host = s.Host == "127.0.0.1" ? "localhost" : "127.0.0.1";
        foreach (var type in new[] { "ssh-ed25519", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
                                     "rsa-sha2-512", "rsa-sha2-256", "ssh-rsa" })
            KnownHostsStore.Default.CheckAndRecord(s.Host, s.Port, type, "SHA256:bogusbogusbogus");

        using var sftp = SshConnectionFactory.CreateSftp(s);
        var ex = Assert.Throws<HostKeyMismatchException>(() => sftp.ConnectVerified());
        Assert.Equal(s.Host, ex.Host);
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }
}
