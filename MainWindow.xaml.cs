using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPlayerServer.Models;
using NPlayerServer.Services;
using NPlayerServer.Storage;

namespace NPlayerServer;

public partial class MainWindow : Window
{
    private readonly LogBuffer _log = new();
    private readonly ObservableCollection<string> _logLines = new();
    private readonly ObservableCollection<ShareEntry> _shares = new();

    private readonly AppConfig _config;
    private WebDavServer? _webdav;
    private FtpServer? _ftp;
    private System.Windows.Forms.NotifyIcon? _tray;
    private DispatcherTimer? _timer;
    private DateTime _startedAt;
    private bool _reallyExit;
    private bool _loading;

    public MainWindow()
    {
        // XAML 里复选框初始为选中，InitializeComponent 期间就会触发变更事件，
        // 此时 _config 尚未赋值、其它控件也还没建好，先挂起保存逻辑。
        _loading = true;
        _config = ConfigStore.Load();

        InitializeComponent();

        _log.EntryAdded += OnLogAdded;

        LoadIntoUi();
        SetupTray();
        StartTimer();

        _log.Add("就绪，点击「启动服务」开始");

        if (Environment.GetCommandLineArgs().Contains("--start", StringComparer.OrdinalIgnoreCase))
        {
            Loaded += (_, _) => StartServer();
        }
    }

    private bool IsAnyRunning => _webdav?.IsRunning == true || _ftp?.IsRunning == true;

    private void LoadIntoUi()
    {
        _loading = true;
        try
        {
            WebDavCheck.IsChecked = _config.EnableWebDav;
            FtpCheck.IsChecked = _config.EnableFtp;

            PortBox.Text = _config.Port.ToString();
            FtpPortBox.Text = _config.FtpPort.ToString();
            PopulateBindings();

            _shares.Clear();
            foreach (var share in _config.Shares) _shares.Add(share.Clone());

            ShareList.ItemsSource = _shares;
            LogList.ItemsSource = _logLines;

            UserBox.Text = _config.Username;
            PassBox.Text = _config.Password;

            UpdateAnonBadge();
            UpdateStatusUi();
        }
        finally
        {
            _loading = false;
        }
    }

    private void PopulateBindings()
    {
        var items = new List<string> { "0.0.0.0" };
        items.AddRange(NetworkHelper.GetIPv4Addresses().Select(a => a.ToString()));
        BindBox.ItemsSource = items;
        BindBox.Text = _config.BindAddress;
    }

    private void PersistFromUi()
    {
        if (_loading) return;

        _config.EnableWebDav = WebDavCheck.IsChecked == true;
        _config.EnableFtp = FtpCheck.IsChecked == true;
        _config.Port = ParsePort();
        _config.FtpPort = ParseFtpPort();
        _config.BindAddress = GetBindAddress();
        _config.Shares = _shares
            .Select(s => new ShareEntry { Path = s.Path, Alias = (s.Alias ?? string.Empty).Trim() })
            .ToList();
        _config.Username = UserBox.Text.Trim();
        _config.Password = PassBox.Text;

        if (!ConfigStore.Save(_config))
            _log.Add("配置保存失败：程序所在目录不可写，换个目录放程序即可");
        UpdateAnonBadge();
        UpdateStatusUi();
    }

    private int ParsePort() =>
        int.TryParse(PortBox.Text.Trim(), out var port) && port is >= 1 and <= 65535 ? port : 8000;

    private int ParseFtpPort() =>
        int.TryParse(FtpPortBox.Text.Trim(), out var port) && port is >= 1 and <= 65535 ? port : 21;

    private string GetBindAddress()
    {
        var text = (BindBox.Text ?? string.Empty).Trim();
        if (text.Length == 0) return "0.0.0.0";
        var first = text.Split(' ', '(', '（')[0].Trim();
        return first.Length == 0 ? "0.0.0.0" : first;
    }

    private string CurrentHost() =>
        GetBindAddress() == "0.0.0.0" ? NetworkHelper.PrimaryAddress() : GetBindAddress();

    private string HttpUrl() => $"http://{CurrentHost()}:{ParsePort()}";

    private string FtpUrl() => $"ftp://{CurrentHost()}:{ParseFtpPort()}";

