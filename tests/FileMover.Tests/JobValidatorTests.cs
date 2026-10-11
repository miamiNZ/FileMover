namespace FileMover.Tests;

public class PermissionParserTests
{
    [Theory]
    [InlineData("644", 0b110_100_100)]
    [InlineData("0644", 0b110_100_100)]
    [InlineData("755", 0b111_101_101)]
    [InlineData(" 600 ", 0b110_000_000)]
    [InlineData("000", 0)]
    [InlineData("777", 0b111_111_111)]
    public void TryParse_AcceptsValidModes(string raw, int expected)
    {
        Assert.True(PermissionParser.TryParse(raw, out var mode));
        Assert.Equal((short)expected, mode);
    }

    [Theory]
    [InlineData(0b110_100_100, 644)]
    [InlineData(0b111_101_101, 755)]
    [InlineData(0b110_100_000, 640)]
    [InlineData(0, 0)]
    public void ToSshNetMode_ConvertsBitsToOctalDigitForm(int bits, short expected) =>
        Assert.Equal(expected, PermissionParser.ToSshNetMode((short)bits));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rwx")]
    [InlineData("648")]
    [InlineData("64")]
    [InlineData("4755")] // setuid
    [InlineData("2755")] // setgid
    [InlineData("1777")] // sticky
    [InlineData("-644")]
    [InlineData("77777")]
    public void TryParse_RejectsInvalidOrPrivilegedModes(string? raw)
    {
        Assert.False(PermissionParser.TryParse(raw, out var mode));
        Assert.Equal(0, mode);
    }
}

public class JobValidatorTests
{
    private static CopyJob ValidLocalJob() => new()
    {
        Source      = new EndpointSettings { Type = EndpointType.Local, Path = @"C:\src" },
        Destination = new EndpointSettings { Type = EndpointType.Local, Path = @"C:\dst" },
        Mode        = CopyMode.Folder,
        MaxThreads  = 4,
    };

    private static EndpointSettings ValidSsh(string path = "/data") => new()
    {
        Type = EndpointType.SSH, Path = path, Host = "example.test", Username = "user", Port = 22,
    };

    [Fact]
    public void Validate_ValidJob_HasNoErrors() => Assert.Empty(JobValidator.Validate(ValidLocalJob()));

    [Fact]
    public void Validate_ValidSshDestination_HasNoErrors()
    {
        var job = ValidLocalJob();
        job.Destination = ValidSsh();
        Assert.Empty(JobValidator.Validate(job));
    }

    [Fact]
    public void Validate_SshToSsh_IsRejected()
    {
        var job = ValidLocalJob();
        job.Source = ValidSsh();
        job.Destination = ValidSsh();
        Assert.Contains(JobValidator.Validate(job), e => e.Contains("SSH-to-SSH"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    public void Validate_ThreadCountOutOfRange_IsRejected(int threads)
    {
        var job = ValidLocalJob();
        job.MaxThreads = threads;
        Assert.Contains(JobValidator.Validate(job), e => e.Contains("Concurrent"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void Validate_ThreadCountBoundaries_AreAccepted(int threads)
    {
        var job = ValidLocalJob();
        job.MaxThreads = threads;
        Assert.Empty(JobValidator.Validate(job));
    }

    [Fact]
    public void Validate_MissingPaths_AreRejected()
    {
        var job = ValidLocalJob();
        job.Source.Path = "";
        job.Destination.Path = " ";
        var errors = JobValidator.Validate(job);
        Assert.Contains(errors, e => e.Contains("Source path"));
        Assert.Contains(errors, e => e.Contains("Destination path"));
    }

    [Fact]
    public void Validate_MultipleFilesWithoutFiles_IsRejected()
    {
        var job = ValidLocalJob();
        job.Mode = CopyMode.MultipleFiles;
        job.Source.Path = "";
        Assert.Contains(JobValidator.Validate(job), e => e.Contains("No source files"));
    }

    [Fact]
    public void Validate_SshEndpointWithoutHostUserOrPort_IsRejected()
    {
        var job = ValidLocalJob();
        job.Destination = new EndpointSettings { Type = EndpointType.SSH, Path = "/x", Port = 0 };
        var errors = JobValidator.Validate(job);
        Assert.Contains(errors, e => e.Contains("host"));
        Assert.Contains(errors, e => e.Contains("username"));
        Assert.Contains(errors, e => e.Contains("port"));
    }

    [Fact]
    public void Validate_BadPermissionModeForSshDestination_IsRejected()
    {
        var job = ValidLocalJob();
        job.Destination = ValidSsh();
        job.SetPermissions = true;
        job.PermissionMode = "4777";
        Assert.Contains(JobValidator.Validate(job), e => e.Contains("permission mode"));
    }

    [Fact]
    public void Validate_PermissionModeIgnoredForLocalDestination()
    {
        var job = ValidLocalJob();
        job.SetPermissions = true;
        job.PermissionMode = "garbage";
        Assert.Empty(JobValidator.Validate(job));
    }
}
