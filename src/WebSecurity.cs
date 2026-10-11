using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace FileMover;

/// <summary>Command-line options for web mode (<c>-w</c>).</summary>
public sealed record WebOptions(bool OpenBrowser, string? Token, string[] HostArgs)
{
    /// <summary>
    /// Extracts FileMover's own switches (<c>--no-browser</c>, <c>--token &lt;value&gt;</c>);
    /// everything else is passed through to the ASP.NET Core host (e.g. <c>--urls</c>).
    /// </summary>
    public static WebOptions Parse(IReadOnlyList<string> args)
    {
        var openBrowser = true;
        string? token   = null;
        var hostArgs    = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--no-browser") { openBrowser = false; continue; }
            if (a == "--token")
            {
                if (i + 1 >= args.Count) throw new ArgumentException("--token requires a value.");
                token = args[++i];
                continue;
            }
            if (a.StartsWith("--token=", StringComparison.Ordinal)) { token = a["--token=".Length..]; continue; }
            hostArgs.Add(a);
        }

        if (token is not null && token.Length < 16)
            throw new ArgumentException("--token must be at least 16 characters.");

        return new WebOptions(openBrowser, token, hostArgs.ToArray());
    }
}

/// <summary>
/// Protects the SignalR hub. The hub can read, copy and delete local files, so it must not be
/// reachable by other machines or by web pages in other origins (cross-site WebSocket hijacking,
/// DNS rebinding). Every hub request must carry the per-launch access token and, when the
/// browser sends an Origin header, that origin must match the request host.
/// </summary>
public static class WebSecurity
{
    public const string HubPath = "/copyhub";

    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static bool TokenMatches(string? supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>A missing Origin (non-browser client) is allowed; otherwise it must equal the request host.</summary>
    public static bool OriginMatchesHost(string? origin, HostString host)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (!host.HasValue) return false;

        var originHost = new HostString(uri.Host, uri.Port);
        var requestPort = host.Port ?? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80);
        return string.Equals(originHost.Host, host.Host, StringComparison.OrdinalIgnoreCase)
            && originHost.Port == requestPort;
    }

    public static string? ExtractToken(HttpRequest request)
    {
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        // Browsers cannot set headers on WebSocket requests; SignalR sends the token as a query value.
        return request.Query["access_token"].ToString();
    }

    public static Func<HttpContext, Func<Task>, Task> Middleware(string token) => async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"]        = "DENY";
        headers["Referrer-Policy"]        = "no-referrer";
        headers["Cache-Control"]          = "no-store";
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        if (ctx.Request.Path.StartsWithSegments(HubPath))
        {
            if (!OriginMatchesHost(ctx.Request.Headers.Origin.ToString(), ctx.Request.Host))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!TokenMatches(ExtractToken(ctx.Request), token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next();
    };
}
