using System.Runtime.CompilerServices;

namespace FileMover.Tests;

internal static class TestSetup
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "FileMover.Tests", Guid.NewGuid().ToString("N"));

    // Keep tests away from the real %LOCALAPPDATA%\FileMover folder.
    [ModuleInitializer]
    public static void Init()
    {
        Directory.CreateDirectory(Root);
        CopySessionManager.SessionsDirectory = Path.Combine(Root, "sessions");
        KnownHostsStore.Default = new KnownHostsStore(Path.Combine(Root, "known_hosts"));
    }
}

/// <summary>Self-deleting scratch directory.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(TestSetup.Root, Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content) =>
        WriteFile(relative, System.Text.Encoding.UTF8.GetBytes(content));

    public string WriteFile(string relative, byte[] content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
