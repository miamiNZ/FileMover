using System;
using System.IO;
using System.Linq;

namespace FileMover;

/// <summary>
/// Destination-path construction for local and SFTP endpoints. Every relative path that
/// originates from a source listing (which may be a remote, untrusted server) is checked so
/// it can never escape the destination root.
/// </summary>
public static class PathHelpers
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>Destination path for a single file name placed directly in the destination root.</summary>
    public static string BuildDestPath(EndpointSettings dest, string fileName)
    {
        if (!IsSafeSegment(fileName))
            throw new InvalidDataException($"Unsafe file name rejected: '{fileName}'");

        return dest.Type == EndpointType.SSH
            ? $"{dest.Path.TrimEnd('/')}/{fileName}"
            : CombineWithin(dest.Path, fileName);
    }

    /// <summary>Destination path for a source-relative path (folder copies).</summary>
    public static string BuildDestRelPath(EndpointSettings dest, string relativePath)
    {
        var segments = relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => !IsSafeSegment(s)))
            throw new InvalidDataException($"Unsafe relative path rejected: '{relativePath}'");

        return dest.Type == EndpointType.SSH
            ? $"{dest.Path.TrimEnd('/')}/{string.Join('/', segments)}"
            : CombineWithin(dest.Path, Path.Combine(segments));
    }

    /// <summary>Combines <paramref name="root"/> and <paramref name="relative"/>, throwing if the result leaves root.</summary>
    public static string CombineWithin(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var full     = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var rootWithSep = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSep, comparison))
            throw new InvalidDataException($"Path '{relative}' escapes destination '{root}'");
        return full;
    }

    /// <summary>True when <paramref name="segment"/> is a single, non-traversing path component.</summary>
    public static bool IsSafeSegment(string? segment) =>
        !string.IsNullOrWhiteSpace(segment)
        && segment != "." && segment != ".."
        && segment.IndexOfAny(Separators) < 0
        && segment.IndexOf('\0') < 0
        && !(OperatingSystem.IsWindows() && segment.Contains(':'));

    public static string SftpDir(string path)
    {
        var i = path.LastIndexOf('/');
        return i > 0 ? path[..i] : "/";
    }

    public static string SftpFileName(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? path;

    public static string FriendlyName(string path, EndpointType type) =>
        type == EndpointType.SSH ? SftpFileName(path) : Path.GetFileName(path);

    /// <summary>POSIX single-quote escaping for use in remote shell commands.</summary>
    public static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