    private void UpdateAnonBadge() =>
        AnonBadge.Visibility = string.IsNullOrEmpty(PassBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStatusUi()
    {
        var running = IsAnyRunning;
        var wantWebDav = WebDavCheck.IsChecked == true;
        var wantFtp = FtpCheck.IsChecked == true;

        var webdavOn = _webdav?.IsRunning == true;
        var ftpOn = _ftp?.IsRunning == true;

        StatusDot.Fill = new SolidColorBrush(running ? Color.FromRgb(0x63, 0x99, 0x22) : Color.FromRgb(0xB4, 0xB2, 0xA9));
        StatusText.Text = running
            ? (webdavOn && ftpOn ? "WebDAV 与 FTP 运行中" : webdavOn ? "WebDAV 运行中" : "FTP 运行中")
            : "服务已停止";
        StatusText.Foreground = new SolidColorBrush(running ? Color.FromRgb(0x3B, 0x6D, 0x11) : Color.FromRgb(0x5F, 0x5E, 0x5A));
        StatusCard.Background = new SolidColorBrush(running ? Color.FromRgb(0xF2, 0xF8, 0xEC) : Color.FromRgb(0xF4, 0xF4, 0xF1));
        StatusCard.BorderBrush = new SolidColorBrush(running ? Color.FromRgb(0xDF, 0xEB, 0xCE) : Color.FromRgb(0xE8, 0xE6, 0xDF));

        ToggleButton.Content = running ? "停止服务" : "启动服务";
        ToggleButton.Style = (Style)FindResource(running ? "DangerButton" : "PrimaryButton");

        WebDavCheck.IsEnabled = !running;
        FtpCheck.IsEnabled = !running;
        PortBox.IsEnabled = !running && wantWebDav;
        FtpPortBox.IsEnabled = !running && wantFtp;
        BindBox.IsEnabled = !running;
        ShareList.IsEnabled = !running;

        HttpUrlText.Text = wantWebDav ? HttpUrl() : "WebDAV 已关闭";
        HttpUrlText.Foreground = new SolidColorBrush(wantWebDav ? Color.FromRgb(0x18, 0x5F, 0xA5) : Color.FromRgb(0xB4, 0xB2, 0xA9));
        FtpUrlText.Text = wantFtp ? FtpUrl() : "FTP 已关闭";
        FtpUrlText.Foreground = new SolidColorBrush(wantFtp ? Color.FromRgb(0x18, 0x5F, 0xA5) : Color.FromRgb(0xB4, 0xB2, 0xA9));
    }

    private void Protocol_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        PersistFromUi();
    }

