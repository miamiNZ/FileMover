using System.Collections.Concurrent;

namespace FileMover.Tests;

/// <summary>End-to-end Local → Local runs of the real copy engine against temp folders.</summary>
public class CopyEngineLocalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CopyJob FolderJob(string src, string dst, Action<CopyJob>? configure = null)
    {
        var job = new CopyJob
        {
            Source      = new EndpointSettings { Type = EndpointType.Local, Path = src },
            Destination = new EndpointSettings { Type = EndpointType.Local, Path = dst },
            Mode        = CopyMode.Folder,
            MaxThreads  = 4,
            VerifyFiles = true,
        };
        configure?.Invoke(job);
        return job;
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<LogEntry> Logs { get; } = new();
        public ConcurrentQueue<ProgressSnapshot> Progress { get; } = new();

        public CopyEngine Attach(CopyEngine engine)
        {
            engine.LogMessage      += (_, e) => Logs.Enqueue(e);
            engine.ProgressChanged += (_, p) => Progress.Enqueue(p);
            return engine;
        }

        public bool Has(LogLevel level, string text) => Logs.Any(l => l.Level == level && l.Message.Contains(text));
    }

    [Fact]
    public async Task Folder_Recursive_CopiesTreeAndVerifies()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.WriteFile("a.txt", "alpha");
        src.WriteFile("sub/b.txt", "bravo");
        src.WriteFile("sub/deeper/c.bin", RandomBytes(5 * 1024 * 1024 + 123)); // spans multiple 4 MB buffers

        var rec    = new Recorder();
        var engine = rec.Attach(new CopyEngine());
        var job    = FolderJob(src.Path, dst.Path);

        Assert.True(await engine.ExecuteAsync(job, Ct));

        AssertSameFile(src.Combine("a.txt"), dst.Combine("a.txt"));
        AssertSameFile(src.Combine("sub", "b.txt"), dst.Combine("sub", "b.txt"));
        AssertSameFile(src.Combine("sub", "deeper", "c.bin"), dst.Combine("sub", "deeper", "c.bin"));
        Assert.True(rec.Has(LogLevel.Success, "(verified)"));
        Assert.Null(CopySessionManager.TryLoad(job)); // session removed on success

        var last = rec.Progress.Last();
        Assert.Equal(3, last.TotalFiles);
        Assert.Equal(3, last.CompletedFiles);
        Assert.Equal(0, last.FailedFiles);
        Assert.Equal(100.0, last.OverallPercent);
    }

    [Fact]
    public async Task Folder_NonRecursive_SkipsSubdirectories()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.WriteFile("top.txt", "top");
        src.WriteFile("sub/nested.txt", "nested");

        var job = FolderJob(src.Path, dst.Path, j => j.Recursive = false);
        Assert.True(await new CopyEngine().ExecuteAsync(job, Ct));

        Assert.True(File.Exists(dst.Combine("top.txt")));
        Assert.False(Directory.Exists(dst.Combine("sub")));
    }

    [Fact]
    public async Task SingleFile_CopiesIntoDestinationFolder()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var file = src.WriteFile("one.txt", "single");

        var job = FolderJob(file, dst.Path, j => j.Mode = CopyMode.File);
        Assert.True(await new CopyEngine().ExecuteAsync(job, Ct));

        Assert.Equal("single", File.ReadAllText(dst.Combine("one.txt")));
    }

    [Fact]
    public async Task MultipleFiles_AreFlattenedIntoDestination()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var f1 = src.WriteFile("x/one.txt", "1");
        var f2 = src.WriteFile("y/two.txt", "2");

        var job = FolderJob("", dst.Path, j =>
        {
            j.Mode = CopyMode.MultipleFiles;
            j.SourceFiles = [f1, f2];
        });
        Assert.True(await new CopyEngine().ExecuteAsync(job, Ct));

        Assert.Equal("1", File.ReadAllText(dst.Combine("one.txt")));
        Assert.Equal("2", File.ReadAllText(dst.Combine("two.txt")));
    }

    [Fact]
    public async Task Move_DeletesSourcesAndEmptiedFolders()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var root = src.Combine("payload");
        src.WriteFile("payload/a.txt", "a");
        src.WriteFile("payload/sub/b.txt", "b");

        var job = FolderJob(root, dst.Path, j => j.OperationMode = OperationMode.Move);
        Assert.True(await new CopyEngine().ExecuteAsync(job, Ct));

        Assert.True(File.Exists(dst.Combine("a.txt")));
        Assert.True(File.Exists(dst.Combine("sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(root, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(root, "sub")));
    }

    [Fact]
    public async Task OverwriteOff_LeavesExistingDestinationUntouched()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.WriteFile("same.txt", "new content");
        src.WriteFile("fresh.txt", "fresh");
        dst.WriteFile("same.txt", "original");

        var rec = new Recorder();
        var job = FolderJob(src.Path, dst.Path, j => j.OverwriteExisting = false);
        Assert.True(await rec.Attach(new CopyEngine()).ExecuteAsync(job, Ct));

        Assert.Equal("original", File.ReadAllText(dst.Combine("same.txt")));
        Assert.Equal("fresh", File.ReadAllText(dst.Combine("fresh.txt")));
        Assert.True(rec.Has(LogLevel.Info, "overwrite off"));
    }

    [Fact]
    public async Task OverwriteOn_ReplacesExistingDestination()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.WriteFile("same.txt", "new");
        dst.WriteFile("same.txt", "much longer original content");

        Assert.True(await new CopyEngine().ExecuteAsync(FolderJob(src.Path, dst.Path), Ct));
        Assert.Equal("new", File.ReadAllText(dst.Combine("same.txt")));
    }

    [Fact]
    public async Task Resume_AppendsFromPartialDestinationFile()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var data    = RandomBytes(256 * 1024);
        var srcFile = src.WriteFile("big.bin", data);
        var dstFile = dst.WriteFile("big.bin", data[..100_000]); // simulate an interrupted transfer
        var done    = src.WriteFile("done.txt", "done");
        dst.WriteFile("done.txt", "done");

        var job = FolderJob(src.Path, dst.Path);
        var session = new CopySessionData
        {
            Files =
            [
                new SessionFileEntry { SourcePath = srcFile, DestinationPath = dstFile, Size = data.Length, Status = "Pending" },
                new SessionFileEntry { SourcePath = done, DestinationPath = dst.Combine("done.txt"), Size = 4, Status = "Completed" },
            ],
        };
        CopySessionManager.Save(job, session);

        var rec = new Recorder();
        Assert.True(await rec.Attach(new CopyEngine()).ExecuteAsync(job, Ct, session));

        AssertSameFile(srcFile, dstFile);
        Assert.True(rec.Has(LogLevel.Info, "Resuming session"));
        Assert.True(rec.Has(LogLevel.Info, "resuming from"));
        Assert.Equal(2, rec.Progress.Last().CompletedFiles);
        Assert.Null(CopySessionManager.TryLoad(job));
    }

    [Fact]
    public async Task Cancel_DuringCopy_KeepsSessionForResume()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        for (int i = 0; i < 5; i++) src.WriteFile($"f{i}.txt", new string('x', 1000));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var rec    = new Recorder();
        var engine = rec.Attach(new CopyEngine());
        engine.LogMessage += (_, e) => { if (e.Message.StartsWith("Found")) cts.Cancel(); };

        var job = FolderJob(src.Path, dst.Path);
        Assert.False(await engine.ExecuteAsync(job, cts.Token));

        Assert.True(rec.Has(LogLevel.Warning, "Cancelled"));
        var saved = CopySessionManager.TryLoad(job);
        Assert.NotNull(saved);
        Assert.Equal(5, saved.TotalCount);
        CopySessionManager.Delete(job);
    }

    [Fact]
    public async Task EmptyFolder_ReturnsFalseWithWarning()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        var rec = new Recorder();

        Assert.False(await rec.Attach(new CopyEngine()).ExecuteAsync(FolderJob(src.Path, dst.Path), Ct));
        Assert.True(rec.Has(LogLevel.Warning, "No files found"));
    }

    [Fact]
    public async Task MissingSource_ReturnsFalseWithError()
    {
        using var dst = new TempDir();
        var rec = new Recorder();
        var job = FolderJob(Path.Combine(dst.Path, "does-not-exist"), dst.Path);

        Assert.False(await rec.Attach(new CopyEngine()).ExecuteAsync(job, Ct));
        Assert.True(rec.Has(LogLevel.Error, "Source directory not found"));
    }

    [Fact]
    public async Task InvalidJob_IsRejectedBeforeTouchingFiles()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.WriteFile("a.txt", "a");
        var rec = new Recorder();
        var job = FolderJob(src.Path, dst.Path, j => j.MaxThreads = 0);

        Assert.False(await rec.Attach(new CopyEngine()).ExecuteAsync(job, Ct));
        Assert.True(rec.Has(LogLevel.Error, "Concurrent transfers"));
        Assert.False(File.Exists(dst.Combine("a.txt")));
    }

    [Fact]
    public async Task Engine_CanBeReusedForSecondJob()
    {
        using var src = new TempDir();
        using var dst1 = new TempDir();
        using var dst2 = new TempDir();
        src.WriteFile("a.txt", "a");

        var rec    = new Recorder();
        var engine = rec.Attach(new CopyEngine());
        Assert.True(await engine.ExecuteAsync(FolderJob(src.Path, dst1.Path), Ct));
        Assert.True(await engine.ExecuteAsync(FolderJob(src.Path, dst2.Path), Ct));

        Assert.Equal(1, rec.Progress.Last().CompletedFiles); // counters reset between runs
    }

    private static byte[] RandomBytes(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    private static void AssertSameFile(string expected, string actual)
    {
        Assert.True(File.Exists(actual), $"Missing: {actual}");
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
    }
}
