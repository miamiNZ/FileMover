namespace FileMover.Tests;

public class DtoMappingTests
{
    [Fact]
    public void EndpointDto_MapsFieldsAndDefaultsPort()
    {
        var s = new EndpointDto
        {
            Type = "SSH", Path = "/p", Host = "h", Port = 0, Username = "u",
            Password = "pw", UseKeyFile = true, KeyFilePath = "k",
        }.ToSettings();

        Assert.Equal(EndpointType.SSH, s.Type);
        Assert.Equal(22, s.Port);
        Assert.Equal(("/p", "h", "u", "pw", true, "k"), (s.Path, s.Host, s.Username, s.Password, s.UseKeyFile, s.KeyFilePath));
    }

    [Theory]
    [InlineData("Local", EndpointType.Local)]
    [InlineData("ssh", EndpointType.Local)] // exact match only
    [InlineData("anything", EndpointType.Local)]
    public void EndpointDto_UnknownTypeFallsBackToLocal(string type, EndpointType expected) =>
        Assert.Equal(expected, new EndpointDto { Type = type }.ToSettings().Type);

    [Theory]
    [InlineData("File", CopyMode.File)]
    [InlineData("MultipleFiles", CopyMode.MultipleFiles)]
    [InlineData("Folder", CopyMode.Folder)]
    [InlineData("bogus", CopyMode.Folder)]
    public void CopyJobDto_MapsMode(string mode, CopyMode expected) =>
        Assert.Equal(expected, new CopyJobDto { Mode = mode }.ToCopyJob().Mode);

    [Fact]
    public void CopyJobDto_MapsOptions()
    {
        var job = new CopyJobDto
        {
            OperationMode = "Move", VerifyFiles = false, OverwriteExisting = false, Recursive = false,
            MaxThreads = 8, SetPermissions = false, PermissionMode = "600", SourceFiles = ["a"],
        }.ToCopyJob();

        Assert.Equal(OperationMode.Move, job.OperationMode);
        Assert.False(job.VerifyFiles);
        Assert.False(job.OverwriteExisting);
        Assert.False(job.Recursive);
        Assert.Equal(8, job.MaxThreads);
        Assert.False(job.SetPermissions);
        Assert.Equal("600", job.PermissionMode);
        Assert.Equal(["a"], job.SourceFiles);
    }
}

public class CopyHubBrowseTests
{
    private static CopyHub Hub() => new(null!);

    [Fact]
    public async Task BrowseLocal_ListsFoldersFirstThenFilesAlphabetically()
    {
        using var tmp = new TempDir();
        tmp.WriteFile("b.txt", "");
        tmp.WriteFile("A.txt", "");
        Directory.CreateDirectory(tmp.Combine("zdir"));
        Directory.CreateDirectory(tmp.Combine("adir"));

        var r = await Hub().BrowseLocal(tmp.Path);

        Assert.Null(r.Error);
        Assert.Equal(tmp.Path, r.CurrentPath);
        Assert.NotNull(r.ParentPath);
        Assert.Equal(["adir", "zdir", "A.txt", "b.txt"], r.Entries.Select(e => e.Name));
        Assert.True(r.Entries[0].IsDirectory);
        Assert.False(r.Entries[2].IsDirectory);
    }

    [Fact]
    public async Task BrowseLocal_FilePath_ListsContainingFolder()
    {
        using var tmp = new TempDir();
        var file = tmp.WriteFile("x.txt", "");
        Assert.Equal(tmp.Path, (await Hub().BrowseLocal(file)).CurrentPath);
    }

    [Fact]
    public async Task BrowseLocal_EmptyPath_ListsDrives()
    {
        var r = await Hub().BrowseLocal("");
        Assert.Null(r.Error);
        Assert.NotEmpty(r.Entries);
        Assert.All(r.Entries, e => Assert.True(e.IsDirectory));
    }

    [Fact]
    public async Task BrowseLocal_MissingFolder_ReturnsError()
    {
        using var tmp = new TempDir();
        var r = await Hub().BrowseLocal(tmp.Combine("missing"));
        Assert.NotNull(r.Error);
        Assert.Empty(r.Entries);
    }

    [Fact]
    public async Task BrowseSsh_Unreachable_ReturnsErrorInsteadOfThrowing()
    {
        var r = await Hub().BrowseSsh(new EndpointDto { Type = "SSH", Host = "", Username = "u" }, "/");
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task TestSshConnection_MissingHost_Fails()
    {
        var r = await Hub().TestSshConnection(new EndpointDto { Type = "SSH", Host = "", Username = "u" });
        Assert.False(r.Success);
        Assert.Contains("host", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSourceSize_SshSource_IsSkipped() =>
        Assert.Equal(-1, await Hub().GetSourceSize(new CopyJobDto { Source = new EndpointDto { Type = "SSH" } }));

    [Fact]
    public async Task GetDestFreeSpace_Local_ReturnsDriveSpace() =>
        Assert.True(await Hub().GetDestFreeSpace(new EndpointDto(), Path.GetTempPath()) > 0);
}

public class SshConnectionFactoryTests
{
    [Fact]
    public void BuildConnectionInfo_PasswordAuth_UsesDefaultTimeout()
    {
        var info = SshConnectionFactory.BuildConnectionInfo(
            new EndpointSettings { Host = "h", Port = 2222, Username = "u", Password = "p" }, null);

        Assert.Equal("h", info.Host);
        Assert.Equal(2222, info.Port);
        Assert.Equal(SshConnectionFactory.DefaultTimeout, info.Timeout);
        Assert.IsType<Renci.SshNet.PasswordAuthenticationMethod>(Assert.Single(info.AuthenticationMethods));
    }

    [Fact]
    public void BuildConnectionInfo_KeyFileFlagWithoutPath_FallsBackToPassword()
    {
        var info = SshConnectionFactory.BuildConnectionInfo(
            new EndpointSettings { Host = "h", Username = "u", UseKeyFile = true }, TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(5), info.Timeout);
        Assert.IsType<Renci.SshNet.PasswordAuthenticationMethod>(Assert.Single(info.AuthenticationMethods));
    }

    [Fact]
    public void BuildConnectionInfo_MissingKeyFile_Throws() =>
        Assert.ThrowsAny<Exception>(() => SshConnectionFactory.BuildConnectionInfo(
            new EndpointSettings { Host = "h", Username = "u", UseKeyFile = true, KeyFilePath = @"C:\no\such\key" }, null));

    [Theory]
    [InlineData("", "u")]
    [InlineData("h", "")]
    public void BuildConnectionInfo_RequiresHostAndUser(string host, string user) =>
        Assert.Throws<ArgumentException>(() => SshConnectionFactory.BuildConnectionInfo(
            new EndpointSettings { Host = host, Username = user }, null));

    [Fact]
    public void CreateClients_DoNotConnectEagerly()
    {
        var s = new EndpointSettings { Host = "h", Username = "u", Password = "p" };
        using var sftp = SshConnectionFactory.CreateSftp(s);
        using var ssh  = SshConnectionFactory.CreateSsh(s);
        Assert.False(sftp.IsConnected);
        Assert.False(ssh.IsConnected);
    }

    [Fact]
    public void HostKeyMismatchException_ExplainsRemedy()
    {
        var ex = new HostKeyMismatchException("h", 22, @"C:\kh");
        Assert.Contains("h:22", ex.Message);
        Assert.Contains(@"C:\kh", ex.Message);
    }
}
