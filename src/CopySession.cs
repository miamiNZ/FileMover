using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMover;

public class SessionFileEntry
{
    public string SourcePath      { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long   Size            { get; set; }
    public string Status          { get; set; } = "Pending"; // Pending | Completed | Failed
}

public class CopySessionData
{
    public string   SessionId   { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Created     { get; set; } = DateTime.Now;
    public DateTime LastUpdated { get; set; } = DateTime.Now;

    public List<SessionFileEntry> Files { get; set; } = new();

    [JsonIgnore] public int  CompletedCount => Files.Count(f => f.Status == "Completed");
    [JsonIgnore] public int  TotalCount     => Files.Count;
    [JsonIgnore] public long CompletedBytes => Files.Where(f => f.Status == "Completed").Sum(f => f.Size);
    [JsonIgnore] public long TotalBytes     => Files.Sum(f => f.Size);
}

public static class CopySessionManager
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    // Stable filename derived from the job's source+destination so the same job always maps to the same file.
    public static string GetSessionPath(CopyJob job)
    {
        var srcKey = job.Mode == CopyMode.MultipleFiles
            ? string.Join("|", job.SourceFiles.OrderBy(f => f))
            : job.Source.Path;
        var key  = $"{job.Source.Type}|{job.Source.Host}|{srcKey}|{job.Destination.Type}|{job.Destination.Host}|{job.Destination.Path}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileMover", "sessions");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{hash}.json");
    }

    public static CopySessionData? TryLoad(CopyJob job)
    {
        try
        {
            var path = GetSessionPath(job);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CopySessionData>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    // Atomic write (temp → rename) so a crash mid-save never corrupts the file.
    public static void Save(CopyJob job, CopySessionData session)
    {
        try
        {
            session.LastUpdated = DateTime.Now;
            var path = GetSessionPath(job);
            var tmp  = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(session, _opts));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* session save is best-effort */ }
    }

    public static void Delete(CopyJob job)
    {
        try { File.Delete(GetSessionPath(job)); } catch { }
    }
}