    private void ToggleServer_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyRunning) StopServer();
        else StartServer();
    }

    private void StartServer()
    {
        if (!ValidateBeforeStart(out var error))
        {
            MessageBox.Show(this, error, "无法启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        PersistFromUi();

        var errors = new List<string>();

        if (_config.EnableWebDav)
        {
            var server = new WebDavServer(_config, _log);
            try
            {
                server.Start();
                _webdav = server;
            }
            catch (Exception ex)
            {
                server.Dispose();
                _webdav = null;
                errors.Add("WebDAV 启动失败：" + ex.Message);
            }
        }

        if (_config.EnableFtp)
        {
            var server = new FtpServer(_config, _log);
            try
            {
                server.Start();
                _ftp = server;
            }
            catch (Exception ex)
            {
                server.Dispose();
                _ftp = null;
                errors.Add("FTP 启动失败：" + ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                string.Join("\n\n", errors) + "\n\n最常见的原因是端口已被其他程序占用，换一个端口试试。",
                "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (IsAnyRunning) _startedAt = DateTime.Now;

        UpdateStatusUi();
        RefreshRuntimeStats();
    }

    private void StopServer()
    {
        _webdav?.Stop();
        _webdav?.Dispose();
        _webdav = null;

        _ftp?.Stop();
        _ftp?.Dispose();
        _ftp = null;

        UpdateStatusUi();
        RefreshRuntimeStats();
    }

    private bool ValidateBeforeStart(out string error)
    {
        error = string.Empty;

        var wantWebDav = WebDavCheck.IsChecked == true;
        var wantFtp = FtpCheck.IsChecked == true;

        if (!wantWebDav && !wantFtp)
        {
            error = "至少要开启一个协议（WebDAV 或 FTP）。";
            return false;
        }

        if (_shares.Count == 0)
        {
            error = "请先添加至少一个共享目录。";
            return false;
        }

        foreach (var share in _shares)
        {
            if (string.IsNullOrWhiteSpace(share.Path) || !Directory.Exists(share.Path))
            {
                error = "目录不存在：\n" + share.Path;
                return false;
            }

            var alias = (share.Alias ?? string.Empty).Trim();
            if (alias.Length == 0)
            {
                error = "目录的访问路径不能为空：\n" + share.Path;
                return false;
            }

            if (alias.Contains('/') || alias.Contains('\\'))
            {
                error = "访问路径不能包含斜杠：\n" + alias;
                return false;
            }
        }

        var duplicate = _shares
            .GroupBy(s => (s.Alias ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            error = "访问路径重复：" + duplicate.Key;
            return false;
        }

        if (wantWebDav && (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535))
        {
            error = "WebDAV 端口必须是 1 到 65535 之间的数字。";
            return false;
        }

        if (wantFtp && (!int.TryParse(FtpPortBox.Text.Trim(), out var ftpPort) || ftpPort is < 1 or > 65535))
        {
            error = "FTP 端口必须是 1 到 65535 之间的数字。";
            return false;
        }

        if (wantWebDav && wantFtp && ParsePort() == ParseFtpPort())
        {
            error = "WebDAV 和 FTP 不能使用同一个端口。";
            return false;
        }

        return true;
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshRuntimeStats();
        _timer.Start();
    }

    private void RefreshRuntimeStats()
    {
        if (!IsAnyRunning)
        {
            StatusSub.Text = "点击右侧按钮启动";
            StatLeft.Text = "当前连接 0  ·  已传输 0 B";
            return;
        }

        var elapsed = DateTime.Now - _startedAt;
        var parts = new List<string>();

        if (_webdav?.IsRunning == true) parts.Add($"WebDAV {_webdav.ActiveConnections} 连接");
        if (_ftp?.IsRunning == true) parts.Add($"FTP {_ftp.ActiveConnections} 连接");

        var totalBytes = (_webdav?.BytesSent ?? 0) + (_ftp?.BytesSent ?? 0);

        StatusSub.Text = $"{_shares.Count} 个目录  ·  已运行 " +
                         $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        StatLeft.Text = string.Join("  ·  ", parts) + $"  ·  已传输 {FormatSize(totalBytes)}";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double value = bytes / 1024.0;
        if (value < 1024) return value.ToString("0.0") + " KB";
        value /= 1024.0;
        if (value < 1024) return value.ToString("0.0") + " MB";
        value /= 1024.0;
        return value.ToString("0.00") + " GB";
    }

    private void AddShare_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要共享的目录",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var added = 0;
        foreach (var folder in dialog.FolderNames)
        {
            if (_shares.Any(s => string.Equals(s.Path, folder, StringComparison.OrdinalIgnoreCase))) continue;

            _shares.Add(new ShareEntry
            {
                Path = folder,
                Alias = MakeUniqueAlias(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))),
            });
            added++;
        }

        if (added > 0)
        {
            PersistFromUi();
            _log.Add($"已添加 {added} 个共享目录");
        }
    }

    private string MakeUniqueAlias(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Share";

        var alias = baseName;
        var index = 2;
        while (_shares.Any(s => string.Equals(s.Alias, alias, StringComparison.OrdinalIgnoreCase)))
        {
            alias = baseName + index;
            index++;
        }

        return alias;
    }

    private void RemoveShare_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShareEntry entry } || !_shares.Contains(entry)) return;

        _shares.Remove(entry);
        PersistFromUi();
        _log.Add("已移除共享目录 " + entry.Path);
    }

    private void CopyHttpUrl_Click(object sender, RoutedEventArgs e) => CopyUrl(HttpUrl(), "WebDAV");

    private void CopyFtpUrl_Click(object sender, RoutedEventArgs e) => CopyUrl(FtpUrl(), "FTP");

    private void CopyUrl(string url, string label)
    {
        try
        {
            Clipboard.SetText(url);
            _log.Add($"已复制 {label} 地址 {url}");
        }
        catch
        {
            MessageBox.Show(this, "复制失败，剪贴板可能被其他程序占用。", "提示");
        }
    }

    private void PortBox_LostFocus(object sender, RoutedEventArgs e) => PersistFromUi();

    private void BindBox_LostFocus(object sender, RoutedEventArgs e) => PersistFromUi();

    private void BindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BindBox.SelectedItem is string selected && selected.Length > 0) BindBox.Text = selected;
        PersistFromUi();
    }

    private void Auth_LostFocus(object sender, RoutedEventArgs e) => PersistFromUi();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _logLines.Clear();

    private void OnLogAdded(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logLines.Add(line);
            while (_logLines.Count > 1000) _logLines.RemoveAt(0);
            if (_logLines.Count > 0) LogList.ScrollIntoView(_logLines[^1]);
        }, DispatcherPriority.Background);
    }

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();

        menu.Items.Add("显示主界面", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("启动服务", null, (_, _) => Dispatcher.Invoke(() =>
        {
            if (!IsAnyRunning) StartServer();
        }));
        menu.Items.Add("停止服务", null, (_, _) => Dispatcher.Invoke(StopServer));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("打开共享目录", null, (_, _) => Dispatcher.Invoke(OpenFirstShare));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "nPlayer 视频服务",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            // 单文件发布时 Assembly.Location 为空，必须用 ProcessPath
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon is not null) return icon;
            }
        }
        catch
        {
            // 取不到就用系统图标
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OpenFirstShare()
    {
        var share = _shares.FirstOrDefault(s => Directory.Exists(s.Path));
        if (share is null) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{share.Path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器就算了
        }
    }

    private void ExitApplication()
    {
        _reallyExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit && _config.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(1500, "nPlayer 视频服务", "已最小化到托盘，服务继续运行",
                System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        _timer?.Stop();

        _webdav?.Stop();
        _webdav?.Dispose();
        _webdav = null;

        _ftp?.Stop();
        _ftp?.Dispose();
        _ftp = null;

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        base.OnClosing(e);
        System.Windows.Application.Current?.Shutdown();
    }
}
