using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace FileMover;

public sealed class CopyEngine
{
    private const int BufferSize = 4 * 1024 * 1024; // 4 MB

    public event EventHandler<ProgressSnapshot>? ProgressChanged;
    public event EventHandler<LogEntry>? LogMessage;

    // ── Mutable progress state (all access under _progressLock) ──
    private readonly object _progressLock = new();
    private long   _copiedBytes;
    private int    _completedFiles;
    private int    _failedFiles;
    private string _currentFileName  = string.Empty;
    private long   _currentFileCopied;
    private long   _currentFileSize;
    private int    _totalFiles;
    private long   _totalBytes;

    private readonly ConcurrentQueue<(DateTime Time, long Bytes)> _speedSamples = new();

    // ── Session state ─────────────────────────────────────────────
    private readonly object _sessionLock = new();
    private CopySessionData? _session;

    // ─────────────────────────────────────────────────────────────

    public async Task<bool> ExecuteAsync(CopyJob job, CancellationToken ct, CopySessionData? session = null)
    {
        Reset();
        _session = session;
        var startedAt = DateTime.UtcNow;

        try
        {
            List<FileEntry> files;

            if (session is not null)
            {
                Log($"Resuming session {session.SessionId} — {session.CompletedCount}/{session.TotalCount} file(s) already done.");
                files = BuildResumeFileList(session);

                lock (_progressLock)
                {
                    _totalFiles     = session.TotalCount;
                    _totalBytes     = session.TotalBytes;
                    _copiedBytes    = session.CompletedBytes;
                    _completedFiles = session.CompletedCount;
                }

                await DetectResumeOffsetsAsync(job, files, ct);
            }
            else
            {
                Log("Scanning source…");
                files = await EnumerateFilesAsync(job, ct);

                if (files.Count == 0)
                {
                    Log("No files found to copy.", LogLevel.Warning);
                    return false;
                }

                lock (_progressLock)
                {
                    _totalFiles = files.Count;
                    _totalBytes = files.Sum(f => f.Size);
                }

                _session = new CopySessionData
                {
                    Files = files.Select(f => new SessionFileEntry
                    {
                        SourcePath      = f.SourcePath,
                        DestinationPath = f.DestinationPath,
                        Size            = f.Size,
                        Status          = "Pending",
                    }).ToList(),
                };
                CopySessionManager.Save(job, _session);

                Log($"Found {files.Count} file(s) — {ProgressSnapshot.FormatBytes(_totalBytes)} total.");
            }

            // Credit partial bytes already on disk toward the overall progress
            var partialBytes = files.Sum(f => f.ResumeOffset);
            if (partialBytes > 0)
                lock (_progressLock) _copiedBytes += partialBytes;

            using var timer = new System.Timers.Timer(400);
            timer.Elapsed += (_, _) => FireProgress();
            timer.Start();

            await CopyFilesAsync(job, files, ct);

            timer.Stop();
            FireProgress();

            var snap = BuildSnapshot();
            var elapsed = DateTime.UtcNow - startedAt;
            var elapsedText = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            var verb = job.OperationMode == OperationMode.Move ? "moved" : "copied";

            if (snap.FailedFiles == 0 && job.OperationMode == OperationMode.Move)
            {
                Log("Deleting source files…");
                await DeleteSourcesAsync(job, files, ct);
            }

            Log(snap.FailedFiles == 0
                ? $"Done — {snap.CompletedFiles} file(s) {verb} successfully in {elapsedText}."
                : $"Done — {snap.CompletedFiles} succeeded, {snap.FailedFiles} failed. Elapsed: {elapsedText}",
                snap.FailedFiles == 0 ? LogLevel.Success : LogLevel.Warning);

            if (snap.FailedFiles == 0)
                CopySessionManager.Delete(job);

            return snap.FailedFiles == 0;
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled — progress saved; you can resume.", LogLevel.Warning);
            return false;
        }
        catch (Exception ex)
        {
            Log($"Fatal: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    // ── Move: delete sources after successful copy ───────────────

    private async Task DeleteSourcesAsync(CopyJob job, List<FileEntry> files, CancellationToken ct)
    {
        if (job.Source.Type == EndpointType.Local)
        {
            var dirs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Delete(f.SourcePath);
                    var dir = Path.GetDirectoryName(f.SourcePath);
                    if (dir is not null) dirs.Add(dir);
                }
                catch (Exception ex) { Log($"Delete failed: {f.SourcePath} — {ex.Message}", LogLevel.Warning); }
            }
            // Remove empty dirs bottom-up
            foreach (var dir in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
        else
        {
            await Task.Run(() =>
            {
                using var sftp = CreateSftp(job.Source);
                sftp.Connect();
                try
                {
                    var dirs = new HashSet<string>();
                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            sftp.DeleteFile(f.SourcePath);
                            var dir = SftpDir(f.SourcePath);
                            if (dir != "/") dirs.Add(dir);
                        }
                        catch (Exception ex) { Log($"Delete failed: {f.SourcePath} — {ex.Message}", LogLevel.Warning); }
                    }
                    foreach (var dir in dirs.OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (!sftp.ListDirectory(dir).Any(e => e.Name is not "." and not ".."))
                                sftp.DeleteDirectory(dir);
                        }
                        catch { }
                    }
                }
                finally { sftp.Disconnect(); }
            }, ct);
        }
    }

