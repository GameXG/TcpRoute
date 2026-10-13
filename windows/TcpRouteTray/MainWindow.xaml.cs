using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TcpRouteTray;

internal partial class MainWindow : Window
{
    private const int MaxConfigBytes = 2 * 1024 * 1024;
    private readonly TcpRouteController _controller;
    private readonly DispatcherTimer _statusTimer;
    private EditableTcpRouteConfig _draft = new();
    private EditableUpstream _directUpstream = EditableUpstream.Direct();
    private ObservableCollection<EditableUpstream> _proxyRows = new();
    private ObservableCollection<EditableHostSource> _hostRows = new();
    private TcpRouteConfig? _selectedConfig;
    private bool _loading;
    private bool _isDirty;
    private bool _allowClose;
    private string? _uiMessage;
    private DateTime _uiMessageUntil;

    public MainWindow(TcpRouteController controller)
    {
        _controller = controller;
        CreatableProxyTypes = EditableTcpRouteConfigCodec.CreatableProxySchemes;
        MatchTypes = new[] { "base", "suffix", "pan", "regex" };
        InitializeComponent();
        DataContext = this;
        RebuildPreferenceOptions();
        _loading = true;
        AppearancePicker.SelectedValue = _controller.Appearance;
        LanguagePicker.SelectedValue = _controller.Language;
        GeoDataIntervalPicker.SelectedValue = _controller.GetGeoDataSnapshot().Interval;
        _loading = false;
        ApplyAppearance();
        ApplyLanguage();
        if (HostsGrid.Columns[1] is DataGridComboBoxColumn hostMatchColumn) hostMatchColumn.ItemsSource = MatchTypes;
        LoadProfiles();
        LoadSelectedDraft();
        RefreshRuntimeState();
        RefreshActivity(null, null);
        RefreshGeoDataStatus();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshRuntimeState();
        _statusTimer.Start();
        SourceInitialized += (_, _) => ApplyAppearance();
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        };
        Loaded += async (_, _) => await UpdateGeoDataIfDue();
    }

    public IReadOnlyList<string> CreatableProxyTypes { get; }

    public IReadOnlyList<string> MatchTypes { get; }

    public ObservableCollection<LocalizedOption> AppearanceOptions { get; } = new();

    public ObservableCollection<LocalizedOption> LanguageOptions { get; } = new();

    public ObservableCollection<LocalizedOption> GeoDataIntervalOptions { get; } = new();
    public ObservableCollection<LocalizedOption> NetworkModeOptions { get; } = new();
    public ObservableCollection<LocalizedOption> OutboundInterfaceOptions { get; } = new();

    public void AllowClose() => _allowClose = true;

    public void RefreshRuntimeState()
    {
        var running = _controller.IsRunning;
        var good = (System.Windows.Media.Brush)FindResource("GoodBrush");
        var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
        HeroStatusDot.Fill = NavStatusDot.Fill = running ? good : muted;
        HeroStatus.Text = running ? T("TcpRoute2 is running", "TcpRoute2 正在运行", "TcpRoute2 正在運行") : T("TcpRoute2 is stopped", "TcpRoute2 已停止", "TcpRoute2 已停止");
        HeroEndpoint.Text = running
            ? (_controller.UseWintun ? "TUN / VPN (Wintun) · " : "") + $"SOCKS5 127.0.0.1:{_controller.RunningPort}"
            : T("SOCKS5 service is not running", "SOCKS5 服务未运行", "SOCKS5 服務未運行");
        NavStatusText.Text = _controller.IsStarting ? T("Starting", "启动中", "啟動中") : running ? T("Running", "运行中", "運行中") : T("Stopped", "已停止", "已停止");
        RunText.Text = _controller.UseWintun
            ? (running ? T("Stop TUN", "停止 TUN", "停止 TUN") : T("Start TUN", "启动 TUN", "啟動 TUN"))
            : (running ? T("Stop", "停止", "停止") : T("Start", "启动", "啟動"));
        RunIcon.Text = running ? "\uE71A" : "\uE768";
        RunButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, running ? "DangerBrush" : "AccentBrush");
        OverviewProfile.Text = _selectedConfig?.Name ?? $"{_draft.ProfileName} ({T("draft", "草稿", "草稿")})";
        OverviewProxy.Text = _controller.SystemProxyEnabled ? T("Enabled", "已启用", "已啟用") : T("Disabled", "未启用", "未啟用");
        OverviewLan.Text = _controller.AllowLan ? T("Open", "已开放", "已開放") : T("Closed", "未开放", "未開放");
        CoreHealth.Text = running ? T("Healthy", "运行正常", "運行正常") : T("Waiting to start", "等待启动", "等待啟動");
        UpstreamCount.Text = (_proxyRows.Count + 1).ToString();
        HostCount.Text = _hostRows.Count.ToString();
        RecentEvent.Text = _uiMessage is not null && DateTime.UtcNow < _uiMessageUntil
            ? _uiMessage
            : LocalizeControllerMessage(_controller.LastMessage);

        _loading = true;
        NetworkModePicker.SelectedValue = _controller.UseWintun ? "wintun" : "proxy";
        NetworkModeTitle.Text = T("Network mode", "网络模式", "網路模式");
        NetworkModePicker.IsEnabled = !running && !_controller.IsStarting;
        OutboundInterfaceRow.Visibility = _controller.UseWintun ? Visibility.Visible : Visibility.Collapsed;
        OutboundInterfaceTitle.Text = T("Outbound interface", "出口网络接口", "出口網路介面");
        OutboundInterfacePicker.SelectedValue = _controller.OutboundInterfaceId;
        OutboundInterfacePicker.IsEnabled = !running && !_controller.IsStarting;
        RunButton.IsEnabled = !_controller.IsStarting;
        SystemProxyToggle.IsChecked = _controller.SystemProxyEnabled;
        SystemProxyToggle.IsEnabled = !_controller.UseWintun && !_controller.IsStarting;
        AllowLanToggle.IsEnabled = !_controller.UseWintun && !_controller.IsStarting;
        AllowLanToggle.IsChecked = _controller.AllowLan;
        AllowStunToggle.IsChecked = _controller.AllowStun;
        AllowQuicToggle.IsChecked = _controller.AllowQuic;
        EnableIpv6Toggle.IsChecked = _controller.EnableIpv6;
        AllowStunToggle.IsEnabled = !running && !_controller.IsStarting;
        AllowQuicToggle.IsEnabled = !running && !_controller.IsStarting;
        EnableIpv6Toggle.IsEnabled = !running && !_controller.IsStarting;
        _loading = false;
    }

    private void LoadProfiles(string? selectedId = null)
    {
        _loading = true;
        var profiles = _controller.LoadConfigs();
        ProfilePicker.ItemsSource = profiles;
        var id = selectedId ?? _controller.SelectedConfigId;
        _selectedConfig = profiles.FirstOrDefault(profile => profile.Id == id) ?? profiles.FirstOrDefault();
        ProfilePicker.SelectedItem = _selectedConfig;
        _loading = false;
    }

    private void LoadSelectedDraft()
    {
        if (_selectedConfig is null) return;
        _loading = true;
        _draft = _controller.LoadEditableConfig(_selectedConfig.Id);
        _directUpstream = _draft.Upstreams.FirstOrDefault(item => item.IsDirect) ?? EditableUpstream.Direct();
        _proxyRows = new ObservableCollection<EditableUpstream>(_draft.Upstreams.Where(item => !item.IsDirect));
        _hostRows = new ObservableCollection<EditableHostSource>(_draft.Hosts);
        UpstreamList.ItemsSource = _proxyRows;
        HostsGrid.ItemsSource = _hostRows;
        var selectedRouting = RoutingUpstreamList.SelectedItem as EditableUpstream;
        RoutingUpstreamList.ItemsSource = RoutingRows();
        if (selectedRouting is not null) RoutingUpstreamList.SelectedItem = selectedRouting;
        UpstreamList.SelectedIndex = _proxyRows.Count > 0 ? 0 : -1;
        RoutingUpstreamList.SelectedIndex = 0;
        _loading = false;
        SetDirty(false);
        RefreshRuntimeState();
    }

    private ObservableCollection<EditableUpstream> RoutingRows() => new(new[] { _directUpstream }.Concat(_proxyRows));

    private void NavigationChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string page }) return;
        OverviewPage.Visibility = page == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        UpstreamsPage.Visibility = page == "Upstreams" ? Visibility.Visible : Visibility.Collapsed;
        RoutingPage.Visibility = page == "Routing" ? Visibility.Visible : Visibility.Collapsed;
        HostsPage.Visibility = page == "Hosts" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "Activity") RefreshActivity(null, null);
    }

    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfilePicker.SelectedItem is not TcpRouteConfig selected || selected.Id == _selectedConfig?.Id) return;
        if (_isDirty && System.Windows.MessageBox.Show(this, "当前配置有未保存更改。放弃更改并切换配置？", "TcpRoute2",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            _loading = true;
            ProfilePicker.SelectedItem = _selectedConfig;
            _loading = false;
            return;
        }

        _controller.SelectConfig(selected.Id);
        _selectedConfig = selected;
        LoadSelectedDraft();
    }

    private async void ToggleRuntime(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            if (_controller.IsRunning) await _controller.StopAsync();
            else await _controller.StartAsync();
            RefreshRuntimeState();
        });
    }

    private async void RunConnectivityTest(object sender, RoutedEventArgs e)
    {
        await RunGuardedAsync(async () =>
        {
            TestResult.Text = "正在检查本地 SOCKS5 连通性…";
            var milliseconds = await _controller.MeasureSocksConnectAsync();
            TestResult.Text = $"连通性正常，连接耗时 {milliseconds} ms";
            RefreshRuntimeState();
        });
    }

    private void SystemProxyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RunGuarded(() =>
        {
            _controller.SetSystemProxyEnabled(SystemProxyToggle.IsChecked == true);
            RefreshRuntimeState();
        });
    }

    private void NetworkModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NetworkModePicker.SelectedValue is not string value) return;
        RunGuarded(() => { _controller.SetUseWintun(value == "wintun"); RefreshRuntimeState(); });
    }

    private void OutboundInterfaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutboundInterfacePicker.SelectedValue is not string value) return;
        RunGuarded(() => { _controller.SetOutboundInterface(value); RefreshRuntimeState(); });
    }

    private void OutboundInterfaceDropDownOpened(object sender, EventArgs e) => RefreshOutboundInterfaces();

    private void RefreshOutboundInterfaces()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            OutboundInterfaceOptions.Clear();
            OutboundInterfaceOptions.Add(new("", T("Automatic", "自动", "自動")));
            foreach (var adapter in WintunNetwork.AvailableInterfaces())
                OutboundInterfaceOptions.Add(new(adapter.Id, adapter.Name));
            if (!OutboundInterfaceOptions.Any(option => option.Value == _controller.OutboundInterfaceId))
                OutboundInterfaceOptions.Add(new(_controller.OutboundInterfaceId, T("Selected interface unavailable", "已选接口不可用", "已選介面不可用")));
            OutboundInterfacePicker.SelectedValue = _controller.OutboundInterfaceId;
        }
        finally { _loading = wasLoading; }
    }

    private void AllowLanChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _controller.SetAllowLan(AllowLanToggle.IsChecked == true);
        SetUiMessage(_controller.IsRunning
            ? T("LAN setting updated; restart to apply", "LAN 设置已更新；重新启动后生效", "LAN 設置已更新；重新啟動後生效")
            : _controller.LastMessage);
        RefreshRuntimeState();
    }

    private void NetworkFeatureChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _controller.IsRunning) return;
        _controller.SetAllowStun(AllowStunToggle.IsChecked == true);
        _controller.SetAllowQuic(AllowQuicToggle.IsChecked == true);
        _controller.SetEnableIpv6(EnableIpv6Toggle.IsChecked == true);
        SetUiMessage(T("Network settings saved; apply on next start", "网络设置已保存；下次启动生效", "網路設定已儲存；下次啟動生效"));
        RefreshRuntimeState();
    }

    private void AppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppearancePicker.SelectedValue is not string value) return;
        _controller.SetAppearance(value);
        ApplyAppearance();
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguagePicker.SelectedValue is not string value) return;
        _controller.SetLanguage(value);
        ApplyLanguage();
        RefreshRuntimeState();
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_controller.Appearance != "system") return;
        Dispatcher.BeginInvoke(ApplyAppearance);
    }

    private void ApplyAppearance() => WindowsTheme.Apply(this, _controller.Appearance);

    private void ApplyLanguage()
    {
        MainWindowLocalization.Apply(this, _controller.Language);
        MainWindowLocalization.Apply(ToolsMenu, _controller.Language);
        RebuildPreferenceOptions();
        _loading = true;
        AppearancePicker.SelectedValue = _controller.Appearance;
        LanguagePicker.SelectedValue = _controller.Language;
        GeoDataIntervalPicker.SelectedValue = _controller.GetGeoDataSnapshot().Interval;
        _loading = false;
        GeoDataTitle.Text = T("Geo Data", "Geo 数据", "Geo 資料");
        GeoDataUpdateButton.Content = T("Update Now", "立即更新", "立即更新");
        RefreshGeoDataStatus();
    }

    private void RebuildPreferenceOptions()
    {
        AppearanceOptions.Clear();
        AppearanceOptions.Add(new("system", T("System", "跟随系统", "跟隨系統")));
        AppearanceOptions.Add(new("light", T("Light", "浅色", "淺色")));
        AppearanceOptions.Add(new("dark", T("Dark", "深色", "深色")));
        LanguageOptions.Clear();
        LanguageOptions.Add(new("system", T("System language", "跟随系统语言", "跟隨系統語言")));
        LanguageOptions.Add(new("en", "English"));
        LanguageOptions.Add(new("zh-Hans", "简体中文"));
        LanguageOptions.Add(new("zh-Hant", "繁體中文"));
        GeoDataIntervalOptions.Clear();
        NetworkModeOptions.Clear();
        NetworkModeOptions.Add(new("proxy", T("Proxy", "代理", "代理")));
        NetworkModeOptions.Add(new("wintun", "TUN / VPN (Wintun)"));
        RefreshOutboundInterfaces();
        GeoDataIntervalOptions.Add(new("off", T("Off", "关闭", "關閉")));
        GeoDataIntervalOptions.Add(new("daily", T("Daily", "每天", "每天")));
        GeoDataIntervalOptions.Add(new("weekly", T("Weekly", "每周", "每週")));
    }

    private void GeoDataIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GeoDataIntervalPicker.SelectedValue is not string value) return;
        _controller.SetGeoDataInterval(value);
        RefreshGeoDataStatus();
    }

    private async void UpdateGeoData(object sender, RoutedEventArgs e)
    {
        await RunGeoDataUpdate(false);
    }

    private async Task UpdateGeoDataIfDue() => await RunGeoDataUpdate(true);

    private async Task RunGeoDataUpdate(bool onlyIfDue)
    {
        GeoDataUpdateButton.IsEnabled = false;
        if (!onlyIfDue) GeoDataStatus.Text = T("Updating Geo data…", "正在更新 Geo 数据…", "正在更新 Geo 資料…");
        try
        {
            await _controller.UpdateGeoDataAsync(onlyIfDue);
            RefreshGeoDataStatus();
        }
        catch (Exception error)
        {
            RefreshGeoDataStatus();
            if (!onlyIfDue) SetUiMessage($"{T("Geo data update failed", "Geo 数据更新失败", "Geo 資料更新失敗")}: {error.Message}");
        }
        finally
        {
            GeoDataUpdateButton.IsEnabled = true;
        }
    }

    private void RefreshGeoDataStatus()
    {
        var state = _controller.GetGeoDataSnapshot();
        var updated = state.LastSuccess?.LocalDateTime.ToString("g") ?? T("Never updated", "从未更新", "從未更新");
        var sizes = $"Geosite {FormatBytes(state.GeositeBytes)} · GeoIP {FormatBytes(state.GeoipBytes)}";
        GeoDataStatus.Text = string.IsNullOrWhiteSpace(state.LastError)
            ? $"{sizes} · {T("Last update", "最近更新", "最近更新")}: {updated}"
            : $"{sizes} · {T("Last success", "最近成功", "最近成功")}: {updated} · {T("Error", "错误", "錯誤")}: {state.LastError}";
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB"
        : $"{bytes / 1024d:0.0} KB";

    private string T(string english, string simplified, string traditional)
        => WindowsLocalization.T(_controller.Language, english, simplified, traditional);

    private void SetUiMessage(string message)
    {
        _uiMessage = message;
        _uiMessageUntil = DateTime.UtcNow.AddSeconds(6);
        RecentEvent.Text = message;
    }

    private string LocalizeControllerMessage(string message)
    {
        if (message == "Ready") return T("Ready", "已就绪", "已就緒");
        if (message == "TcpRoute2 stopped") return T("TcpRoute2 stopped", "TcpRoute2 已停止", "TcpRoute2 已停止");
        if (message == "LAN clients enabled") return T("LAN clients enabled", "LAN 客户端已启用", "LAN 客戶端已啟用");
        if (message == "LAN clients disabled") return T("LAN clients disabled", "LAN 客户端已禁用", "LAN 客戶端已禁用");
        if (message == "Windows SOCKS proxy enabled") return T("Windows SOCKS proxy enabled", "Windows SOCKS 代理已启用", "Windows SOCKS 代理已啟用");
        if (message == "Windows SOCKS proxy disabled") return T("Windows SOCKS proxy disabled", "Windows SOCKS 代理已禁用", "Windows SOCKS 代理已禁用");
        if (message == "Windows SOCKS proxy will be enabled on start") return T("Windows SOCKS proxy will be enabled on start", "启动时将启用 Windows SOCKS 代理", "啟動時將啟用 Windows SOCKS 代理");
        if (message == "TcpRoute2 executable path saved") return T("TcpRoute2 executable path saved", "TcpRoute2 程序路径已保存", "TcpRoute2 程序路徑已保存");
        if (message == "Windows proxy cleared") return T("Windows proxy cleared", "Windows 代理已清除", "Windows 代理已清除");
        if (message.StartsWith("Saved ", StringComparison.Ordinal))
            return T(message, $"已保存 {message[6..]}", $"已保存 {message[6..]}");
        if (message.StartsWith("Selected ", StringComparison.Ordinal))
            return T(message, $"已选择 {message[9..]}", $"已選擇 {message[9..]}");
        if (message.StartsWith("TcpRoute2 is running on ", StringComparison.Ordinal))
            return T(message, $"TcpRoute2 正在 {message[23..]} 运行", $"TcpRoute2 正在 {message[23..]} 運行");
        if (message.StartsWith("TcpRoute2 exited with code ", StringComparison.Ordinal))
            return T(message, $"TcpRoute2 已退出，代码 {message[26..]}", $"TcpRoute2 已退出，代碼 {message[26..]}");
        if (message.StartsWith("Speed test: ", StringComparison.Ordinal))
            return T(message, $"连通性检查：{message[12..]}", $"連通性檢查：{message[12..]}");
        return message;
    }

    private void UpstreamSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _loading = true;
        if (UpstreamList.SelectedItem is EditableUpstream upstream)
        {
            UpstreamName.Text = upstream.Name;
            UpstreamUrl.Text = upstream.ProxyUrl;
            UpstreamDns.IsChecked = upstream.DnsResolve;
            UpstreamCredit.Text = upstream.Credit.ToString();
            UpstreamSleep.Text = upstream.Sleep.ToString();
            UpstreamCorrect.Text = upstream.CorrectDelay.ToString();
            ProxyValidation.Text = EditableTcpRouteConfigCodec.ProxyUrlError(upstream.ProxyUrl) ?? "URL 格式有效";
            ProxyValidation.Foreground = EditableTcpRouteConfigCodec.ProxyUrlError(upstream.ProxyUrl) is null
                ? (System.Windows.Media.Brush)FindResource("GoodBrush") : (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
        else
        {
            UpstreamName.Text = UpstreamUrl.Text = UpstreamCredit.Text = UpstreamSleep.Text = UpstreamCorrect.Text = "";
            UpstreamDns.IsChecked = false;
            ProxyValidation.Text = "选择一个代理节点进行编辑";
        }
        _loading = false;
    }

    private void DraftFieldChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || UpstreamList.SelectedItem is not EditableUpstream upstream) return;
        upstream.Name = UpstreamName.Text;
        upstream.ProxyUrl = UpstreamUrl.Text;
        upstream.DnsResolve = UpstreamDns.IsChecked == true;
        _ = int.TryParse(UpstreamCredit.Text, out var credit); upstream.Credit = credit;
        _ = int.TryParse(UpstreamSleep.Text, out var sleep); upstream.Sleep = sleep;
        _ = int.TryParse(UpstreamCorrect.Text, out var correct); upstream.CorrectDelay = correct;
        var error = EditableTcpRouteConfigCodec.ProxyUrlError(upstream.ProxyUrl);
        ProxyValidation.Text = error ?? "URL 格式有效";
        ProxyValidation.Foreground = error is null ? (System.Windows.Media.Brush)FindResource("GoodBrush") : (System.Windows.Media.Brush)FindResource("DangerBrush");
        UpstreamList.Items.Refresh();
        RoutingUpstreamList.Items.Refresh();
        SetDirty(true);
    }

    private void AddProxy(object sender, RoutedEventArgs e)
    {
        var scheme = NewProxyType.SelectedItem?.ToString() ?? "socks5";
        var proxy = EditableUpstream.ProxyTemplate(scheme);
        proxy.Name = UniqueUpstreamName(scheme);
        _proxyRows.Add(proxy);
        RoutingUpstreamList.ItemsSource = RoutingRows();
        UpstreamList.SelectedItem = proxy;
        SetDirty(true);
    }

    private string UniqueUpstreamName(string basis)
    {
        var name = basis;
        for (var suffix = 2; _proxyRows.Any(item => item.Name == name); suffix++) name = $"{basis}-{suffix}";
        return name;
    }

    private void RemoveProxy(object sender, RoutedEventArgs e)
    {
        if (UpstreamList.SelectedItem is not EditableUpstream selected) return;
        if (System.Windows.MessageBox.Show(this, $"移除代理“{selected.Name}”及其分流规则？", "TcpRoute2",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _proxyRows.Remove(selected);
        RoutingUpstreamList.ItemsSource = RoutingRows();
        UpstreamList.SelectedIndex = _proxyRows.Count > 0 ? 0 : -1;
        SetDirty(true);
    }

    private void RoutingUpstreamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RoutingUpstreamList.SelectedItem is not EditableUpstream upstream)
        {
            WhitelistGrid.ItemsSource = null;
            BlacklistGrid.ItemsSource = null;
            return;
        }
        RefreshRoutingRules(upstream);
    }

    private void RefreshRoutingRules(EditableUpstream upstream)
    {
        WhitelistGrid.ItemsSource = new ObservableCollection<EditableDomainRule>(upstream.Whitelist);
        BlacklistGrid.ItemsSource = new ObservableCollection<EditableDomainRule>(upstream.Blacklist);
    }

    private void AddWhitelistLocal(object sender, RoutedEventArgs e) => AddRule(true, RuleSourceKind.Local);
    private void AddWhitelistRemote(object sender, RoutedEventArgs e) => AddRule(true, RuleSourceKind.Remote);
    private void AddWhitelistGeosite(object sender, RoutedEventArgs e) => AddRule(true, RuleSourceKind.Geosite);
    private void AddWhitelistGeoIP(object sender, RoutedEventArgs e) => AddRule(true, RuleSourceKind.GeoIP);
    private void AddBlacklistLocal(object sender, RoutedEventArgs e) => AddRule(false, RuleSourceKind.Local);
    private void AddBlacklistRemote(object sender, RoutedEventArgs e) => AddRule(false, RuleSourceKind.Remote);
    private void AddBlacklistGeosite(object sender, RoutedEventArgs e) => AddRule(false, RuleSourceKind.Geosite);
    private void AddBlacklistGeoIP(object sender, RoutedEventArgs e) => AddRule(false, RuleSourceKind.GeoIP);

    private void AddRule(bool whitelist, RuleSourceKind source)
    {
        if (RoutingUpstreamList.SelectedItem is not EditableUpstream upstream) return;
        var rule = EditableDomainRule.Template(source);
        (whitelist ? upstream.Whitelist : upstream.Blacklist).Add(rule);
        RefreshRoutingRules(upstream);
        (whitelist ? WhitelistGrid : BlacklistGrid).SelectedItem = rule;
        SetDirty(true);
    }

    private void RemoveWhitelist(object sender, RoutedEventArgs e) => RemoveRule(true);
    private void RemoveBlacklist(object sender, RoutedEventArgs e) => RemoveRule(false);
    private void ChooseWhitelistFile(object sender, RoutedEventArgs e) => ChooseRuleFile(WhitelistGrid);
    private void ChooseBlacklistFile(object sender, RoutedEventArgs e) => ChooseRuleFile(BlacklistGrid);

    private void RemoveRule(bool whitelist)
    {
        if (RoutingUpstreamList.SelectedItem is not EditableUpstream upstream) return;
        var grid = whitelist ? WhitelistGrid : BlacklistGrid;
        if (grid.SelectedItem is not EditableDomainRule rule) return;
        (whitelist ? upstream.Whitelist : upstream.Blacklist).Remove(rule);
        RefreshRoutingRules(upstream);
        SetDirty(true);
    }

    private void RuleGridEdited(object sender, DataGridCellEditEndingEventArgs e) => SetDirty(true);

    private void ChooseRuleFile(DataGrid grid)
    {
        if (grid.SelectedItem is not EditableDomainRule rule ||
            rule.Source is RuleSourceKind.Remote or RuleSourceKind.Geosite or RuleSourceKind.GeoIP) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Rule files|*.txt;*.dat|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            rule.Path = dialog.FileName;
            grid.Items.Refresh();
            SetDirty(true);
        }
    }

    private void AddLocalHost(object sender, RoutedEventArgs e)
    {
        var host = new EditableHostSource();
        _hostRows.Add(host);
        HostsGrid.SelectedItem = host;
        SetDirty(true);
    }

    private void AddRemoteHost(object sender, RoutedEventArgs e)
    {
        var host = new EditableHostSource { Source = RuleSourceKind.Remote };
        _hostRows.Add(host);
        HostsGrid.SelectedItem = host;
        SetDirty(true);
    }

    private void RemoveHost(object sender, RoutedEventArgs e)
    {
        if (HostsGrid.SelectedItem is EditableHostSource host)
        {
            _hostRows.Remove(host);
            SetDirty(true);
        }
    }

    private void HostGridEdited(object sender, DataGridCellEditEndingEventArgs e) => SetDirty(true);

    private void SaveConfiguration(object sender, RoutedEventArgs e)
    {
        RunGuarded(() =>
        {
            CommitPendingGridEdits();
            var config = BuildDraft();
            EditableTcpRouteConfigCodec.Validate(config);
            var fileName = _controller.SaveEditableConfig(config);
            LoadProfiles(fileName);
            LoadSelectedDraft();
            SetUiMessage(string.Format(T("Saved {0}; the running instance was not restarted", "已保存 {0}；运行中的实例未自动重启", "已保存 {0}；運行中的實例未自動重啟"), fileName));
        });
    }

    private EditableTcpRouteConfig BuildDraft()
    {
        var config = new EditableTcpRouteConfig { ProfileName = _draft.ProfileName, ListenPort = _draft.ListenPort, DnsToml = _draft.DnsToml };
        config.Upstreams.Clear();
        config.Upstreams.Add(_directUpstream);
        foreach (var upstream in _proxyRows) config.Upstreams.Add(upstream);
        foreach (var host in _hostRows) config.Hosts.Add(host);
        return config;
    }

    private void CommitPendingGridEdits()
    {
        foreach (var grid in new[] { WhitelistGrid, BlacklistGrid, HostsGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private void SetDirty(bool dirty)
    {
        _isDirty = dirty;
        SaveButton.IsEnabled = dirty;
        DraftStatus.Text = dirty ? "有未保存更改" : "已保存";
        DraftDot.Fill = (System.Windows.Media.Brush)FindResource(dirty ? "WarningBrush" : "GoodBrush");
    }

    private void OpenToolsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.ContextMenu is { } menu)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void NewConfiguration(object sender, RoutedEventArgs e)
    {
        if (_isDirty && System.Windows.MessageBox.Show(this, "放弃当前未保存更改并新建配置？", "TcpRoute2",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var name = TextPromptWindow.Show(this, "新建配置", "配置名称");
        if (string.IsNullOrWhiteSpace(name)) return;
        var fresh = new EditableTcpRouteConfig { ProfileName = name };
        ApplyImportedDraft(EditableTcpRouteConfigCodec.Render(fresh), name);
    }

    private void ReloadConfiguration(object sender, RoutedEventArgs e)
    {
        if (_selectedConfig is null) return;
        if (_isDirty && System.Windows.MessageBox.Show(this, "放弃当前未保存更改并重新载入？", "TcpRoute2",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        LoadSelectedDraft();
        SetUiMessage(T("Reloaded configuration from disk", "已重新载入磁盘中的配置", "已重新載入磁盤中的配置"));
    }

    private void ImportConfigFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "TcpRoute2 config|*.toml|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        RunGuarded(() =>
        {
            if (new FileInfo(dialog.FileName).Length > MaxConfigBytes) throw new InvalidOperationException("配置文件超过 2 MB。 ");
            ApplyImportedDraft(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
        });
    }

    private async void ImportConfigUrl(object sender, RoutedEventArgs e)
    {
        var url = TextPromptWindow.Show(this, "导入在线配置", "HTTP 或 HTTPS 配置 URL");
        if (url is null) return;
        await RunGuardedAsync(async () =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("配置 URL 必须使用 HTTP 或 HTTPS。 ");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync();
            if (data.Length > MaxConfigBytes) throw new InvalidOperationException("配置文件超过 2 MB。 ");
            ApplyImportedDraft(Encoding.UTF8.GetString(data), Path.GetFileNameWithoutExtension(uri.AbsolutePath).Trim('/'));
        });
    }

    private void ApplyImportedDraft(string content, string suggestedName)
    {
        var name = string.IsNullOrWhiteSpace(suggestedName) ? "imported" : suggestedName;
        _draft = EditableTcpRouteConfigCodec.Parse(name, content);
        _selectedConfig = null;
        _loading = true;
        ProfilePicker.SelectedItem = null;
        _loading = false;
        _directUpstream = _draft.Upstreams.FirstOrDefault(item => item.IsDirect) ?? EditableUpstream.Direct();
        _proxyRows = new ObservableCollection<EditableUpstream>(_draft.Upstreams.Where(item => !item.IsDirect));
        _hostRows = new ObservableCollection<EditableHostSource>(_draft.Hosts);
        UpstreamList.ItemsSource = _proxyRows;
        HostsGrid.ItemsSource = _hostRows;
        RoutingUpstreamList.ItemsSource = RoutingRows();
        UpstreamList.SelectedIndex = _proxyRows.Count > 0 ? 0 : -1;
        RoutingUpstreamList.SelectedIndex = 0;
        SetDirty(true);
        SetUiMessage(T("Imported as a draft; review it and select Save configuration", "配置已导入为草稿；检查后按“保存配置”写入磁盘", "配置已導入為草稿；檢查後按「保存配置」寫入磁盤"));
    }

    private void ExportSavedConfig(object sender, RoutedEventArgs e)
    {
        if (_selectedConfig is null)
        {
            System.Windows.MessageBox.Show(this, "导入的草稿尚未保存，不能导出为已保存配置。", "TcpRoute2", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "TcpRoute2 config|*.toml", FileName = _selectedConfig.Id };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, _selectedConfig.Content, Encoding.UTF8);
            SetUiMessage(string.Format(T("Exported {0}", "已导出 {0}", "已導出 {0}"), Path.GetFileName(dialog.FileName)));
        }
    }

    private void OpenAdvancedToml(object sender, RoutedEventArgs e)
    {
        if (_isDirty)
        {
            System.Windows.MessageBox.Show(this, "请先保存或放弃当前 Quick UI 草稿，再打开 Advanced TOML。", "TcpRoute2",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var editor = new AdvancedTomlWindow(_controller) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            LoadProfiles(_controller.SelectedConfigId);
            LoadSelectedDraft();
        }
    }

    private void RefreshActivity(object? sender, RoutedEventArgs? e)
    {
        ActivityLog.Text = string.Join(Environment.NewLine, _controller.ReadRecentLogLines(500));
        ActivityLog.ScrollToEnd();
    }

    private void OpenLogFolder(object sender, RoutedEventArgs e) => _controller.OpenLogs();
    private void OpenConfigFolder(object sender, RoutedEventArgs e) => _controller.OpenConfigFolder();

    private void LocateExecutable(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "TcpRoute2 executable|TcpRoute2.exe|Executable files|*.exe|All files|*.*" };
        if (dialog.ShowDialog(this) == true) RunGuarded(() => _controller.SetExecutablePath(dialog.FileName));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        SetUiMessage(T("TcpRoute2 was hidden to the system tray", "TcpRoute2 已隐藏到系统托盘", "TcpRoute2 已隱藏到系統托盤"));
    }

    private void RunGuarded(Action action)
    {
        try { action(); RefreshRuntimeState(); }
        catch (Exception ex)
        {
            SetUiMessage(ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "TcpRoute2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try { await action(); RefreshRuntimeState(); }
        catch (Exception ex)
        {
            SetUiMessage(ex.Message);
            TestResult.Text = ex.Message;
            System.Windows.MessageBox.Show(this, ex.Message, "TcpRoute2", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

internal sealed record LocalizedOption(string Value, string Label);
