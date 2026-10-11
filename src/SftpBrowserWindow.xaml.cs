using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Renci.SshNet;

namespace FileMover;

public partial class SftpBrowserWindow : Window
{
    private readonly EndpointSettings _settings;
    private SftpClient? _sftp;
    private string _currentPath;
    private readonly Stack<string> _backStack = new();
    private bool _navigating;

    public string? SelectedPath { get; private set; }

    public SftpBrowserWindow(EndpointSettings settings)
    {
        InitializeComponent();
        _settings    = settings;
        _currentPath = string.IsNullOrWhiteSpace(settings.Path) ? $"/home/{settings.Username}" : settings.Path;
        SelectedPath = _currentPath;
        Title        = $"Browse  {settings.Username}@{settings.Host}";
    }

    // ── Lifecycle ────────────────────────────────────────────────

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("Connecting to " + _settings.Host + "…", StatusKind.Busy);
        try
        {
            await Task.Run(() =>
            {
                _sftp = CreateSftpClient(_settings);
                _sftp.ConnectVerified();
            });
            SetStatus("Connected  —  " + _settings.Username + "@" + _settings.Host, StatusKind.Ok);
            await NavigateAsync(_currentPath, addToHistory: false);
        }
        catch (Exception ex)
        {
            SetStatus("Connection failed: " + ex.Message, StatusKind.Error);
            btnSelect.IsEnabled = false;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        try { _sftp?.Disconnect(); _sftp?.Dispose(); } catch { }
    }

    // ── Navigation ───────────────────────────────────────────────

    private async Task NavigateAsync(string path, bool addToHistory = true)
    {
        if (_navigating || _sftp?.IsConnected != true) return;
        _navigating = true;

        ShowLoading(true);
        lstFolders.IsEnabled = false;
        btnUp.IsEnabled = false;
        btnBack.IsEnabled = false;
        btnSelect.IsEnabled = false;

        try
        {
            if (path != "/" && path.EndsWith('/')) path = path.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(path)) path = "/";

            var folders = await Task.Run(() =>
                _sftp!.ListDirectory(path)
                      .Where(e => e.IsDirectory && e.Name != "." && e.Name != "..")
                      .OrderBy(e => e.Name.ToLowerInvariant())
                      .Select(e => new FolderItem(e.Name, $"{path.TrimEnd('/')}/{e.Name}"))
                      .ToList());

            if (addToHistory)
                _backStack.Push(_currentPath);

            _currentPath       = path;
            SelectedPath       = path;
            txtNavPath.Text    = path;
            txtSelectedPath.Text = path;
            lstFolders.ItemsSource = folders;
            txtItemCount.Text  = folders.Count == 0 ? "No subfolders" : $"{folders.Count} folder(s)";

            SetStatus($"{folders.Count} subfolder(s) in  {path}", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message, StatusKind.Error);
            txtItemCount.Text = "Error loading directory";
            txtNavPath.Text = _currentPath;
        }
        finally
        {
            _navigating = false;
            ShowLoading(false);
            lstFolders.IsEnabled = true;
            btnUp.IsEnabled    = _currentPath != "/";
            btnBack.IsEnabled  = _backStack.Count > 0;
            btnSelect.IsEnabled = true;
        }
    }

    // ── List events ──────────────────────────────────────────────

    private void LstFolders_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (lstFolders.SelectedItem is FolderItem item)
        {
            SelectedPath         = item.FullPath;
            txtSelectedPath.Text = item.FullPath;
        }
        else
        {
            SelectedPath         = _currentPath;
            txtSelectedPath.Text = _currentPath;
        }
    }

    private async void LstFolders_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (lstFolders.SelectedItem is FolderItem item)
            await NavigateAsync(item.FullPath);
    }

    // ── Button handlers ──────────────────────────────────────────

    private async void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count > 0)
            await NavigateAsync(_backStack.Pop(), addToHistory: false);
    }

    private async void BtnUp_Click(object sender, RoutedEventArgs e)
    {
        await NavigateAsync(ParentOf(_currentPath));
    }

    private async void BtnGo_Click(object sender, RoutedEventArgs e)
    {
        var p = txtNavPath.Text.Trim();
        if (!string.IsNullOrEmpty(p))
            await NavigateAsync(p);
    }

    private async void TxtNavPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await NavigateAsync(txtNavPath.Text.Trim());
        }
    }

    private void BtnSelect_Click(object sender, RoutedEventArgs e)
    {
        SelectedPath = SelectedPath ?? _currentPath;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string ParentOf(string path)
    {
        path = path.TrimEnd('/');
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private enum StatusKind { Ok, Busy, Error }

    private void SetStatus(string msg, StatusKind kind = StatusKind.Ok)
    {
        Dispatcher.InvokeAsync(() =>
        {
            txtBrowserStatus.Text = msg;
            txtStatusIcon.Text    = kind switch
            {
                StatusKind.Busy  => "⏳",
                StatusKind.Error => "✗",
                _                => "✓",
            };
            txtBrowserStatus.Foreground = kind switch
            {
                StatusKind.Error => (System.Windows.Media.Brush)FindResource("ErrorBrush"),
                StatusKind.Busy  => (System.Windows.Media.Brush)FindResource("WarningBrush"),
                _                => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            };
        });
    }

    private void ShowLoading(bool show) =>
        Dispatcher.InvokeAsync(() => pnlLoading.Visibility = show ? Visibility.Visible : Visibility.Collapsed);

    private static SftpClient CreateSftpClient(EndpointSettings s) => SshConnectionFactory.CreateSftp(s);
}

internal sealed record FolderItem(string Name, string FullPath)
{
    public string Icon => "📁";
}