    // ── Resume helpers ───────────────────────────────────────────

    private static List<FileEntry> BuildResumeFileList(CopySessionData session) =>
        session.Files
            .Where(e => e.Status != "Completed")
            .Select(e => new FileEntry
            {
                SourcePath      = e.SourcePath,
                DestinationPath = e.DestinationPath,
                Size            = e.Size,
                Status          = FileStatus.Pending,
            })
            .ToList();

    private async Task DetectResumeOffsetsAsync(CopyJob job, List<FileEntry> files, CancellationToken ct)
    {
        if (job.Destination.Type == EndpointType.Local)
        {
            foreach (var f in files)
                f.ResumeOffset = GetLocalResumeOffset(f.DestinationPath, f.Size);
        }
        else
        {
            await Task.Run(() =>
            {
                using var sftp = CreateSftp(job.Destination);
                sftp.Connect();
                try
                {
                    foreach (var f in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        f.ResumeOffset = GetSftpResumeOffset(sftp, f.DestinationPath, f.Size);
                    }
                }
                finally { sftp.Disconnect(); }
            }, ct);
        }
    }

    private static long GetLocalResumeOffset(string localPath, long expectedSize)
    {
        if (!File.Exists(localPath)) return 0;
        var len = new FileInfo(localPath).Length;
        return len < expectedSize ? len : 0;
    }

    private static long GetSftpResumeOffset(SftpClient sftp, string remotePath, long expectedSize)
    {
        try
        {
            if (!sftp.Exists(remotePath)) return 0;
            var size = sftp.GetAttributes(remotePath).Size;
            return size < expectedSize ? size : 0;
        }
        catch { return 0; }
    }

    private void UpdateSession(CopyJob job, FileEntry file, bool completed)
    {
        lock (_sessionLock)
        {
            if (_session is null) return;
            var entry = _session.Files.FirstOrDefault(e => e.SourcePath == file.SourcePath);
            if (entry is not null)
                entry.Status = completed ? "Completed" : "Failed";
            CopySessionManager.Save(job, _session);
        }
    }

    // ── File enumeration ─────────────────────────────────────────

