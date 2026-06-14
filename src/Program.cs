using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FileMover;
using Microsoft.Extensions.FileProviders;

if (args.Length == 0 || (args[0] != "-d" && args[0] != "-w"))
{
    Console.WriteLine("FileMover — file copy / move utility");
    Console.WriteLine("Usage: FileMover -d | -w [--no-browser] [--urls <url>]");
    Console.WriteLine("  -d   Desktop (WPF) UI");
    Console.WriteLine("  -w   Web UI  (browser at http://localhost:5050)");
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
var webArgs = args.Skip(1).ToArray();
var builder = WebApplication.CreateBuilder(webArgs);
builder.Services.AddSignalR();

var webApp = builder.Build();
webApp.MapGet("/", () => Results.Redirect("/index.html"));
webApp.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new EmbeddedFileProvider(
        typeof(Program).Assembly, "FileMover.wwwroot"),
    RequestPath = ""
});
webApp.MapHub<CopyHub>("/copyhub");

var url = builder.Configuration["urls"] ?? "http://localhost:5050";
Console.WriteLine($"FileMover Web running at {url}");

if (!webArgs.Contains("--no-browser"))
{
    if (OperatingSystem.IsWindows())
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    else if (OperatingSystem.IsMacOS())
        System.Diagnostics.Process.Start("open", url);
    else if (OperatingSystem.IsLinux())
        System.Diagnostics.Process.Start("xdg-open", url);
}

webApp.Run(url);
return 0;

// ── Console window helpers (Windows only) ────────────────────────
[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")]   static extern bool   ShowWindow(IntPtr hWnd, int nCmdShow);
