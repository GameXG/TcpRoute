using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TcpRouteTray;

internal sealed class TrayIconHost : IDisposable
{
    private readonly TcpRouteController _controller;
    private readonly LogWindow _window;
    private readonly System.Windows.Application _application;
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _trayIcon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly ConfigurationChangeTracker _changes;
    private bool _busy;
    private bool _geoBusy;
    private DateTime _nextGeoAttempt;
    private readonly ConfigurationChangeTracker _readErrors = new("");

    public TrayIconHost(TcpRouteController controller, LogWindow window, System.Windows.Application application)
    {
        _controller = controller; _window = window; _application = application;
        _changes = new ConfigurationChangeTracker(controller.AppliedConfigurationSignature());
        _menu.Opening += (_, _) => RebuildMenu();
        _trayIcon = LoadIcon();
        _icon = new Forms.NotifyIcon { ContextMenuStrip = _menu, Icon = _trayIcon, Text = "TcpRoute2", Visible = true };
        _icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) _window.ShowOutput(); };
        _timer.Tick += async (_, _) => await PollConfigurationAsync();
        _timer.Start(); RebuildMenu();
    }

    public Task StartAsync() => RunAsync(() => _controller.StartAsync());

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var running = _controller.IsRunning;
        var mode = _controller.UseWintun ? "TUN / VPN (Wintun)" : T("Proxy", "代理", "代理");
        _icon.Text = "TcpRoute2 - " + (running ? mode : T("Stopped", "已停止", "已停止"));
        _menu.Items.Add(new Forms.ToolStripMenuItem(running ? $"{mode} · 127.0.0.1:{_controller.RunningPort}" : T("Stopped", "已停止", "已停止")) { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripMenuItem(_controller.LastMessage) { Enabled = false });
        Add(_menu.Items, T("Activity log", "活动日志", "活動日誌"), () => { _window.ShowOutput(); return Task.CompletedTask; });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add(_menu.Items, running ? T("Stop", "停止", "停止") : T("Start", "启动", "啟動"), async () => {
            if (_controller.IsRunning) await _controller.StopAsync(); else await _controller.StartAsync();
        });
        Add(_menu.Items, T("Restart", "重新启动", "重新啟動"), () => _controller.RestartAsync(), running);
        Add(_menu.Items, T("Connectivity check", "连通性检查", "連通性檢查"), async () => {
            var elapsed = await _controller.MeasureSocksConnectAsync(); _controller.RecordEvent($"SOCKS5 connect: {elapsed} ms");
        }, running);
        var network = Submenu(T("Network mode", "网络模式", "網路模式"));
        Choice(network, T("Proxy", "代理", "代理"), !_controller.UseWintun, () => _controller.SetUseWintun(false), !running);
        Choice(network, "TUN / VPN (Wintun)", _controller.UseWintun, () => _controller.SetUseWintun(true), !running);
        var interfaces = Submenu(T("Outbound interface", "出口网络接口", "出口網路介面"));
        Choice(interfaces, T("Automatic", "自动", "自動"), _controller.OutboundInterfaceId == "", () => _controller.SetOutboundInterface(""), !running);
        foreach (var adapter in WintunNetwork.AvailableInterfaces())
            Choice(interfaces, adapter.Name, adapter.Id == _controller.OutboundInterfaceId, () => _controller.SetOutboundInterface(adapter.Id), !running).ToolTipText = adapter.Id;
        if (_controller.OutboundInterfaceId != "" && !interfaces.DropDownItems.OfType<Forms.ToolStripMenuItem>().Any(item => item.Checked))
            interfaces.DropDownItems.Add(new Forms.ToolStripMenuItem(T("Selected interface unavailable", "已选接口不可用", "已選介面不可用")) { Enabled = false });

        var preferences = Submenu(T("Runtime settings", "运行设置", "執行設定"));
        Choice(preferences, T("System proxy", "系统代理", "系統代理"), _controller.SystemProxyEnabled, () => _controller.SetSystemProxyEnabled(!_controller.SystemProxyEnabled), !_controller.UseWintun);
        Choice(preferences, T("Allow LAN", "允许 LAN", "允許 LAN"), _controller.AllowLan, () => _controller.SetAllowLan(!_controller.AllowLan), !running && !_controller.UseWintun);
        Choice(preferences, "STUN", _controller.AllowStun, () => _controller.SetAllowStun(!_controller.AllowStun), !running);
        Choice(preferences, "QUIC", _controller.AllowQuic, () => _controller.SetAllowQuic(!_controller.AllowQuic), !running);
        Choice(preferences, "IPv6", _controller.EnableIpv6, () => _controller.SetEnableIpv6(!_controller.EnableIpv6), !running);
        preferences.DropDownItems.Add(new Forms.ToolStripMenuItem($"{_controller.Language} / {_controller.Appearance}") { Enabled = false });
        Add(preferences.DropDownItems, T("Edit client configuration...", "编辑客户端配置…", "編輯客戶端配置…"), () => EditFile(_controller.SettingsPath));

        var configurations = Submenu(T("Configuration", "配置", "配置"));
        foreach (var config in _controller.LoadConfigs())
        {
            var profile = new Forms.ToolStripMenuItem(config.Name) { Checked = config.Id == _controller.SelectedConfigId };
            Add(profile.DropDownItems, T("Use this configuration", "使用此配置", "使用此配置"), async () => {
                _controller.SelectConfig(config.Id); await _controller.ReloadConfigurationAsync();
            });
            Add(profile.DropDownItems, T("Edit file...", "编辑文件…", "編輯檔案…"), () => EditFile(config.Path));
            configurations.DropDownItems.Add(profile);
        }
        Add(configurations.DropDownItems, T("Open configuration folder", "打开配置目录", "開啟配置目錄"), () => { _controller.OpenConfigFolder(); return Task.CompletedTask; });
        Add(configurations.DropDownItems, T("Reload now", "立即重载", "立即重新載入"), () => _controller.ReloadConfigurationAsync());
        var geo = Submenu(T("Geo data", "Geo 数据", "Geo 資料"));
        var snapshot = _controller.GetGeoDataSnapshot();
        geo.DropDownItems.Add(new Forms.ToolStripMenuItem($"geosite: {snapshot.GeositeBytes:N0} B / geoip: {snapshot.GeoipBytes:N0} B") { Enabled = false });
        geo.DropDownItems.Add(new Forms.ToolStripMenuItem($"{snapshot.Interval} / {snapshot.LastSuccess?.ToLocalTime().ToString("g") ?? "-"}") { Enabled = false });
        Add(geo.DropDownItems, T("Update now", "立即更新", "立即更新"), async () => { await _controller.UpdateGeoDataAsync(); }, !_geoBusy);
        Add(_menu.Items, T("Export activity log...", "导出活动日志…", "匯出活動日誌…"), () => { _controller.OpenLogs(); return Task.CompletedTask; });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        Add(_menu.Items, T("Quit", "退出", "退出"), () => { _window.AllowClose(); _window.Close(); _application.Shutdown(); return Task.CompletedTask; });
    }

    private Forms.ToolStripMenuItem Submenu(string label)
    {
        var item = new Forms.ToolStripMenuItem(label); _menu.Items.Add(item); return item;
    }

    private Forms.ToolStripMenuItem Choice(Forms.ToolStripMenuItem parent, string label, bool selected, Action action, bool enabled)
    {
        var item = Add(parent.DropDownItems, label, () => { action(); return Task.CompletedTask; }, enabled);
        item.Checked = selected; return item;
    }

    private Forms.ToolStripMenuItem Add(Forms.ToolStripItemCollection items, string label, Func<Task> action, bool enabled = true)
    {
        var item = new Forms.ToolStripMenuItem(label) { Enabled = enabled && !_busy && !_controller.IsStarting };
        item.Click += async (_, _) => await RunAsync(action); items.Add(item); return item;
    }

    private static Task EditFile(string path)
    {
        var start = new ProcessStartInfo("notepad.exe") { UseShellExecute = true }; start.ArgumentList.Add(path);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Could not open the configuration file.");
        return Task.CompletedTask;
    }

    private string ReadSignature()
    {
        try { return _controller.ReadConfigurationSnapshot().Signature; }
        catch (Exception error) { _controller.RecordEvent(error.Message); return ""; }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        var success = false;
        try { await action(); success = true; }
        catch (Exception error) { ReportError(error); }
        finally { _busy = false; _changes.Accept(success ? _controller.AppliedConfigurationSignature() : ReadSignature()); RebuildMenu(); }
    }

    private async Task PollConfigurationAsync()
    {
        if (_busy || _controller.IsStarting) return;
        try
        {
            var next = _controller.ReadConfigurationSnapshot(); _readErrors.Accept("");
            if (_changes.Observe(next.Signature)) await RunAsync(() => _controller.ReloadConfigurationAsync());
        }
        catch (Exception error)
        {
            if (_readErrors.Observe(error.Message)) ReportError(error);
        }
        if (_geoBusy || DateTime.UtcNow < _nextGeoAttempt || !GeoDataManager.IsDue(_controller.GetGeoDataSnapshot())) return;
        _geoBusy = true; _nextGeoAttempt = DateTime.UtcNow.AddMinutes(5);
        try { await _controller.UpdateGeoDataAsync(onlyIfDue: true); }
        catch (Exception error) { ReportError(error); }
        finally { _geoBusy = false; }
    }

    private void ReportError(Exception error)
    {
        _controller.RecordEvent(error.Message); _icon.BalloonTipTitle = "TcpRoute2"; _icon.BalloonTipText = error.Message;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.Error; _icon.ShowBalloonTip(4000);
    }

    private string T(string english, string simplified, string traditional) => WindowsLocalization.T(_controller.Language, english, simplified, traditional);

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TcpRoute2.ico"));
        return resource is null ? (Icon)SystemIcons.Application.Clone() : new Icon(resource.Stream);
    }

    public void Dispose()
    {
        _timer.Stop(); _window.AllowClose(); _window.Close();
        _icon.Visible = false; _icon.Dispose(); _trayIcon.Dispose(); _menu.Dispose();
    }
}