    private async Task<List<FileEntry>> EnumerateFilesAsync(CopyJob job, CancellationToken ct)
    {
        var files = new List<FileEntry>();

        if (job.Source.Type == EndpointType.Local)
        {
            if (job.Mode == CopyMode.File)
            {
                var fi = new FileInfo(job.Source.Path);
                if (!fi.Exists) throw new FileNotFoundException($"Source not found: {job.Source.Path}");
                files.Add(new FileEntry
                {
                    SourcePath      = fi.FullName,
                    DestinationPath = BuildDestPath(job.Destination, fi.Name),
                    Size            = fi.Length,
                });
            }
            else if (job.Mode == CopyMode.MultipleFiles)
            {
                foreach (var path in job.SourceFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var fi = new FileInfo(path);
                    if (!fi.Exists) throw new FileNotFoundException($"Source file not found: {path}");
                    files.Add(new FileEntry
                    {
                        SourcePath      = fi.FullName,
                        DestinationPath = BuildDestPath(job.Destination, fi.Name),
                        Size            = fi.Length,
                    });
                }
            }
            else
            {
                if (!Directory.Exists(job.Source.Path))
                    throw new DirectoryNotFoundException($"Source directory not found: {job.Source.Path}");

                var option = job.Recursive
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;

                foreach (var path in Directory.EnumerateFiles(job.Source.Path, "*", option))
                {
                    ct.ThrowIfCancellationRequested();
                    var fi  = new FileInfo(path);
                    var rel = Path.GetRelativePath(job.Source.Path, path);
                    files.Add(new FileEntry
                    {
                        SourcePath      = fi.FullName,
                        DestinationPath = BuildDestRelPath(job.Destination, rel),
                        Size            = fi.Length,
                    });
                }
            }
        }
        else // SSH source
        {
            using var sftp = CreateSftp(job.Source);
            sftp.Connect();

            if (job.Mode == CopyMode.File)
            {
                var attr     = sftp.GetAttributes(job.Source.Path);
                var fileName = SftpFileName(job.Source.Path);
                files.Add(new FileEntry
                {
                    SourcePath      = job.Source.Path,
                    DestinationPath = BuildDestPath(job.Destination, fileName),
                    Size            = attr.Size,
                });
            }
            else if (job.Mode == CopyMode.MultipleFiles)
            {
                await Task.Run(() =>
                {
                    foreach (var path in job.SourceFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        var attr = sftp.GetAttributes(path);
                        files.Add(new FileEntry
                        {
                            SourcePath      = path,
                            DestinationPath = BuildDestPath(job.Destination, SftpFileName(path)),
                            Size            = attr.Size,
                        });
                    }
                }, ct);
            }
            else
            {
                await Task.Run(() => WalkSftp(sftp, job.Source.Path, job.Source.Path,
                    job.Destination, files, job.Recursive, ct), ct);
            }

            sftp.Disconnect();
        }

        return files;
    }

