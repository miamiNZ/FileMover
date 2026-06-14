using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace FileMover;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LogEntry> _log = new();
    private readonly ObservableCollection<string> _srcFiles = new();
    private CancellationTokenSource? _cts;
    private readonly CopyEngine _engine = new();
    private bool _isDarkTheme = true;
    private DateTime _opStartTime;
    private bool _srcSshAuth;
    private bool _dstSshAuth;
    private System.Windows.Threading.DispatcherTimer? _dstFreeSpaceTimer;
    private long _dstFreeSpaceBytes = -1;

    public MainWindow()
    {
        InitializeComponent();
        lstLog.ItemsSource      = _log;
        lstSrcFiles.ItemsSource = _srcFiles;
        _engine.LogMessage      += OnLogMessage;
        _engine.ProgressChanged += OnProgressChanged;
        txtDstPath.TextChanged  += (_, _) => ScheduleDstFreeSpaceRefresh();
    }

    // ── Maximize fix — prevent overlap with taskbar ──────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ((HwndSource)PresentationSource.FromVisual(this)).AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0024) // WM_GETMINMAXINFO
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var wa  = SystemParameters.WorkArea;
            mmi.ptMaxPosition.x = (int)wa.Left;
            mmi.ptMaxPosition.y = (int)wa.Top;
            mmi.ptMaxSize.x     = (int)wa.Width;
            mmi.ptMaxSize.y     = (int)wa.Height;
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ── SSH auth state ────────────────────────────────────────────

    private void ResetSrcSshAuth()
    {
        _srcSshAuth = false;
        if (txtSrcConnectStatus is not null)
            txtSrcConnectStatus.Text = string.Empty;
    }

    private void ResetDstSshAuth()
    {
        _dstSshAuth = false;
        if (txtDstConnectStatus is not null)
            txtDstConnectStatus.Text = string.Empty;
        _dstFreeSpaceBytes = -1;
        if (txtDstFreeSpace is not null)
        {
            txtDstFreeSpace.Text = "—";
            txtDstFreeSpace.Foreground = (Brush)Resources["SubtextBrush"];
        }
    }

    private void SrcCred_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ResetSrcSshAuth();
    private void SrcPwd_Changed(object sender, RoutedEventArgs e) => ResetSrcSshAuth();
    private void DstCred_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ResetDstSshAuth();
    private void DstPwd_Changed(object sender, RoutedEventArgs e) => ResetDstSshAuth();

    private static async Task<(bool ok, string? error)> TestSshAsync(EndpointSettings s)
    {
        try
        {
            await Task.Run(() =>
            {
                using var sftp = s.UseKeyFile && !string.IsNullOrWhiteSpace(s.KeyFilePath)
                    ? new Renci.SshNet.SftpClient(s.Host, s.Port, s.Username, new Renci.SshNet.PrivateKeyFile(s.KeyFilePath))
                    : new Renci.SshNet.SftpClient(s.Host, s.Port, s.Username, s.Password);
                sftp.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
                sftp.Connect();
                sftp.Disconnect();
            });
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async void BtnSrcConnect_Click(object sender, RoutedEventArgs e)
    {
        var ep = BuildSrcEndpoint();
        if (string.IsNullOrWhiteSpace(ep.Host) || string.IsNullOrWhiteSpace(ep.Username))
        {
            MessageBox.Show("Enter Host and Username before connecting.", "Fields Required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        btnSrcConnect.IsEnabled = false;
        txtSrcConnectStatus.Foreground = (Brush)Resources["SubtextBrush"];
        txtSrcConnectStatus.Text = "Connecting…";
        var (ok, error) = await TestSshAsync(ep);
        _srcSshAuth = ok;
        txtSrcConnectStatus.Text = ok ? "✓ Connected" : "✗ " + error;
        txtSrcConnectStatus.Foreground = ok
            ? (Brush)Resources["SuccessBrush"]
            : (Brush)Resources["ErrorBrush"];
        btnSrcConnect.IsEnabled = true;
    }

    private async void BtnDstConnect_Click(object sender, RoutedEventArgs e)
    {
        var ep = BuildDstEndpoint();
        if (string.IsNullOrWhiteSpace(ep.Host) || string.IsNullOrWhiteSpace(ep.Username))
        {
            MessageBox.Show("Enter Host and Username before connecting.", "Fields Required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        btnDstConnect.IsEnabled = false;
        txtDstConnectStatus.Foreground = (Brush)Resources["SubtextBrush"];
        txtDstConnectStatus.Text = "Connecting…";
        var (ok, error) = await TestSshAsync(ep);
        _dstSshAuth = ok;
        txtDstConnectStatus.Text = ok ? "✓ Connected" : "✗ " + error;
        txtDstConnectStatus.Foreground = ok
            ? (Brush)Resources["SuccessBrush"]
            : (Brush)Resources["ErrorBrush"];
        btnDstConnect.IsEnabled = true;
        if (ok) ScheduleDstFreeSpaceRefresh();
    }

    // ── Endpoint type toggles ────────────────────────────────────

    private void SrcType_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlSrcSSH is null) return;
        pnlSrcSSH.Visibility = rbSrcSSH.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ResetSrcSshAuth();
    }

    // ── Source mode toggle (single path ↔ multiple files list) ───

    private void SrcMode_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlSrcSinglePath is null || pnlSrcMultiFiles is null) return;
        bool multi = rbModeMulti?.IsChecked == true;
        pnlSrcSinglePath.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        pnlSrcMultiFiles.Visibility = multi ? Visibility.Visible   : Visibility.Collapsed;
    }

    // ── Multiple-files list management ───────────────────────────

    private void BtnSrcAddFiles_Click(object sender, RoutedEventArgs e)
    {
        if (rbSrcSSH.IsChecked == true)
        {
            if (!_srcSshAuth)
            {
                MessageBox.Show("Click Connect in SSH settings first.", "SSH Not Connected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var browser = new SftpBrowserWindow(BuildSrcEndpoint()) { Owner = this };
            if (browser.ShowDialog() == true && browser.SelectedPath is not null)
            {
                var path = browser.SelectedPath;
                if (!_srcFiles.Contains(path)) _srcFiles.Add(path);
            }
        }
        else
        {
            var dlg = new OpenFileDialog { Title = "Select files to add", Multiselect = true };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FileNames)
                    if (!_srcFiles.Contains(f)) _srcFiles.Add(f);
        }
    }

    private void BtnSrcRemoveFiles_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in lstSrcFiles.SelectedItems.Cast<string>().ToList())
            _srcFiles.Remove(item);
    }

    private void BtnSrcClearFiles_Click(object sender, RoutedEventArgs e) => _srcFiles.Clear();

    private void DstType_Changed(object sender, RoutedEventArgs e)
    {
        if (pnlDstSSH is null) return;
        pnlDstSSH.Visibility = rbDstSSH.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ResetDstSshAuth();
        ScheduleDstFreeSpaceRefresh();
    }

    // ── Auth type toggles ────────────────────────────────────────

    private void SrcAuth_Changed(object sender, RoutedEventArgs e)
    {
        if (pbSrcPwd is null) return;
        bool key = rbSrcKey.IsChecked == true;
        pbSrcPwd.IsEnabled  = !key;
        txtSrcKey.IsEnabled = key;
        btnSrcKey.IsEnabled = key;
        ResetSrcSshAuth();
    }

    private void DstAuth_Changed(object sender, RoutedEventArgs e)
    {
        if (pbDstPwd is null) return;
        bool key = rbDstKey.IsChecked == true;
        pbDstPwd.IsEnabled  = !key;
        txtDstKey.IsEnabled = key;
        btnDstKey.IsEnabled = key;
        ResetDstSshAuth();
    }

    // ── Browse buttons ───────────────────────────────────────────

    private void BtnSrcBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (rbSrcSSH.IsChecked == true)
        {
            if (!_srcSshAuth)
            {
                MessageBox.Show("Click Connect in SSH settings first.", "SSH Not Connected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            BrowseSftpPath(BuildSrcEndpoint(), txtSrcPath);
            return;
        }

        if (rbModeFile.IsChecked == true)
        {
            var dlg = new OpenFileDialog { Title = "Select source file" };
            if (dlg.ShowDialog() == true) txtSrcPath.Text = dlg.FileName;
        }
        else
        {
            var path = BrowseLocalFolder("Select source folder");
            if (path is not null) txtSrcPath.Text = path;
        }
    }

    private void BtnDstBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (rbDstSSH.IsChecked == true)
        {
            if (!_dstSshAuth)
            {
                MessageBox.Show("Click Connect in SSH settings first.", "SSH Not Connected",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            BrowseSftpPath(BuildDstEndpoint(), txtDstPath);
            return;
        }

        var folder = BrowseLocalFolder("Select destination folder");
        if (folder is not null) txtDstPath.Text = folder;
    }

    private void BtnSrcKey_Click(object sender, RoutedEventArgs e)
    {
        var p = BrowseKeyFile();
        if (p is not null) txtSrcKey.Text = p;
    }

    private void BtnDstKey_Click(object sender, RoutedEventArgs e)
    {
        var p = BrowseKeyFile();
        if (p is not null) txtDstKey.Text = p;
    }

    private void BrowseSftpPath(EndpointSettings settings, System.Windows.Controls.TextBox pathBox)
    {
        var browser = new SftpBrowserWindow(settings) { Owner = this };
        if (browser.ShowDialog() == true && browser.SelectedPath is not null)
            pathBox.Text = browser.SelectedPath;
    }

    // ── Local folder/file pickers ────────────────────────────────

    private static string? BrowseLocalFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private static string? BrowseKeyFile()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Select SSH private key",
            Filter = "Key files (*.pem;*.ppk;*.key;*)|*.pem;*.ppk;*.key;*|All files (*.*)|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // ── Thread slider ────────────────────────────────────────────

    private void SldThreads_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (txtThreadCount is null) return;
        txtThreadCount.Text = ((int)sldThreads.Value).ToString();
    }

    // ── Destination free space ───────────────────────────────────

    private void ScheduleDstFreeSpaceRefresh()
    {
        _dstFreeSpaceTimer?.Stop();
        if (_dstFreeSpaceTimer is null)
        {
            _dstFreeSpaceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _dstFreeSpaceTimer.Tick += async (_, _) =>
            {
                _dstFreeSpaceTimer!.Stop();
                await RefreshDestFreeSpaceAsync();
            };
        }
        _dstFreeSpaceTimer.Start();
    }

    private async Task RefreshDestFreeSpaceAsync()
    {
        if (txtDstFreeSpace is null) return;
        var dst = BuildDstEndpoint();
        if (string.IsNullOrWhiteSpace(dst.Path))
        {
            _dstFreeSpaceBytes = -1;
            txtDstFreeSpace.Text = "—";
            txtDstFreeSpace.Foreground = (Brush)Resources["SubtextBrush"];
            return;
        }
        if (dst.Type == EndpointType.SSH && !_dstSshAuth)
        {
            _dstFreeSpaceBytes = -1;
            txtDstFreeSpace.Text = "—";
            txtDstFreeSpace.Foreground = (Brush)Resources["SubtextBrush"];
            return;
        }

        txtDstFreeSpace.Text = "Checking…";
        txtDstFreeSpace.Foreground = (Brush)Resources["SubtextBrush"];

        long bytes = dst.Type == EndpointType.SSH
            ? await DiskSpaceHelper.GetSshFreeSpaceAsync(dst, dst.Path)
            : DiskSpaceHelper.GetLocalFreeSpace(dst.Path);

        _dstFreeSpaceBytes = bytes;
        if (bytes < 0)
        {
            txtDstFreeSpace.Text = "Unknown";
            txtDstFreeSpace.Foreground = (Brush)Resources["SubtextBrush"];
        }
        else
        {
            txtDstFreeSpace.Text = ProgressSnapshot.FormatBytes(bytes) + " available";
            txtDstFreeSpace.Foreground = (Brush)Resources["SuccessBrush"];
        }
    }

    // ── Start / Cancel ───────────────────────────────────────────

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs(out var job, out var error))
        {
            MessageBox.Show(error, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CopySessionData? session = null;
        var existing = CopySessionManager.TryLoad(job!);
        if (existing is not null)
        {
            var msg = $"A previous session was found:\n" +
                      $"  {existing.CompletedCount} of {existing.TotalCount} file(s) completed " +
                      $"({ProgressSnapshot.FormatBytes(existing.CompletedBytes)})\n\n" +
                      $"Resume where you left off?";
            var result = MessageBox.Show(msg, "Resume Session",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                session = existing;
            else
                CopySessionManager.Delete(job!);
        }

        if (_dstFreeSpaceBytes > 0 && job!.Source.Type == EndpointType.Local)
        {
            long srcBytes = await DiskSpaceHelper.GetLocalSourceSizeAsync(job);
            if (srcBytes > 0 && srcBytes > _dstFreeSpaceBytes)
            {
                var warn = $"Source size ({ProgressSnapshot.FormatBytes(srcBytes)}) exceeds " +
                           $"destination free space ({ProgressSnapshot.FormatBytes(_dstFreeSpaceBytes)}).\n\nProceed anyway?";
                if (MessageBox.Show(warn, "Insufficient Disk Space",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                    return;
            }
        }

        SetCopyingState(true);
        _opStartTime = DateTime.Now;
        _log.Clear();
        ResetProgressUI();

        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => _engine.ExecuteAsync(job!, _cts.Token, session), _cts.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            SetCopyingState(false);
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        txtStatus.Text = "Cancelling…";
    }

    // ── Validation ───────────────────────────────────────────────

    private bool ValidateInputs(out CopyJob? job, out string? error)
    {
        job   = null;
        error = null;

        bool isMulti = rbModeMulti.IsChecked == true;

        var src = BuildSrcEndpoint();
        var dst = BuildDstEndpoint();

        if (!isMulti && string.IsNullOrWhiteSpace(src.Path)) { error = "Source path is required."; return false; }
        if (isMulti  && _srcFiles.Count == 0) { error = "No source files selected.\nUse 'Add...' to add files."; return false; }
        if (string.IsNullOrWhiteSpace(dst.Path)) { error = "Destination path is required."; return false; }

        if (src.Type == EndpointType.SSH)
        {
            if (string.IsNullOrWhiteSpace(src.Host))     { error = "Source SSH host is required.";     return false; }
            if (string.IsNullOrWhiteSpace(src.Username)) { error = "Source SSH username is required."; return false; }
        }
        if (dst.Type == EndpointType.SSH)
        {
            if (string.IsNullOrWhiteSpace(dst.Host))     { error = "Destination SSH host is required.";     return false; }
            if (string.IsNullOrWhiteSpace(dst.Username)) { error = "Destination SSH username is required."; return false; }
        }

        if (src.Type == EndpointType.Local)
        {
            if (rbModeFile.IsChecked == true)
            {
                if (!File.Exists(src.Path)) { error = $"Source file not found:\n{src.Path}"; return false; }
            }
            else if (isMulti)
            {
                foreach (var f in _srcFiles)
                    if (!File.Exists(f)) { error = $"Source file not found:\n{f}"; return false; }
            }
            else
            {
                if (!Directory.Exists(src.Path)) { error = $"Source folder not found:\n{src.Path}"; return false; }
            }
        }

        var permMode = txtPermMode.Text.Trim();
        if (chkSetPerms.IsChecked == true && dst.Type == EndpointType.SSH)
        {
            try { Convert.ToInt16(permMode, 8); }
            catch { error = $"'{permMode}' is not a valid octal permission mode (e.g. 644, 664, 755)."; return false; }
        }

        job = new CopyJob
        {
            Source            = src,
            Destination       = dst,
            Mode              = rbModeFile.IsChecked == true ? CopyMode.File
                              : isMulti               ? CopyMode.MultipleFiles
                              : CopyMode.Folder,
            SourceFiles       = isMulti ? _srcFiles.ToList() : new(),
            OperationMode     = rbOpMove.IsChecked   == true ? OperationMode.Move : OperationMode.Copy,
            VerifyFiles       = chkVerify.IsChecked    == true,
            OverwriteExisting = chkOverwrite.IsChecked == true,
            Recursive         = chkRecursive.IsChecked == true,
            MaxThreads        = (int)sldThreads.Value,
            SetPermissions    = chkSetPerms.IsChecked  == true,
            PermissionMode    = permMode,
        };
        return true;
    }

    // ── Endpoint builders ────────────────────────────────────────

    private EndpointSettings BuildSrcEndpoint() => BuildEndpoint(
        rbSrcSSH.IsChecked == true,
        rbModeMulti?.IsChecked == true ? string.Empty : txtSrcPath.Text.Trim(),
        txtSrcHost.Text.Trim(), txtSrcPort.Text.Trim(),
        txtSrcUser.Text.Trim(), pbSrcPwd.Password,
        rbSrcKey.IsChecked == true, txtSrcKey.Text.Trim());

    private EndpointSettings BuildDstEndpoint() => BuildEndpoint(
        rbDstSSH.IsChecked == true,
        txtDstPath.Text.Trim(),
        txtDstHost.Text.Trim(), txtDstPort.Text.Trim(),
        txtDstUser.Text.Trim(), pbDstPwd.Password,
        rbDstKey.IsChecked == true, txtDstKey.Text.Trim());

    private static EndpointSettings BuildEndpoint(
        bool isSsh, string path,
        string host, string portText, string user, string pwd,
        bool useKey, string keyFile)
    {
        int.TryParse(portText, out int port);
        if (port <= 0) port = 22;
        return new EndpointSettings
        {
            Type        = isSsh ? EndpointType.SSH : EndpointType.Local,
            Path        = path,
            Host        = host,
            Port        = port,
            Username    = user,
            Password    = pwd,
            UseKeyFile  = useKey,
            KeyFilePath = keyFile,
        };
    }

    // ── UI state helpers ─────────────────────────────────────────

    private void SetCopyingState(bool copying)
    {
        btnStart.IsEnabled  = !copying;
        btnCancel.IsEnabled = copying;
        txtStatus.Text      = copying ? "Copying…" : "Ready";
    }

    private void ResetProgressUI()
    {
        pbOverall.Value     = 0;
        pbCurrent.Value     = 0;
        txtOverallPct.Text  = "0%";
        txtCurrentPct.Text  = "0%";
        txtCurrentFile.Text = "—";
        txtSpeed.Text       = "—";
        txtETA.Text         = "—";
        txtFiles.Text       = "—";
        txtBytes.Text       = "—";
        txtTime.Text        = "—";
    }

    // ── Engine callbacks ─────────────────────────────────────────

    private void OnLogMessage(object? sender, LogEntry entry)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _log.Add(entry);
            lstLog.ScrollIntoView(entry);
        });
    }

    private void OnProgressChanged(object? sender, ProgressSnapshot snap)
    {
        Dispatcher.InvokeAsync(() => ApplySnapshot(snap));
    }

    private void ApplySnapshot(ProgressSnapshot s)
    {
        pbOverall.Value     = s.OverallPercent;
        txtOverallPct.Text  = $"{s.OverallPercent:F1}%";
        pbCurrent.Value     = s.CurrentFilePercent;
        txtCurrentPct.Text  = $"{s.CurrentFilePercent:F1}%";
        txtCurrentFile.Text = string.IsNullOrEmpty(s.CurrentFileName) ? "—" : s.CurrentFileName;
        txtSpeed.Text       = s.SpeedBytesPerSecond > 0 ? s.SpeedText : "—";
        txtETA.Text         = s.ETA.HasValue ? s.ETAText : "—";
        txtFiles.Text       = s.TotalFiles  > 0 ? s.FilesText : "—";
        txtBytes.Text       = s.TotalBytes  > 0 ? s.BytesText : "—";

        if (_opStartTime != default)
        {
            var elapsed = DateTime.Now - _opStartTime;
            txtTime.Text = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }
    }

    // ── Custom title bar ─────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (btnMaximize is not null)
        {
            bool maximized = WindowState == WindowState.Maximized;
            btnMaximize.Content = maximized ? "⧉" : "□";
            btnMaximize.ToolTip = maximized ? "Restore" : "Maximize";
        }
    }

    // ── Theme toggle ─────────────────────────────────────────────

    private void BtnTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        ApplyTheme(_isDarkTheme);
        btnTheme.Content = _isDarkTheme ? "Light Mode" : "Dark Mode";
    }

    private void ApplyTheme(bool dark)
    {
        if (dark)
        {
            SetBrush("BgBrush",         "#1A1A1A");
            SetBrush("SurfaceBrush",    "#252525");
            SetBrush("CardBrush",       "#111111");
            SetBrush("BorderBrush",     "#3D3D3D");
            SetBrush("TextBrush",       "#E8E8E8");
            SetBrush("SubtextBrush",    "#888888");
            SetBrush("SuccessBrush",    "#5DB85D");
            SetBrush("WarningBrush",    "#E8A838");
            SetBrush("ErrorBrush",      "#E05050");
            SetBrush("AltBrush",        "#7DBCCC");
            SetBrush("HeaderBrush",     "#303030");
            SetBrush("TitleBarBrush",   "#1C1C1C");
            SetBrush("PanelBrush",      "#181818");
            SetBrush("PanelBorderBrush","#2E2E2E");
            SetBrush("BtnBgBrush",      "#2E2E2E");
            SetBrush("BtnHoverBrush",   "#3A3A3A");
            SetBrush("BtnPressedBrush", "#474747");
            SetBrush("ChkCheckedBrush", "#0E2340");
        }
        else
        {
            SetBrush("BgBrush",         "#F2F2F2");
            SetBrush("SurfaceBrush",    "#E8E8E8");
            SetBrush("CardBrush",       "#FFFFFF");
            SetBrush("BorderBrush",     "#C0C0C0");
            SetBrush("TextBrush",       "#1C1C1C");
            SetBrush("SubtextBrush",    "#606060");
            SetBrush("SuccessBrush",    "#2D7D32");
            SetBrush("WarningBrush",    "#C45000");
            SetBrush("ErrorBrush",      "#B71C1C");
            SetBrush("AltBrush",        "#0277BD");
            SetBrush("HeaderBrush",     "#D5D5D5");
            SetBrush("TitleBarBrush",   "#DCDCDC");
            SetBrush("PanelBrush",      "#EEEEEE");
            SetBrush("PanelBorderBrush","#C0C0C0");
            SetBrush("BtnBgBrush",      "#DEDEDE");
            SetBrush("BtnHoverBrush",   "#D0D0D0");
            SetBrush("BtnPressedBrush", "#C2C2C2");
            SetBrush("ChkCheckedBrush", "#D0E4F5");
        }
        Background = (Brush)Resources["BgBrush"];
    }

    private void SetBrush(string key, string hex)
    {
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    // ── Misc ─────────────────────────────────────────────────────

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();
}
