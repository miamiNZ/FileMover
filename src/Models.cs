using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Media;

namespace FileMover;

public enum EndpointType { Local, SSH }
public enum CopyMode { File, MultipleFiles, Folder }
public enum OperationMode { Copy, Move }
public enum FileStatus { Pending, Copying, Verifying, Completed, Failed, Skipped }
public enum LogLevel { Info, Success, Warning, Error }

public class EndpointSettings
{
    public EndpointType Type { get; set; } = EndpointType.Local;
    public string Path { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string KeyFilePath { get; set; } = string.Empty;
    public bool UseKeyFile { get; set; } = false;
}

public class CopyJob
{
    public EndpointSettings Source { get; set; } = new();
    public EndpointSettings Destination { get; set; } = new();
    public CopyMode Mode { get; set; } = CopyMode.Folder;
    public List<string> SourceFiles { get; set; } = new();
    public OperationMode OperationMode { get; set; } = OperationMode.Copy;
    public bool VerifyFiles { get; set; } = true;
    public bool OverwriteExisting { get; set; } = true;
    public int MaxThreads { get; set; } = 4;
    public bool Recursive { get; set; } = true;
    public bool SetPermissions { get; set; } = true;
    public string PermissionMode { get; set; } = "644";
}

public class FileEntry
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public long ResumeOffset { get; set; }
    public FileStatus Status { get; set; } = FileStatus.Pending;
    public string? ErrorMessage { get; set; }
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Message { get; set; } = string.Empty;
    public LogLevel Level { get; set; } = LogLevel.Info;

    public string FormattedMessage => $"[{Timestamp:HH:mm:ss}] {Message}";

    // Hex color for web / SignalR serialization
    public string Color => Level switch
    {
        LogLevel.Success => "#4ec9b0",
        LogLevel.Error   => "#f44747",
        LogLevel.Warning => "#ffc53d",
        _                => "#c8c8c8",
    };

    // WPF brush for desktop XAML data binding (not serialized)
    [JsonIgnore]
    public Brush Brush => Level switch
    {
        LogLevel.Success => new SolidColorBrush(System.Windows.Media.Color.FromRgb(78,  201, 176)),
        LogLevel.Error   => new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 71,  71)),
        LogLevel.Warning => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 197, 61)),
        _                => new SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 200, 200)),
    };
}

public class ProgressSnapshot
{
    public int TotalFiles { get; init; }
    public int CompletedFiles { get; init; }
    public int FailedFiles { get; init; }
    public long TotalBytes { get; init; }
    public long CopiedBytes { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan? ETA { get; init; }
    public string CurrentFileName { get; init; } = string.Empty;
    public double CurrentFilePercent { get; init; }
    public long CurrentFileCopied { get; init; }
    public long CurrentFileSize { get; init; }

    public double OverallPercent => TotalBytes > 0
        ? Math.Min(100.0, (double)CopiedBytes / TotalBytes * 100.0) : 0;

    public string SpeedText => SpeedBytesPerSecond switch
    {
        >= 1_073_741_824 => $"{SpeedBytesPerSecond / 1_073_741_824.0:F1} GB/s",
        >= 1_048_576     => $"{SpeedBytesPerSecond / 1_048_576.0:F1} MB/s",
        >= 1024          => $"{SpeedBytesPerSecond / 1024.0:F1} KB/s",
        _                => $"{SpeedBytesPerSecond:F0} B/s",
    };

    public string ETAText => ETA.HasValue
        ? $"{(int)ETA.Value.TotalHours:D2}:{ETA.Value.Minutes:D2}:{ETA.Value.Seconds:D2}"
        : "--:--:--";

    public string FilesText => $"{CompletedFiles}/{TotalFiles} ({FailedFiles} failed)";

    public string BytesText => $"{FormatBytes(CopiedBytes)} / {FormatBytes(TotalBytes)}";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F2} MB",
        >= 1024          => $"{bytes / 1024.0:F2} KB",
        _                => $"{bytes} B",
    };
}

public static class DiskSpaceHelper
{
    public static long GetLocalFreeSpace(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return -1;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : -1;
        }
        catch { return -1; }
    }

    public static async Task<long> GetSshFreeSpaceAsync(EndpointSettings s, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(s.Host)) return -1;
        try
        {
            return await Task.Run(() =>
            {
                using var ssh = SshConnectionFactory.CreateSsh(s, TimeSpan.FromSeconds(15));
                ssh.ConnectVerified();
                try
                {
                    using var cmd = ssh.RunCommand($"df -Pk {PathHelpers.ShellQuote(path)} 2>/dev/null | awk 'NR==2 {{print $4}}'");
                    return ParseDfKilobytes(cmd.Result);
                }
                finally { ssh.Disconnect(); }
            });
        }
        catch { return -1; }
    }

    public static async Task<long> GetLocalSourceSizeAsync(CopyJob job)
    {
        return await Task.Run(() =>
        {
            try
            {
                return job.Mode switch
                {
                    CopyMode.File          => new FileInfo(job.Source.Path).Length,
                    CopyMode.MultipleFiles => job.SourceFiles.Sum(f => new FileInfo(f).Length),
                    CopyMode.Folder        => Directory
                        .GetFiles(job.Source.Path, "*",
                            job.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                        .Sum(f => new FileInfo(f).Length),
                    _                      => -1L,
                };
            }
            catch { return -1L; }
        });
    }

    /// <summary>Converts the KB figure printed by <c>df -Pk … | awk</c> to bytes; -1 when unparseable.</summary>
    public static long ParseDfKilobytes(string? output) =>
        long.TryParse(output?.Trim(), out long kb) && kb >= 0 ? kb * 1024L : -1L;
}
