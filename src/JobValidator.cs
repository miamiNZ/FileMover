using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FileMover;

public static partial class PermissionParser
{
    /// <summary>
    /// Parses a 3-digit octal mode (optionally with a leading 0, e.g. 644 or 0755).
    /// setuid/setgid/sticky bits are rejected so a copy can never create privileged files.
    /// </summary>
    public static bool TryParse(string? raw, out short mode)
    {
        mode = 0;
        if (raw is null) return false;
        var trimmed = raw.Trim();
        if (!OctalMode().IsMatch(trimmed)) return false;
        mode = System.Convert.ToInt16(trimmed, 8);
        return true;
    }

    /// <summary>
    /// SSH.NET's <c>ChangePermissions(path, short)</c> reads the <em>decimal digits</em> of its
    /// argument as octal (it expects 644, not 0644 = 420). Converts permission bits to that form.
    /// </summary>
    public static short ToSshNetMode(short bits) =>
        short.Parse(System.Convert.ToString(bits & 0x1FF, 8), System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex("^0?[0-7]{3}$")]
    private static partial Regex OctalMode();
}

public static class JobValidator
{
    public const int MinThreads = 1;
    public const int MaxThreads = 16;

    /// <summary>Returns human-readable problems with <paramref name="job"/>; empty when valid.</summary>
    public static List<string> Validate(CopyJob job)
    {
        var errors = new List<string>();

        if (job.Source.Type == EndpointType.SSH && job.Destination.Type == EndpointType.SSH)
            errors.Add("SSH-to-SSH transfers are not supported.");

        if (job.MaxThreads is < MinThreads or > MaxThreads)
            errors.Add($"Concurrent transfers must be between {MinThreads} and {MaxThreads}.");

        if (job.Mode == CopyMode.MultipleFiles)
        {
            if (job.SourceFiles.Count == 0) errors.Add("No source files selected.");
        }
        else if (string.IsNullOrWhiteSpace(job.Source.Path))
        {
            errors.Add("Source path is required.");
        }

        if (string.IsNullOrWhiteSpace(job.Destination.Path))
            errors.Add("Destination path is required.");

        ValidateEndpoint(job.Source, "Source", errors);
        ValidateEndpoint(job.Destination, "Destination", errors);

        if (job.SetPermissions && job.Destination.Type == EndpointType.SSH
            && !PermissionParser.TryParse(job.PermissionMode, out _))
            errors.Add($"'{job.PermissionMode}' is not a valid permission mode (use 3 octal digits, e.g. 644, 664, 755).");

        return errors;
    }

    private static void ValidateEndpoint(EndpointSettings e, string name, List<string> errors)
    {
        if (e.Type != EndpointType.SSH) return;
        if (string.IsNullOrWhiteSpace(e.Host))     errors.Add($"{name} SSH host is required.");
        if (string.IsNullOrWhiteSpace(e.Username)) errors.Add($"{name} SSH username is required.");
        if (e.Port is < 1 or > 65535)              errors.Add($"{name} SSH port must be between 1 and 65535.");
    }
}
