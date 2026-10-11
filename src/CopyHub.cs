using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.SignalR;

namespace FileMover;

public class CopyHub : Hub
{
    private readonly IHubContext<CopyHub> _ctx;

    private static CancellationTokenSource? _cts;
    private static string? _connId;
    private static readonly SemaphoreSlim _gate = new(1, 1);

    public CopyHub(IHubContext<CopyHub> ctx) => _ctx = ctx;

    public async Task StartCopy(CopyJobDto dto)
    {
        if (!await _gate.WaitAsync(0))
        {
            await Clients.Caller.SendAsync("Busy");
            return;
        }

        _connId = Context.ConnectionId;
        _cts    = new CancellationTokenSource();
        var connId = _connId;

        try
        {
            await _ctx.Clients.Client(connId).SendAsync("StateChanged", "copying");

            var job = dto.ToCopyJob();
            var errors = JobValidator.Validate(job);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                    await _ctx.Clients.Client(connId).SendAsync("Log",
                        new { text = $"[{DateTime.Now:HH:mm:ss}] {error}", color = "#f44747" });
                return;
            }

            CopySessionManager.Delete(job);

            var engine = new CopyEngine();

            engine.LogMessage += (_, e) =>
                _ = _ctx.Clients.Client(connId).SendAsync("Log",
                    new { text = e.FormattedMessage, color = e.Color });

            engine.ProgressChanged += (_, s) =>
                _ = _ctx.Clients.Client(connId).SendAsync("Progress", new
                {
                    overallPct      = s.OverallPercent,
                    currentPct      = s.CurrentFilePercent,
                    currentFileName = s.CurrentFileName,
                    speed           = s.SpeedBytesPerSecond > 0 ? s.SpeedText : "—",
                    eta             = s.ETA.HasValue ? s.ETAText : "—",
                    files           = s.TotalFiles > 0 ? s.FilesText : "—",
                    bytes           = s.TotalBytes > 0 ? s.BytesText : "—",
                });

            await engine.ExecuteAsync(job, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _ctx.Clients.Client(connId).SendAsync("Log",
                new { text = $"[{DateTime.Now:HH:mm:ss}] Error: {ex.Message}", color = "#f44747" });
        }
        finally
        {
            _cts    = null;
            _connId = null;
            _gate.Release();
            await _ctx.Clients.Client(connId).SendAsync("StateChanged", "ready");
        }
    }

    public void Cancel() => _cts?.Cancel();

    // ── Disk space ────────────────────────────────────────────────

    public async Task<long> GetDestFreeSpace(EndpointDto endpoint, string path)
    {
        var s = endpoint.ToSettings();
        if (s.Type == EndpointType.SSH)
            return await DiskSpaceHelper.GetSshFreeSpaceAsync(s, path);
        return DiskSpaceHelper.GetLocalFreeSpace(path);
    }

    public async Task<long> GetSourceSize(CopyJobDto dto)
    {
        var job = dto.ToCopyJob();
        if (job.Source.Type != EndpointType.Local) return -1;
        return await DiskSpaceHelper.GetLocalSourceSizeAsync(job);
    }

    // ── File browser ─────────────────────────────────────────────

