namespace FileMover.Tests;

public class ProgressSnapshotTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1_048_576L, "1.00 MB")]
    [InlineData(1_073_741_824L, "1.00 GB")]
    [InlineData(5_368_709_120L, "5.00 GB")]
    public void FormatBytes_UsesBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, ProgressSnapshot.FormatBytes(bytes).Replace(',', '.'));

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(100, 50, 50)]
    [InlineData(100, 150, 100)] // clamped
    public void OverallPercent_IsClamped(long total, long copied, double expected) =>
        Assert.Equal(expected, new ProgressSnapshot { TotalBytes = total, CopiedBytes = copied }.OverallPercent);

    [Theory]
    [InlineData(500, "500 B/s")]
    [InlineData(2048, "2.0 KB/s")]
    [InlineData(3 * 1_048_576, "3.0 MB/s")]
    [InlineData(2.5 * 1_073_741_824, "2.5 GB/s")]
    public void SpeedText_PicksUnit(double bps, string expected) =>
        Assert.Equal(expected, new ProgressSnapshot { SpeedBytesPerSecond = bps }.SpeedText.Replace(',', '.'));

    [Fact]
    public void EtaText_FormatsHoursMinutesSeconds()
    {
        Assert.Equal("--:--:--", new ProgressSnapshot().ETAText);
        Assert.Equal("01:02:03", new ProgressSnapshot { ETA = new TimeSpan(1, 2, 3) }.ETAText);
        Assert.Equal("26:00:00", new ProgressSnapshot { ETA = TimeSpan.FromHours(26) }.ETAText);
    }

    [Fact]
    public void FilesAndBytesText_Format()
    {
        var s = new ProgressSnapshot { TotalFiles = 10, CompletedFiles = 7, FailedFiles = 1, TotalBytes = 2048, CopiedBytes = 1024 };
        Assert.Equal("7/10 (1 failed)", s.FilesText);
        Assert.Equal("1.00 KB / 2.00 KB", s.BytesText.Replace(',', '.'));
    }
}

public class LogEntryTests
{
    [Theory]
    [InlineData(LogLevel.Info, "#c8c8c8")]
    [InlineData(LogLevel.Success, "#4ec9b0")]
    [InlineData(LogLevel.Warning, "#ffc53d")]
    [InlineData(LogLevel.Error, "#f44747")]
    public void Color_MapsLevel(LogLevel level, string color) =>
        Assert.Equal(color, new LogEntry { Level = level }.Color);

    [Fact]
    public void FormattedMessage_PrefixesTime()
    {
        var e = new LogEntry { Timestamp = new DateTime(2026, 1, 2, 13, 4, 5), Message = "hello" };
        Assert.Equal("[13:04:05] hello", e.FormattedMessage);
    }

    [Fact]
    public void Brush_IsCreatedForEveryLevel()
    {
        foreach (var level in Enum.GetValues<LogLevel>())
            Assert.NotNull(new LogEntry { Level = level }.Brush);
    }
}

public class DiskSpaceHelperTests
{
    [Theory]
    [InlineData("1024\n", 1024L * 1024)]
    [InlineData("  0 ", 0L)]
    [InlineData("", -1L)]
    [InlineData(null, -1L)]
    [InlineData("df: no such file", -1L)]
    [InlineData("-5", -1L)]
    public void ParseDfKilobytes_ConvertsToBytes(string? output, long expected) =>
        Assert.Equal(expected, DiskSpaceHelper.ParseDfKilobytes(output));

    [Fact]
    public void GetLocalFreeSpace_ReturnsPositiveForTempPath() =>
        Assert.True(DiskSpaceHelper.GetLocalFreeSpace(Path.GetTempPath()) > 0);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Q:\\definitely\\missing")]
    public void GetLocalFreeSpace_ReturnsMinusOneForBadPath(string path) =>
        Assert.Equal(-1, DiskSpaceHelper.GetLocalFreeSpace(path));

    [Fact]
    public async Task GetSshFreeSpaceAsync_WithoutHost_ReturnsMinusOne() =>
        Assert.Equal(-1, await DiskSpaceHelper.GetSshFreeSpaceAsync(new EndpointSettings { Type = EndpointType.SSH }, "/"));

    [Fact]
    public async Task GetLocalSourceSizeAsync_SumsPerMode()
    {
        using var tmp = new TempDir();
        var a = tmp.WriteFile("a.txt", new byte[100]);
        var b = tmp.WriteFile("sub/b.txt", new byte[50]);

        async Task<long> Size(Action<CopyJob> cfg)
        {
            var job = new CopyJob { Source = new EndpointSettings { Path = tmp.Path } };
            cfg(job);
            return await DiskSpaceHelper.GetLocalSourceSizeAsync(job);
        }

        Assert.Equal(150, await Size(j => j.Mode = CopyMode.Folder));
        Assert.Equal(100, await Size(j => { j.Mode = CopyMode.Folder; j.Recursive = false; }));
        Assert.Equal(100, await Size(j => { j.Mode = CopyMode.File; j.Source.Path = a; }));
        Assert.Equal(150, await Size(j => { j.Mode = CopyMode.MultipleFiles; j.SourceFiles = [a, b]; }));
        Assert.Equal(-1, await Size(j => { j.Mode = CopyMode.File; j.Source.Path = tmp.Combine("missing"); }));
    }
}