    private static void WalkSftp(SftpClient sftp, string basePath, string dir,
        EndpointSettings dest, List<FileEntry> files, bool recursive, CancellationToken ct)
    {
        foreach (var entry in sftp.ListDirectory(dir))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..") continue;

            if (entry.IsDirectory && recursive)
                WalkSftp(sftp, basePath, entry.FullName, dest, files, recursive, ct);
            else if (entry.IsRegularFile)
            {
                var rel = entry.FullName[basePath.Length..].TrimStart('/');
                files.Add(new FileEntry
                {
                    SourcePath      = entry.FullName,
                    DestinationPath = BuildDestRelPath(dest, rel),
                    Size            = entry.Length,
                });
            }
        }
    }

    // ── Parallel copy orchestration ──────────────────────────────

    private async Task CopyFilesAsync(CopyJob job, List<FileEntry> files, CancellationToken ct)
    {
        var sem     = new SemaphoreSlim(job.MaxThreads, job.MaxThreads);
        var srcPool = job.Source.Type      == EndpointType.SSH ? new SftpPool(job.Source,      job.MaxThreads) : null;
        var dstPool = job.Destination.Type == EndpointType.SSH ? new SftpPool(job.Destination, job.MaxThreads) : null;

        try
        {
            var tasks = files.Select(file => Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await ProcessFileAsync(job, file, srcPool, dstPool, ct);
                }
                finally { sem.Release(); }
            }, ct));

            await Task.WhenAll(tasks);
        }
        finally
        {
            srcPool?.Dispose();
            dstPool?.Dispose();
        }
    }

    private async Task ProcessFileAsync(CopyJob job, FileEntry file,
        SftpPool? srcPool, SftpPool? dstPool, CancellationToken ct)
    {
        var name = FriendlyName(file.SourcePath, job.Source.Type);
        var resumeNote = file.ResumeOffset > 0
            ? $" (resuming from {ProgressSnapshot.FormatBytes(file.ResumeOffset)})"
            : string.Empty;
        Log($"Copying  {name}  ({ProgressSnapshot.FormatBytes(file.Size)}){resumeNote}");

        SetCurrentFile(name, file.Size, file.ResumeOffset);

        // Skip if destination already exists and overwrite is disabled (not resuming)
        if (!job.OverwriteExisting && file.ResumeOffset == 0)
        {
            bool destExists = false;
            if (job.Destination.Type == EndpointType.Local)
                destExists = File.Exists(file.DestinationPath);
            else if (dstPool != null)
            {
                var sftp = await dstPool.AcquireAsync(ct);
                try { destExists = sftp.Exists(file.DestinationPath); }
                finally { dstPool.Release(sftp); }
            }

            if (destExists)
            {
                Log($"Skip  {name}  (exists, overwrite off)");
                file.Status = FileStatus.Completed;
                Interlocked.Increment(ref _completedFiles);
                UpdateSession(job, file, true);
                return;
            }
        }

        file.Status = FileStatus.Copying;
        var ok = await CopySingleAsync(job, file, srcPool, dstPool, ct);

        if (ok && job.VerifyFiles)
        {
            file.Status = FileStatus.Verifying;
            Log($"Verifying  {name}…");
            ok = await VerifyAsync(job, file, srcPool, dstPool, ct);
            if (!ok) file.ErrorMessage = "Checksum mismatch after copy";
        }

        if (ok)
        {
            file.Status = FileStatus.Completed;
            Log($"✓  {name}  {(job.VerifyFiles ? "(verified)" : "")}", LogLevel.Success);
            Interlocked.Increment(ref _completedFiles);
        }
        else
        {
            file.Status = FileStatus.Failed;
            Log($"✗  {name}  — {file.ErrorMessage ?? "unknown error"}", LogLevel.Error);
            Interlocked.Increment(ref _failedFiles);
        }

        UpdateSession(job, file, ok);
    }

    // ── Single-file copy (all source/dest combinations) ──────────

    private async Task<bool> CopySingleAsync(CopyJob job, FileEntry file,
        SftpPool? srcPool, SftpPool? dstPool, CancellationToken ct)
    {
        try
        {
            var offset = file.ResumeOffset;

            void OnChunk(long bytes)
            {
                Interlocked.Add(ref _copiedBytes, bytes);
                lock (_progressLock) _currentFileCopied += bytes;
                _speedSamples.Enqueue((DateTime.UtcNow, Interlocked.Read(ref _copiedBytes)));
                while (_speedSamples.Count > 25) _speedSamples.TryDequeue(out _);
            }

            short fileMode = ParseOctalMode(job.SetPermissions ? job.PermissionMode : "");
            short dirMode  = fileMode > 0 ? (short)0b111_101_101 : (short)0; // 755

            if (job.Source.Type == EndpointType.Local && job.Destination.Type == EndpointType.Local)
            {
                EnsureDir(Path.GetDirectoryName(file.DestinationPath)!);
                await CopyLocalLocalAsync(file.SourcePath, file.DestinationPath, offset, OnChunk, ct);
            }
            else if (job.Source.Type == EndpointType.Local && job.Destination.Type == EndpointType.SSH)
            {
                var sftp = await dstPool!.AcquireAsync(ct);
                try
                {
                    EnsureSftpDir(sftp, SftpDir(file.DestinationPath), dirMode);
                    await CopyLocalSftpAsync(file.SourcePath, file.DestinationPath, sftp, offset, OnChunk, ct);
                    if (fileMode > 0)
                        try { sftp.ChangePermissions(file.DestinationPath, fileMode); } catch { }
                }
                finally { dstPool.Release(sftp); }
            }
            else if (job.Source.Type == EndpointType.SSH && job.Destination.Type == EndpointType.Local)
            {
                EnsureDir(Path.GetDirectoryName(file.DestinationPath)!);
                var sftp = await srcPool!.AcquireAsync(ct);
                try
                {
                    await CopySftpLocalAsync(file.SourcePath, file.DestinationPath, sftp, offset, OnChunk, ct);
                }
                finally { srcPool.Release(sftp); }
            }
            else
            {
                throw new NotSupportedException("SSH-to-SSH is not supported.");
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            file.ErrorMessage = ex.Message;
            return false;
        }
    }

    // ── Transfer implementations ─────────────────────────────────

    private static async Task CopyLocalLocalAsync(string src, string dst,
        long resumeOffset, Action<long> onChunk, CancellationToken ct)
    {
        using var r = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var w = new FileStream(dst,
            resumeOffset > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);

        if (resumeOffset > 0)
            r.Seek(resumeOffset, SeekOrigin.Begin);

        var buf = new byte[BufferSize];
        int n;
        while ((n = await r.ReadAsync(buf, ct)) > 0)
        {
            await w.WriteAsync(buf.AsMemory(0, n), ct);
            onChunk(n);
        }
    }

    private static async Task CopyLocalSftpAsync(string localPath, string remotePath,
        SftpClient sftp, long resumeOffset, Action<long> onChunk, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            using var localStream = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, FileOptions.SequentialScan);

            if (resumeOffset > 0)
            {
                localStream.Seek(resumeOffset, SeekOrigin.Begin);
                using var remoteStream = sftp.Open(remotePath, FileMode.Append, FileAccess.Write);
                var buf = new byte[BufferSize];
                int n;
                while ((n = localStream.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    remoteStream.Write(buf, 0, n);
                    onChunk(n);
                }
            }
            else
            {
                ulong last = 0;
                sftp.UploadFile(localStream, remotePath, true, uploaded =>
                {
                    ct.ThrowIfCancellationRequested();
                    var delta = (long)(uploaded - last);
                    if (delta > 0) { onChunk(delta); last = uploaded; }
                });
            }
        }, ct);
    }

    private static async Task CopySftpLocalAsync(string remotePath, string localPath,
        SftpClient sftp, long resumeOffset, Action<long> onChunk, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (resumeOffset > 0)
            {
                using var remoteStream = sftp.Open(remotePath, FileMode.Open, FileAccess.Read);
                remoteStream.Seek(resumeOffset, SeekOrigin.Begin);
                using var localStream = new FileStream(localPath, FileMode.Append, FileAccess.Write,
                    FileShare.None, BufferSize);
                var buf = new byte[BufferSize];
                int n;
                while ((n = remoteStream.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    localStream.Write(buf, 0, n);
                    onChunk(n);
                }
            }
            else
            {
                using var localStream = new FileStream(localPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, BufferSize);
                ulong last = 0;
                sftp.DownloadFile(remotePath, localStream, downloaded =>
                {
                    ct.ThrowIfCancellationRequested();
                    var delta = (long)(downloaded - last);
                    if (delta > 0) { onChunk(delta); last = downloaded; }
                });
            }
        }, ct);
    }

    // ── Verification (MD5) ───────────────────────────────────────

    private async Task<bool> VerifyAsync(CopyJob job, FileEntry file,
        SftpPool? srcPool, SftpPool? dstPool, CancellationToken ct)
    {
        try
        {
            string srcHash = job.Source.Type == EndpointType.Local
                ? await Md5LocalAsync(file.SourcePath, ct)
                : await Md5RemoteAsync(file.SourcePath, job.Source, ct);

            string dstHash = job.Destination.Type == EndpointType.Local
                ? await Md5LocalAsync(file.DestinationPath, ct)
                : await Md5RemoteAsync(file.DestinationPath, job.Destination, ct);

            return string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log($"Verify error: {ex.Message}", LogLevel.Warning);
            return false;
        }
    }

    private static async Task<string> Md5LocalAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var hash = await MD5.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> Md5RemoteAsync(string remotePath, EndpointSettings settings, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var ssh = CreateSsh(settings);
            ssh.Connect();
            try
            {
                var q      = ShellQuote(remotePath);
                var cmd    = ssh.RunCommand($"md5sum {q} 2>/dev/null || md5 -q {q} 2>/dev/null");
                var output = cmd.Result?.Trim() ?? string.Empty;
                return output.Contains("  ") ? output.Split("  ")[0].Trim() : output;
            }
            finally { ssh.Disconnect(); }
        }, ct);
    }

    private static string ShellQuote(string path) =>
        "'" + path.Replace("'", "'\\''") + "'";

    // ── Progress helpers ─────────────────────────────────────────

    private void FireProgress() => ProgressChanged?.Invoke(this, BuildSnapshot());

    private ProgressSnapshot BuildSnapshot()
    {
        double speed = 0;
        TimeSpan? eta = null;
        long copied, total;

        lock (_progressLock)
        {
            copied = _copiedBytes;
            total  = _totalBytes;

            var samples = _speedSamples.ToArray();
            if (samples.Length >= 2)
            {
                var (t0, b0) = samples[0];
                var (t1, b1) = samples[^1];
                var secs = (t1 - t0).TotalSeconds;
                if (secs > 0.01) speed = (b1 - b0) / secs;
            }

            if (speed > 0 && total > copied)
                eta = TimeSpan.FromSeconds((total - copied) / speed);

            return new ProgressSnapshot
            {
                TotalFiles          = _totalFiles,
                CompletedFiles      = _completedFiles,
                FailedFiles         = _failedFiles,
                TotalBytes          = total,
                CopiedBytes         = copied,
                SpeedBytesPerSecond = speed,
                ETA                 = eta,
                CurrentFileName     = _currentFileName,
                CurrentFilePercent  = _currentFileSize > 0
                    ? Math.Min(100.0, (double)_currentFileCopied / _currentFileSize * 100.0)
                    : 0,
                CurrentFileCopied   = _currentFileCopied,
                CurrentFileSize     = _currentFileSize,
            };
        }
    }

    private void SetCurrentFile(string name, long size, long resumeOffset = 0)
    {
        lock (_progressLock)
        {
            _currentFileName   = name;
            _currentFileSize   = size;
            _currentFileCopied = resumeOffset;
        }
    }

    private void Reset()
    {
        lock (_progressLock)
        {
            _copiedBytes       = 0;
            _completedFiles    = 0;
            _failedFiles       = 0;
            _currentFileName   = string.Empty;
            _currentFileCopied = 0;
            _currentFileSize   = 0;
            _totalFiles        = 0;
            _totalBytes        = 0;
        }
        while (_speedSamples.TryDequeue(out _)) { }
        _session = null;
    }

    // ── SFTP / SSH factory helpers ───────────────────────────────

    private static SftpClient CreateSftp(EndpointSettings s)
    {
        SftpClient c = s.UseKeyFile && !string.IsNullOrWhiteSpace(s.KeyFilePath)
            ? new SftpClient(s.Host, s.Port, s.Username, new PrivateKeyFile(s.KeyFilePath))
            : new SftpClient(s.Host, s.Port, s.Username, s.Password);
        c.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    private static SshClient CreateSsh(EndpointSettings s)
    {
        SshClient c = s.UseKeyFile && !string.IsNullOrWhiteSpace(s.KeyFilePath)
            ? new SshClient(s.Host, s.Port, s.Username, new PrivateKeyFile(s.KeyFilePath))
            : new SshClient(s.Host, s.Port, s.Username, s.Password);
        c.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    // ── Directory helpers ────────────────────────────────────────

    private static void EnsureDir(string path)
    {
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            Directory.CreateDirectory(path);
    }

    private static void EnsureSftpDir(SftpClient sftp, string path, short dirMode = 0)
    {
        if (string.IsNullOrEmpty(path)) return;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current  = "/";
        foreach (var seg in segments)
        {
            current = current == "/" ? $"/{seg}" : $"{current}/{seg}";
            try
            {
                if (!sftp.Exists(current))
                {
                    sftp.CreateDirectory(current);
                    if (dirMode > 0)
                        try { sftp.ChangePermissions(current, dirMode); } catch { }
                }
            }
            catch { }
        }
    }

    // ── Path helpers ─────────────────────────────────────────────

    private static string BuildDestPath(EndpointSettings dest, string fileName)
        => dest.Type == EndpointType.SSH
            ? $"{dest.Path.TrimEnd('/')}/{fileName}"
            : Path.Combine(dest.Path, fileName);

    private static string BuildDestRelPath(EndpointSettings dest, string relativePath)
        => dest.Type == EndpointType.SSH
            ? $"{dest.Path.TrimEnd('/')}/{relativePath.Replace('\\', '/')}"
            : Path.Combine(dest.Path, relativePath);

    private static string SftpDir(string path)
    {
        var i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : "/";
    }

    private static string SftpFileName(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;

    private static string FriendlyName(string path, EndpointType type) =>
        type == EndpointType.SSH ? SftpFileName(path) : Path.GetFileName(path);

    private static short ParseOctalMode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        try { return Convert.ToInt16(raw.Trim(), 8); }
        catch { return 0; }
    }

    // ── Logging ──────────────────────────────────────────────────

    private void Log(string msg, LogLevel level = LogLevel.Info) =>
        LogMessage?.Invoke(this, new LogEntry { Message = msg, Level = level, Timestamp = DateTime.Now });
}

// ── SFTP connection pool ─────────────────────────────────────────

internal sealed class SftpPool : IDisposable
{
    private readonly EndpointSettings _settings;
    private readonly Stack<SftpClient> _pool = new();
    private readonly SemaphoreSlim _sem;
    private readonly object _lock = new();
    private bool _disposed;

    public SftpPool(EndpointSettings settings, int maxSize)
    {
        _settings = settings;
        _sem      = new SemaphoreSlim(maxSize, maxSize);
    }

    public async Task<SftpClient> AcquireAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        lock (_lock)
        {
            while (_pool.Count > 0)
            {
                var c = _pool.Pop();
                if (c.IsConnected) return c;
                try { c.Dispose(); } catch { }
            }
        }
        return Connect();
    }

    public void Release(SftpClient client)
    {
        bool discard;
        lock (_lock)
        {
            discard = _disposed || !client.IsConnected;
            if (!discard) _pool.Push(client);
        }
        if (discard) { try { client.Disconnect(); client.Dispose(); } catch { } }
        _sem.Release();
    }

    private SftpClient Connect()
    {
        SftpClient c;
        if (_settings.UseKeyFile && !string.IsNullOrWhiteSpace(_settings.KeyFilePath))
            c = new SftpClient(_settings.Host, _settings.Port, _settings.Username,
                    new PrivateKeyFile(_settings.KeyFilePath));
        else
            c = new SftpClient(_settings.Host, _settings.Port,
                    _settings.Username, _settings.Password);
        c.Connect();
        return c;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            while (_pool.Count > 0)
            {
                try { var c = _pool.Pop(); c.Disconnect(); c.Dispose(); } catch { }
            }
        }
    }
}
