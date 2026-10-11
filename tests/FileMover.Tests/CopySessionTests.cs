namespace FileMover.Tests;

public class CopySessionTests
{
    private static CopyJob Job(string src, string dst) => new()
    {
        Source      = new EndpointSettings { Type = EndpointType.Local, Path = src },
        Destination = new EndpointSettings { Type = EndpointType.Local, Path = dst },
    };

    [Fact]
    public void SessionPath_IsStableForSameJob_AndDiffersOtherwise()
    {
        var a1 = CopySessionManager.GetSessionPath(Job(@"C:\a", @"D:\x"));
        var a2 = CopySessionManager.GetSessionPath(Job(@"C:\a", @"D:\x"));
        var b  = CopySessionManager.GetSessionPath(Job(@"C:\b", @"D:\x"));

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        Assert.StartsWith(CopySessionManager.SessionsDirectory, a1);
        Assert.Matches("[0-9A-F]{64}\\.json$", a1);
    }

    [Fact]
    public void SessionPath_MultipleFiles_IgnoresSelectionOrder()
    {
        var j1 = Job("", @"D:\x"); j1.Mode = CopyMode.MultipleFiles; j1.SourceFiles = ["b", "a"];
        var j2 = Job("", @"D:\x"); j2.Mode = CopyMode.MultipleFiles; j2.SourceFiles = ["a", "b"];
        Assert.Equal(CopySessionManager.GetSessionPath(j1), CopySessionManager.GetSessionPath(j2));
    }

    [Fact]
    public void SaveLoadDelete_RoundTrips()
    {
        var job = Job(@"C:\round-trip-" + Guid.NewGuid(), @"D:\x");
        var data = new CopySessionData
        {
            Files =
            [
                new SessionFileEntry { SourcePath = "s1", DestinationPath = "d1", Size = 10, Status = "Completed" },
                new SessionFileEntry { SourcePath = "s2", DestinationPath = "d2", Size = 30, Status = "Pending" },
            ],
        };

        CopySessionManager.Save(job, data);
        var loaded = CopySessionManager.TryLoad(job);

        Assert.NotNull(loaded);
        Assert.Equal(data.SessionId, loaded.SessionId);
        Assert.Equal(2, loaded.TotalCount);
        Assert.Equal(1, loaded.CompletedCount);
        Assert.Equal(40, loaded.TotalBytes);
        Assert.Equal(10, loaded.CompletedBytes);
        Assert.False(File.Exists(CopySessionManager.GetSessionPath(job) + ".tmp"));

        CopySessionManager.Delete(job);
        Assert.Null(CopySessionManager.TryLoad(job));
    }

    [Fact]
    public void TryLoad_CorruptFile_ReturnsNull()
    {
        var job = Job(@"C:\corrupt-" + Guid.NewGuid(), @"D:\x");
        File.WriteAllText(CopySessionManager.GetSessionPath(job), "{ not json");
        Assert.Null(CopySessionManager.TryLoad(job));
        CopySessionManager.Delete(job);
    }

    [Fact]
    public void Delete_MissingSession_DoesNotThrow() =>
        CopySessionManager.Delete(Job(@"C:\never-" + Guid.NewGuid(), @"D:\x"));

    [Fact]
    public void NewSession_HasShortIdAndTimestamps()
    {
        var s = new CopySessionData();
        Assert.Equal(8, s.SessionId.Length);
        Assert.True(s.Created <= DateTime.Now);
    }
}
