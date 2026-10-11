using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FileMover;
using Microsoft.Extensions.FileProviders;

if (args.Length == 0 || (args[0] != "-d" && args[0] != "-w"))
{
    Console.WriteLine("FileMover — file copy / move utility");
    Console.WriteLine("Usage: FileMover -d | -w [--no-browser] [--urls <url>] [--token <value>]");
    Console.WriteLine("  -d   Desktop (WPF) UI");
    Console.WriteLine("  -w   Web UI  (browser at http://localhost:5050)");
    Console.WriteLine("       --token  fixed access token (min 16 chars); default is random per launch");
    return 1;
}

if (args[0] == "-d")
{
    // Hide the console window so only the WPF window is visible on Windows.
    if (OperatingSystem.IsWindows())
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0);
    }

    int exitCode = 0;
    var uiThread = new Thread(() =>
    {
        var wpfApp = new System.Windows.Application();
        exitCode = wpfApp.Run(new MainWindow());
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    return exitCode;
}

// Web mode (-w)
WebOptions webOptions;
try { webOptions = WebOptions.Parse(args.Skip(1).ToArray()); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }

var accessToken = webOptions.Token ?? WebSecurity.GenerateToken();
var builder = WebApplication.CreateBuilder(webOptions.HostArgs);
// Framework request logs include the query string, which carries the access token.
builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
builder.Services.AddSignalR();

var webApp = builder.Build();
webApp.Use(WebSecurity.Middleware(accessToken));
webApp.MapGet("/", (HttpRequest req) => Results.Redirect("/index.html" + req.QueryString));
webApp.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new EmbeddedFileProvider(
        typeof(Program).Assembly, "FileMover.wwwroot"),
    RequestPath = ""
});
webApp.MapHub<CopyHub>(WebSecurity.HubPath);

var url = builder.Configuration["urls"] ?? "http://localhost:5050";
var openUrl = $"{url.Split(';')[0].Replace("*", "localhost").Replace("+", "localhost").TrimEnd('/')}/?token={accessToken}";
Console.WriteLine($"FileMover Web running at {url}");
Console.WriteLine($"Open: {openUrl}");
Console.WriteLine("The token in this URL is required to use the UI — keep it private.");

if (webOptions.OpenBrowser)
{
    if (OperatingSystem.IsWindows())
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(openUrl) { UseShellExecute = true });
    else if (OperatingSystem.IsMacOS())
        System.Diagnostics.Process.Start("open", openUrl);
    else if (OperatingSystem.IsLinux())
        System.Diagnostics.Process.Start("xdg-open", openUrl);
}

webApp.Run(url);
return 0;

// ── Console window helpers (Windows only) ────────────────────────
[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")]   static extern bool   ShowWindow(IntPtr hWnd, int nCmdShow);
