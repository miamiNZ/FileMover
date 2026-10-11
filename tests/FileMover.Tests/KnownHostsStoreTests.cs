namespace FileMover.Tests;

public class KnownHostsStoreTests
{
    [Fact]
    public void FirstConnection_IsRecordedAsNew_ThenTrusted()
    {
        using var tmp = new TempDir();
        var store = new KnownHostsStore(tmp.Combine("sub", "known_hosts"));

        Assert.Equal(HostKeyStatus.New, store.CheckAndRecord("Host.Example", 22, "ssh-ed25519", "abc123"));
        Assert.Equal(HostKeyStatus.Trusted, store.CheckAndRecord("host.example", 22, "ssh-ed25519", "SHA256:abc123"));
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void ChangedKey_IsReportedAsMismatch()
    {
        using var tmp = new TempDir();
        var store = new KnownHostsStore(tmp.Combine("known_hosts"));
        store.CheckAndRecord("h", 22, "ssh-ed25519", "aaa");

        Assert.Equal(HostKeyStatus.Mismatch, store.CheckAndRecord("h", 22, "ssh-ed25519", "bbb"));
        // A mismatch must not overwrite the trusted key.
        Assert.Equal(HostKeyStatus.Trusted, store.CheckAndRecord("h", 22, "ssh-ed25519", "aaa"));
    }

    [Fact]
    public void DifferentPortOrKeyType_IsTrackedSeparately()
    {
        using var tmp = new TempDir();
        var store = new KnownHostsStore(tmp.Combine("known_hosts"));
        store.CheckAndRecord("h", 22, "ssh-ed25519", "aaa");

        Assert.Equal(HostKeyStatus.New, store.CheckAndRecord("h", 2222, "ssh-ed25519", "bbb"));
        Assert.Equal(HostKeyStatus.New, store.CheckAndRecord("h", 22, "rsa-sha2-512", "ccc"));
    }

    [Fact]
    public void Forget_RemovesHostEntries()
    {
        using var tmp = new TempDir();
        var store = new KnownHostsStore(tmp.Combine("known_hosts"));
        store.CheckAndRecord("h", 22, "ssh-ed25519", "aaa");
        store.CheckAndRecord("h", 22, "rsa-sha2-512", "bbb");
        store.CheckAndRecord("other", 22, "ssh-ed25519", "ccc");

        Assert.Equal(2, store.Forget("H", 22));
        Assert.Equal(HostKeyStatus.New, store.CheckAndRecord("h", 22, "ssh-ed25519", "zzz"));
        Assert.Equal(HostKeyStatus.Trusted, store.CheckAndRecord("other", 22, "ssh-ed25519", "ccc"));
    }

    [Fact]
    public void CommentsAndMalformedLines_AreIgnored()
    {
        using var tmp = new TempDir();
        var path = tmp.WriteFile("known_hosts", "# comment\n\nbroken line\nh:22 ssh-ed25519 SHA256:aaa\n");
        var store = new KnownHostsStore(path);

        Assert.Equal(HostKeyStatus.Trusted, store.CheckAndRecord("h", 22, "ssh-ed25519", "aaa"));
    }

    [Theory]
    [InlineData("", "t", "f")]
    [InlineData("h", "", "f")]
    [InlineData("h", "t", "")]
    public void MissingArguments_Throw(string host, string type, string fp)
    {
        using var tmp = new TempDir();
        var store = new KnownHostsStore(tmp.Combine("known_hosts"));
        Assert.Throws<ArgumentException>(() => store.CheckAndRecord(host, 22, type, fp));
    }

    [Fact]
    public async Task ConcurrentFirstConnections_RecordSingleEntry()
    {
        using var tmp = new TempDir();
        var store = new KnownHostsStore(tmp.Combine("known_hosts"));

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => store.CheckAndRecord("h", 22, "ssh-ed25519", "aaa"), TestContext.Current.CancellationToken)));

        Assert.Single(results, r => r == HostKeyStatus.New);
        Assert.Single(File.ReadAllLines(store.FilePath));
    }
}
