using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Forms;

namespace TcpRouteTray;

internal sealed class ConfigEditorForm : Form
{
    private const int MaxConfigBytes = 2 * 1024 * 1024;
    private readonly TcpRouteController _controller;
    private readonly TextBox _profileName = new();
    private readonly NumericUpDown _listenPort = new();
    private readonly DataGridView _upstreams = new();
    private readonly ComboBox _proxyType = new();
    private readonly ComboBox _routingUpstream = new();
    private readonly DataGridView _whitelist = new();
    private readonly DataGridView _blacklist = new();
    private readonly DataGridView _hosts = new();
    private readonly TabControl _modeTabs = new();
    private TabPage? _quickPage;
    private TabPage? _advancedPage;
    private readonly TextBox _rawProfileName = new();
    private readonly TextBox _rawConfig = new();
    private readonly Label _status = new();
    private BindingList<EditableUpstream> _proxyRows = new();
    private BindingList<EditableHostSource> _hostRows = new();
    private EditableUpstream _directUpstream = EditableUpstream.Direct();
    private string _dnsToml = "";

    public ConfigEditorForm(TcpRouteController controller)
    {
        _controller = controller;
        Text = "TcpRoute2 Config";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1080, 780);
        MinimumSize = new Size(900, 660);
        BuildLayout();
        LoadEditable(_controller.LoadEditableConfig());
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "TcpRoute2 Config — direct is built in and is not editable",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 0, 0, 8)
        }, 0, 0);
        ConfigureModeTabs();
        root.Controls.Add(_modeTabs, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);
    }

    private void ConfigureModeTabs()
    {
        _modeTabs.Dock = DockStyle.Fill;
        _advancedPage = BuildAdvancedPage();
        _quickPage = BuildQuickPage();
        _modeTabs.TabPages.Add(_advancedPage);
        _modeTabs.TabPages.Add(_quickPage);
        _modeTabs.SelectedTab = _advancedPage;
    }

    private bool IsAdvancedMode => _modeTabs.SelectedTab == _advancedPage;

    private TabPage BuildQuickPage()
    {
        var page = new TabPage("Quick");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

        var profile = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _profileName.Width = 220;
        _listenPort.Minimum = 1; _listenPort.Maximum = 65535; _listenPort.Value = 7070; _listenPort.Width = 100;
        profile.Controls.AddRange([LabelFor("Profile"), _profileName]);
        layout.Controls.Add(profile, 0, 0);
        layout.Controls.Add(BuildUpstreamGroup(), 0, 1);
        layout.Controls.Add(BuildRoutingGroup(), 0, 2);
        layout.Controls.Add(BuildHostGroup(), 0, 3);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildUpstreamGroup()
    {
        var group = new GroupBox { Text = "Connection Configuration", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _proxyType.DropDownStyle = ComboBoxStyle.DropDownList;
        _proxyType.Items.AddRange(EditableTcpRouteConfigCodec.CreatableProxySchemes.Cast<object>().ToArray());
        _proxyType.SelectedItem = "socks5";
        var add = new Button { Text = "Add Proxy", AutoSize = true };
        add.Click += (_, _) => AddProxy();
        var remove = new Button { Text = "Remove Selected", AutoSize = true };
        remove.Click += (_, _) => RemoveSelected(_upstreams, _proxyRows);
        controls.Controls.AddRange([LabelFor("Proxy Type"), _proxyType, add, remove]);
        ConfigureUpstreamGrid();
        layout.Controls.Add(controls, 0, 0); layout.Controls.Add(_upstreams, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildRoutingGroup()
    {
        var group = new GroupBox { Text = "Routing Configuration", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        _routingUpstream.DropDownStyle = ComboBoxStyle.DropDownList;
        _routingUpstream.Width = 180;
        _routingUpstream.SelectedIndexChanged += (_, _) => BindSelectedRoutingRules();
        var localWhite = ButtonFor("+ Local whitelist", () => AddRule(_whitelist, RuleSourceKind.Local));
        var remoteWhite = ButtonFor("+ Remote whitelist", () => AddRule(_whitelist, RuleSourceKind.Remote));
        var geoWhite = ButtonFor("+ Geosite whitelist", () => AddRule(_whitelist, RuleSourceKind.Geosite));
        var geoIPWhite = ButtonFor("+ GeoIP whitelist", () => AddRule(_whitelist, RuleSourceKind.GeoIP));
        var localBlack = ButtonFor("+ Local blacklist", () => AddRule(_blacklist, RuleSourceKind.Local));
        var remoteBlack = ButtonFor("+ Remote blacklist", () => AddRule(_blacklist, RuleSourceKind.Remote));
        var geoBlack = ButtonFor("+ Geosite blacklist", () => AddRule(_blacklist, RuleSourceKind.Geosite));
        var geoIPBlack = ButtonFor("+ GeoIP blacklist", () => AddRule(_blacklist, RuleSourceKind.GeoIP));
        controls.Controls.AddRange([LabelFor("Upstream"), _routingUpstream, localWhite, remoteWhite, geoWhite, geoIPWhite, localBlack, remoteBlack, geoBlack, geoIPBlack]);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 500 };
        split.Panel1.Controls.Add(BuildRulePanel("Whitelist", _whitelist));
        split.Panel2.Controls.Add(BuildRulePanel("Blacklist", _blacklist));
        ConfigureRuleGrid(_whitelist);
        ConfigureRuleGrid(_blacklist);
        layout.Controls.Add(controls, 0, 0); layout.Controls.Add(split, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildRulePanel(string title, DataGridView grid)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        controls.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 8, 0) });
        controls.Controls.Add(ButtonFor("Choose Local…", () => ChooseRuleFile(grid)));
        controls.Controls.Add(ButtonFor("Remove", () => RemoveSelectedRules(grid)));
        layout.Controls.Add(controls, 0, 0); layout.Controls.Add(grid, 0, 1);
        return layout;
    }

    private Control BuildHostGroup()
    {
        var group = new GroupBox { Text = "Host Configuration — local or remote hosts files", Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        controls.Controls.AddRange([
            ButtonFor("Add Local", () => _hostRows.Add(new EditableHostSource())),
            ButtonFor("Add Remote", () => _hostRows.Add(new EditableHostSource { Source = RuleSourceKind.Remote })),
            ButtonFor("Choose Local…", ChooseHostFile),
            ButtonFor("Remove", () => RemoveSelected(_hosts, _hostRows))
        ]);
        ConfigureHostGrid();
        layout.Controls.Add(controls, 0, 0); layout.Controls.Add(_hosts, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private TabPage BuildAdvancedPage()
    {
        var page = new TabPage("Advanced TOML");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rawProfileName.Dock = DockStyle.Top;
        _rawConfig.Dock = DockStyle.Fill; _rawConfig.Multiline = true; _rawConfig.AcceptsReturn = true; _rawConfig.AcceptsTab = true;
        _rawConfig.ScrollBars = ScrollBars.Both; _rawConfig.WordWrap = false; _rawConfig.Font = new Font(FontFamily.GenericMonospace, Font.Size);
        layout.Controls.Add(LabelFor("Profile"), 0, 0); layout.Controls.Add(_rawProfileName, 0, 1); layout.Controls.Add(_rawConfig, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Text = "Ready"; _status.AutoSize = true; _status.Anchor = AnchorStyles.Left;
        var actions = new FlowLayoutPanel { AutoSize = true };
        actions.Controls.AddRange([
            ButtonFor("Import File…", ImportConfigFile), ButtonFor("Import URL…", ImportOnlineConfig),
            ButtonFor("Export…", ExportConfig), ButtonFor("Reload", () => LoadEditable(_controller.LoadEditableConfig())),
            ButtonFor("New", LoadNewEditable), ButtonFor("Save Config", SaveEditable)
        ]);
        footer.Controls.Add(_status, 0, 0); footer.Controls.Add(actions, 1, 0);
        return footer;
    }

    private void ConfigureUpstreamGrid()
    {
        ConfigureGrid(_upstreams);
        _upstreams.Columns.Add(TextColumn("Name", nameof(EditableUpstream.Name), 120));
        _upstreams.Columns.Add(TextColumn("Proxy URL", nameof(EditableUpstream.ProxyUrl), 320, true));
        _upstreams.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "DNS", DataPropertyName = nameof(EditableUpstream.DnsResolve), Width = 52 });
        _upstreams.Columns.Add(TextColumn("Credit", nameof(EditableUpstream.Credit), 65));
        _upstreams.Columns.Add(TextColumn("Sleep", nameof(EditableUpstream.Sleep), 65));
        _upstreams.Columns.Add(TextColumn("Correct", nameof(EditableUpstream.CorrectDelay), 70));
        _upstreams.SelectionChanged += (_, _) => RefreshRoutingUpstreams();
    }

    private void ConfigureRuleGrid(DataGridView grid)
    {
        ConfigureGrid(grid);
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Source", DataPropertyName = nameof(EditableDomainRule.Source),
            DataSource = Enum.GetValues<RuleSourceKind>(), Width = 78
        });
        grid.Columns.Add(TextColumn("Match / Geo rules", nameof(EditableDomainRule.Type), 150));
        grid.Columns.Add(TextColumn("Path (local or remote)", nameof(EditableDomainRule.QuickPath), 220, true));
        grid.Columns.Add(TextColumn("Update", nameof(EditableDomainRule.QuickUpdateInterval), 72));
        grid.CellValueChanged += (_, args) => NormalizeRuleRow(grid, args.RowIndex);
    }

    private void ConfigureHostGrid()
    {
        ConfigureGrid(_hosts);
        _hosts.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Source", DataPropertyName = nameof(EditableHostSource.Source),
            DataSource = new[] { RuleSourceKind.Local, RuleSourceKind.Remote }, Width = 85
        });
        _hosts.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Match", DataPropertyName = nameof(EditableHostSource.Type),
            DataSource = new[] { "base", "suffix", "pan", "regex" }, Width = 90
        });
        _hosts.Columns.Add(TextColumn("Credit", nameof(EditableHostSource.Credit), 65));
        _hosts.Columns.Add(TextColumn("Path (local or remote)", nameof(EditableHostSource.Path), 420, true));
        _hosts.Columns.Add(TextColumn("Update", nameof(EditableHostSource.UpdateInterval), 90));
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill; grid.AutoGenerateColumns = false; grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = true; grid.RowHeadersVisible = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
    }

    private void LoadEditable(EditableTcpRouteConfig config)
    {
        _dnsToml = config.DnsToml;
        _profileName.Text = config.ProfileName;
        _listenPort.Value = Math.Clamp(config.ListenPort, 1, 65535);
        _directUpstream = config.Upstreams.FirstOrDefault(item => item.IsDirect) ?? EditableUpstream.Direct();
        _proxyRows = new BindingList<EditableUpstream>(config.Upstreams.Where(item => !item.IsDirect).ToList());
        _upstreams.DataSource = _proxyRows;
        _proxyRows.ListChanged += (_, _) => RefreshRoutingUpstreams();
        _hostRows = new BindingList<EditableHostSource>(config.Hosts);
        _hosts.DataSource = _hostRows;
        var raw = _controller.LoadRawEditableConfig();
        _rawProfileName.Text = raw.ProfileName; _rawConfig.Text = raw.Content;
        RefreshRoutingUpstreams();
        _status.Text = "Loaded editable config";
    }

    private void LoadNewEditable()
    {
        var config = new EditableTcpRouteConfig();
        _profileName.Text = config.ProfileName; _listenPort.Value = config.ListenPort;
        _directUpstream = EditableUpstream.Direct();
        _proxyRows = new BindingList<EditableUpstream>(); _upstreams.DataSource = _proxyRows;
        _proxyRows.ListChanged += (_, _) => RefreshRoutingUpstreams();
        _hostRows = new BindingList<EditableHostSource>(); _hosts.DataSource = _hostRows;
        _rawProfileName.Text = config.ProfileName; _rawConfig.Text = EditableTcpRouteConfigCodec.Render(config);
        RefreshRoutingUpstreams();
        _status.Text = "New editable config";
    }

    private void AddProxy()
    {
        var scheme = _proxyType.SelectedItem?.ToString() ?? "socks5";
        var proxy = EditableUpstream.ProxyTemplate(scheme);
        _proxyRows.Add(proxy);
        _upstreams.ClearSelection();
        if (_upstreams.Rows.Count > 0) _upstreams.Rows[^1].Selected = true;
        RefreshRoutingUpstreams();
    }

    private void RefreshRoutingUpstreams()
    {
        var selected = _routingUpstream.SelectedItem as EditableUpstream;
        var routingRows = new List<EditableUpstream> { _directUpstream };
        routingRows.AddRange(_proxyRows);
        _routingUpstream.DataSource = null;
        _routingUpstream.DisplayMember = nameof(EditableUpstream.Name);
        _routingUpstream.DataSource = routingRows;
        if (selected is not null && routingRows.Contains(selected)) _routingUpstream.SelectedItem = selected;
        else if (_upstreams.SelectedRows.Count > 0 && _upstreams.SelectedRows[0].DataBoundItem is EditableUpstream row) _routingUpstream.SelectedItem = row;
        BindSelectedRoutingRules();
    }

    private void BindSelectedRoutingRules()
    {
        if (_routingUpstream.SelectedItem is not EditableUpstream upstream)
        {
            _whitelist.DataSource = null; _blacklist.DataSource = null; return;
        }
        _whitelist.DataSource = new BindingList<EditableDomainRule>(upstream.Whitelist);
        _blacklist.DataSource = new BindingList<EditableDomainRule>(upstream.Blacklist);
    }

    private static void AddRule(DataGridView grid, RuleSourceKind source)
    {
        if (grid.DataSource is BindingList<EditableDomainRule> rows) rows.Add(EditableDomainRule.Template(source));
    }

    private static void NormalizeRuleRow(DataGridView grid, int index)
    {
        if (index < 0 || grid.Rows[index].DataBoundItem is not EditableDomainRule rule) return;
        if (rule.Source is RuleSourceKind.Geosite or RuleSourceKind.GeoIP)
        {
            var prefix = rule.Source == RuleSourceKind.GeoIP ? "geoip:" : "geosite:";
            if (rule.Type.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rule.Type = rule.Type[prefix.Length..];
            else if (string.IsNullOrWhiteSpace(rule.Type) || new[] { "base", "suffix", "pan", "regex" }.Contains(rule.Type, StringComparer.OrdinalIgnoreCase)) rule.Type = rule.Source == RuleSourceKind.GeoIP ? "cn,private" : "gfw";
            if (string.IsNullOrWhiteSpace(rule.Path) || rule.Path.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase) || rule.Path.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ||
                (rule.Source == RuleSourceKind.GeoIP && rule.Path == "geosite.dat") || (rule.Source == RuleSourceKind.Geosite && rule.Path == "geoip.dat"))
                rule.Path = rule.Source == RuleSourceKind.GeoIP ? "geoip.dat" : "geosite.dat";
        }
        else
        {
            if (rule.Path is "geosite.dat" or "geoip.dat") { rule.Type = "base"; rule.Path = ""; }
        }
        grid.Refresh();
    }

    private static void RemoveSelectedRules(DataGridView grid)
    {
        if (grid.DataSource is not BindingList<EditableDomainRule> rows) return;
        foreach (DataGridViewRow row in grid.SelectedRows)
            if (row.DataBoundItem is EditableDomainRule item) rows.Remove(item);
    }

    private void ChooseRuleFile(DataGridView grid)
    {
        if (grid.CurrentRow?.DataBoundItem is not EditableDomainRule rule ||
            rule.Source is RuleSourceKind.Remote or RuleSourceKind.Geosite or RuleSourceKind.GeoIP) return;
        var path = ChooseLocalFile("Rule files|*.txt;*.dat|All files|*.*");
        if (path is not null) { rule.Path = path; grid.Refresh(); }
    }

    private void ChooseHostFile()
    {
        if (_hosts.CurrentRow?.DataBoundItem is not EditableHostSource host || host.Source == RuleSourceKind.Remote) return;
        var path = ChooseLocalFile("Hosts files|*.txt;*.hosts|All files|*.*");
        if (path is not null) { host.Path = path; _hosts.Refresh(); }
    }

    private static string? ChooseLocalFile(string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    private EditableTcpRouteConfig CollectQuickConfig()
    {
        ValidateChildren(); _upstreams.EndEdit(); _whitelist.EndEdit(); _blacklist.EndEdit(); _hosts.EndEdit();
        var config = new EditableTcpRouteConfig { ProfileName = _profileName.Text, ListenPort = (int)_listenPort.Value, DnsToml = _dnsToml };
        config.Upstreams.Clear(); config.Upstreams.Add(_directUpstream);
        foreach (var upstream in _proxyRows) config.Upstreams.Add(upstream);
        foreach (var host in _hostRows) config.Hosts.Add(host);
        EditableTcpRouteConfigCodec.Validate(config);
        return config;
    }

    private void SaveEditable()
    {
        try
        {
            var fileName = IsAdvancedMode ? SaveRawEditable() : SaveQuickEditable();
            if (_controller.IsRunning) { _controller.RestartAsync().GetAwaiter().GetResult(); _status.Text = $"Saved {fileName} and restarted TcpRoute2"; }
            else _status.Text = $"Saved {fileName}";
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private string SaveQuickEditable()
    {
        var config = CollectQuickConfig();
        var fileName = _controller.SaveEditableConfig(config);
        _rawProfileName.Text = config.ProfileName; _rawConfig.Text = EditableTcpRouteConfigCodec.Render(config);
        return fileName;
    }

    private string SaveRawEditable()
    {
        _ = EditableTcpRouteConfigCodec.Parse(_rawProfileName.Text, _rawConfig.Text);
        var fileName = _controller.SaveRawEditableConfig(_rawProfileName.Text, _rawConfig.Text);
        LoadEditable(_controller.LoadEditableConfig()); _modeTabs.SelectedTab = _advancedPage;
        return fileName;
    }

    private void ImportConfigFile()
    {
        using var dialog = new OpenFileDialog { Filter = "TcpRoute2 config|*.toml|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > MaxConfigBytes) throw new InvalidOperationException("Config exceeds 2 MB.");
            ApplyImportedConfig(File.ReadAllText(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName));
        }
        catch (Exception ex) { ShowError($"Import failed: {ex.Message}"); }
    }

    private async void ImportOnlineConfig()
    {
        var url = PromptForText("Import Online Config", "HTTP or HTTPS config URL:");
        if (url is null) return;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new InvalidOperationException("Config URL must use HTTP or HTTPS.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync();
            if (data.Length > MaxConfigBytes) throw new InvalidOperationException("Config exceeds 2 MB.");
            ApplyImportedConfig(Encoding.UTF8.GetString(data), Path.GetFileNameWithoutExtension(uri.AbsolutePath).Trim('/'));
        }
        catch (Exception ex) { ShowError($"Online import failed: {ex.Message}"); }
    }

    private void ApplyImportedConfig(string content, string suggestedName)
    {
        var name = string.IsNullOrWhiteSpace(suggestedName) ? "imported" : suggestedName;
        var config = EditableTcpRouteConfigCodec.Parse(name, content);
        LoadEditableModel(config);
        _rawProfileName.Text = name; _rawConfig.Text = content; _modeTabs.SelectedTab = _advancedPage;
        _status.Text = "Imported config; review and save it";
    }

    private void LoadEditableModel(EditableTcpRouteConfig config)
    {
        _dnsToml = config.DnsToml;
        _profileName.Text = config.ProfileName; _listenPort.Value = Math.Clamp(config.ListenPort, 1, 65535);
        _directUpstream = config.Upstreams.FirstOrDefault(item => item.IsDirect) ?? EditableUpstream.Direct();
        _proxyRows = new BindingList<EditableUpstream>(config.Upstreams.Where(item => !item.IsDirect).ToList());
        _proxyRows.ListChanged += (_, _) => RefreshRoutingUpstreams(); _upstreams.DataSource = _proxyRows;
        _hostRows = new BindingList<EditableHostSource>(config.Hosts); _hosts.DataSource = _hostRows;
        RefreshRoutingUpstreams();
    }

    private void ExportConfig()
    {
        try
        {
            var content = IsAdvancedMode ? _rawConfig.Text : EditableTcpRouteConfigCodec.Render(CollectQuickConfig());
            var profile = IsAdvancedMode ? _rawProfileName.Text : _profileName.Text;
            using var dialog = new SaveFileDialog { Filter = "TcpRoute2 config|*.toml", FileName = EditableTcpRouteConfigCodec.FileNameFor(profile) };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dialog.FileName, content, Encoding.UTF8); _status.Text = $"Exported {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex) { ShowError($"Export failed: {ex.Message}"); }
    }

    private static string? PromptForText(string title, string label)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(540, 120), MinimizeBox = false, MaximizeBox = false };
        var prompt = new Label { Text = label, AutoSize = true, Location = new Point(12, 14) };
        var input = new TextBox { Location = new Point(12, 38), Width = 510 };
        var ok = new Button { Text = "Import", DialogResult = DialogResult.OK, Location = new Point(358, 78), Width = 78 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(444, 78), Width = 78 };
        dialog.Controls.AddRange([prompt, input, ok, cancel]); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        MessageBox.Show(this, message, "TcpRoute2 Config", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static Label LabelFor(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(4, 6, 4, 0) };
    private static Button ButtonFor(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action();
        return button;
    }

    private static DataGridViewTextBoxColumn TextColumn(string title, string property, int width, bool fill = false) => new()
    {
        HeaderText = title, DataPropertyName = property, Width = width,
        AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None
    };

    private static void RemoveSelected<T>(DataGridView grid, BindingList<T> rows) where T : class
    {
        foreach (DataGridViewRow row in grid.SelectedRows)
            if (!row.IsNewRow && row.DataBoundItem is T item) rows.Remove(item);
    }
}