    public Task<BrowseResult> BrowseLocal(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    var drives = DriveInfo.GetDrives()
                        .Where(d => d.IsReady)
                        .Select(d => new BrowseEntry(d.Name, d.RootDirectory.FullName, true))
                        .ToArray();
                    return Task.FromResult(new BrowseResult("", null, drives, null));
                }
                path = "/";
            }

            if (OperatingSystem.IsWindows() && GetUncServerName(path) is string serverName)
            {
                var shares = ListUncShares(serverName);
                return Task.FromResult(new BrowseResult(path, null, shares,
                    shares.Length == 0 ? "No accessible shares found on " + serverName : null));
            }

            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? path;

            var di = new DirectoryInfo(path);
            var entries = di.EnumerateFileSystemInfos()
                .OrderBy(e => e is DirectoryInfo ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new BrowseEntry(e.Name, e.FullName, e is DirectoryInfo))
                .ToArray();

            return Task.FromResult(new BrowseResult(di.FullName, di.Parent?.FullName, entries, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new BrowseResult(path, null, [], ex.Message));
        }
    }

    public Task<BrowseResult> BrowseSsh(EndpointDto endpoint, string path)
    {
        return Task.Run(() =>
        {
            try
            {
                var s = endpoint.ToSettings();
                using var sftp = SshConnectionFactory.CreateSftp(s);
                sftp.ConnectVerified();

                var effectivePath = string.IsNullOrWhiteSpace(path) ? "/" : path;
                var trimmed = effectivePath.TrimEnd('/');
                var parent  = effectivePath == "/" ? null
                    : trimmed.LastIndexOf('/') is int i && i > 0 ? trimmed[..i] : "/";

                var entries = sftp.ListDirectory(effectivePath)
                    .Where(e => e.Name != "." && e.Name != "..")
                    .OrderBy(e => e.IsDirectory ? 0 : 1)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new BrowseEntry(e.Name, e.FullName, e.IsDirectory))
                    .ToArray();

                sftp.Disconnect();
                return new BrowseResult(effectivePath, parent, entries, null);
            }
            catch (Exception ex)
            {
                return new BrowseResult(path, null, [], ex.Message);
            }
        });
    }

    public Task<SshTestResult> TestSshConnection(EndpointDto endpoint)
    {
        return Task.Run(() =>
        {
            try
            {
                var s = endpoint.ToSettings();
                using var sftp = SshConnectionFactory.CreateSftp(s);
                sftp.ConnectVerified();
                sftp.Disconnect();
                return new SshTestResult(true, null);
            }
            catch (Exception ex)
            {
                return new SshTestResult(false, ex.Message);
            }
        });
    }

    private static string? GetUncServerName(string path)
    {
        if (path.Length < 3 || !path.StartsWith(@"\\")) return null;
        var rest = path.Substring(2).TrimEnd('\\');
        return rest.Length > 0 && !rest.Contains('\\') ? rest : null;
    }

    [SupportedOSPlatform("windows")]
    private static BrowseEntry[] ListUncShares(string serverName)
    {
        try
        {
            int resume = 0;
            int code = NativeMethods.NetShareEnum(@"\\" + serverName, 1, out IntPtr buf, -1,
                out int read, out int _, ref resume);
            if (code != 0 || buf == IntPtr.Zero) return [];
            try
            {
                int size = Marshal.SizeOf<NativeMethods.SHARE_INFO_1>();
                var list = new List<BrowseEntry>();
                for (int i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.SHARE_INFO_1>(IntPtr.Add(buf, i * size));
                    if ((info.shi1_type & 3u) == 0 && info.shi1_netname is string name)
                        list.Add(new BrowseEntry(name, @"\\" + serverName + @"\" + name, true));
                }
                return [.. list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
            }
            finally { NativeMethods.NetApiBufferFree(buf); }
        }
        catch { return []; }
    }
}

// ── DTOs sent from the browser ────────────────────────────────────

public class CopyJobDto
{
    public EndpointDto Source { get; set; } = new();
    public EndpointDto Destination { get; set; } = new();
    public string Mode { get; set; } = "Folder";
    public List<string> SourceFiles { get; set; } = new();
    public string OperationMode { get; set; } = "Copy";
    public bool VerifyFiles { get; set; } = true;
    public bool OverwriteExisting { get; set; } = true;
    public bool Recursive { get; set; } = true;
    public int MaxThreads { get; set; } = 4;
    public bool SetPermissions { get; set; } = true;
    public string PermissionMode { get; set; } = "644";

    public CopyJob ToCopyJob() => new()
    {
        Source        = Source.ToSettings(),
        Destination   = Destination.ToSettings(),
        Mode          = Mode switch
        {
            "File"          => CopyMode.File,
            "MultipleFiles" => CopyMode.MultipleFiles,
            _               => CopyMode.Folder,
        },
        SourceFiles       = SourceFiles,
        OperationMode     = OperationMode == "Move" ? FileMover.OperationMode.Move : FileMover.OperationMode.Copy,
        VerifyFiles       = VerifyFiles,
        OverwriteExisting = OverwriteExisting,
        Recursive         = Recursive,
        MaxThreads        = MaxThreads,
        SetPermissions    = SetPermissions,
        PermissionMode    = PermissionMode,
    };
}

public record BrowseEntry(string Name, string FullPath, bool IsDirectory);
public record BrowseResult(string CurrentPath, string? ParentPath, BrowseEntry[] Entries, string? Error);
public record SshTestResult(bool Success, string? Error);

public class EndpointDto
{
    public string Type { get; set; } = "Local";
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseKeyFile { get; set; }
    public string KeyFilePath { get; set; } = "";

    public EndpointSettings ToSettings() => new()
    {
        Type        = Type == "SSH" ? EndpointType.SSH : EndpointType.Local,
        Path        = Path,
        Host        = Host,
        Port        = Port > 0 ? Port : 22,
        Username    = Username,
        Password    = Password,
        UseKeyFile  = UseKeyFile,
        KeyFilePath = KeyFilePath,
    };
}

[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    internal static extern int NetShareEnum(string lpServerName, int dwLevel,
        out IntPtr lpBufPtr, int dwPrefMaxLen,
        out int entriesRead, out int totalEntries, ref int hResume);

    [DllImport("Netapi32.dll")]
    internal static extern int NetApiBufferFree(IntPtr lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_remark;
    }
}
