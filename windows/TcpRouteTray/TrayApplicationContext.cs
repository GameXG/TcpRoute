using System.Drawing;
using System.Windows.Forms;

namespace TcpRouteTray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TcpRouteController _controller;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu = new();

    public TrayApplicationContext(TcpRouteController controller)
    {
        _controller = controller;
        _controller.Initialize();

        _menu.Opening += (_, _) => RebuildMenu();

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = SystemIcons.Application,
            Text = "TcpRoute2",
            Visible = true
        };
        _trayIcon.DoubleClick += async (_, _) => await RunActionAsync(ToggleStartStopAsync);

        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        var status = _controller.IsRunning
            ? $"Running on SOCKS5 :{_controller.RunningPort}"
            : "Stopped";

        _menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        _menu.Items.Add(new ToolStripMenuItem(_controller.LastMessage) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var configsMenu = new ToolStripMenuItem("Configs");
        var configs = _controller.LoadConfigs();
        foreach (var config in configs)
        {
            var item = new ToolStripMenuItem(config.Name)
            {
                Checked = config.Id == _controller.SelectedConfigId
            };
            item.Click += async (_, _) => await RunActionAsync(async () =>
            {
                _controller.SelectConfig(config.Id);
                if (_controller.IsRunning)
                {
                    await _controller.RestartAsync();
                }
            });
            configsMenu.DropDownItems.Add(item);
        }

        if (configsMenu.DropDownItems.Count == 0)
        {
            configsMenu.DropDownItems.Add(new ToolStripMenuItem("No .toml configs") { Enabled = false });
        }

        configsMenu.DropDownItems.Add(new ToolStripSeparator());
        var reloadItem = new ToolStripMenuItem("Reload Configs");
        reloadItem.Click += (_, _) => RebuildMenu();
        configsMenu.DropDownItems.Add(reloadItem);
        _menu.Items.Add(configsMenu);

        var startStop = new ToolStripMenuItem(_controller.IsRunning ? "Stop TcpRoute2" : "Start TcpRoute2");
        startStop.Click += async (_, _) => await RunActionAsync(ToggleStartStopAsync);
        _menu.Items.Add(startStop);

        var restart = new ToolStripMenuItem("Restart TcpRoute2")
        {
            Enabled = _controller.SelectedConfigId is not null
        };
        restart.Click += async (_, _) => await RunActionAsync(_controller.RestartAsync);
        _menu.Items.Add(restart);

        _menu.Items.Add(new ToolStripSeparator());

        var systemProxy = new ToolStripMenuItem("Set Windows SOCKS Proxy")
        {
            Checked = _controller.SystemProxyEnabled,
            CheckOnClick = true
        };
        systemProxy.Click += async (_, _) => await RunActionAsync(() =>
        {
            _controller.SetSystemProxyEnabled(systemProxy.Checked);
            return Task.CompletedTask;
        });
        _menu.Items.Add(systemProxy);

        var allowLan = new ToolStripMenuItem("Allow Clients From LAN")
        {
            Checked = _controller.AllowLan,
            CheckOnClick = true
        };
        allowLan.Click += async (_, _) => await RunActionAsync(async () =>
        {
            _controller.SetAllowLan(allowLan.Checked);
            if (_controller.IsRunning)
            {
                await _controller.RestartAsync();
            }
        });
        _menu.Items.Add(allowLan);

        var speedTest = new ToolStripMenuItem("Speed Test")
        {
            Enabled = _controller.IsRunning
        };
        speedTest.Click += async (_, _) => await RunActionAsync(async () =>
        {
            var milliseconds = await _controller.MeasureSocksConnectAsync();
            ShowBalloon("TcpRoute2 speed test", $"{milliseconds} ms");
        });
        _menu.Items.Add(speedTest);

        _menu.Items.Add(new ToolStripSeparator());

        var configure = new ToolStripMenuItem("Configure...");
        configure.Click += (_, _) => ShowConfigEditor();
        _menu.Items.Add(configure);

        var locateExe = new ToolStripMenuItem("Locate TcpRoute2 executable...");
        locateExe.Click += (_, _) => LocateExecutable();
        _menu.Items.Add(locateExe);

        var openConfig = new ToolStripMenuItem("Open Config Folder");
        openConfig.Click += (_, _) => _controller.OpenConfigFolder();
        _menu.Items.Add(openConfig);

        var openLogs = new ToolStripMenuItem("Open Logs");
        openLogs.Click += (_, _) => _controller.OpenLogs();
        _menu.Items.Add(openLogs);

        var clearProxy = new ToolStripMenuItem("Clear Windows Proxy");
        clearProxy.Click += (_, _) =>
        {
            _controller.ClearWindowsProxy();
            ShowBalloon("TcpRoute2", "Windows proxy cleared.");
        };
        _menu.Items.Add(clearProxy);

        _menu.Items.Add(new ToolStripSeparator());

        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => ExitThread();
        _menu.Items.Add(quit);
    }

    private async Task ToggleStartStopAsync()
    {
        if (_controller.IsRunning)
        {
            await _controller.StopAsync();
        }
        else
        {
            await _controller.StartAsync();
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        try
        {
            await action();
            RebuildMenu();
        }
        catch (Exception ex)
        {
            ShowBalloon("TcpRoute2", ex.Message, ToolTipIcon.Error);
            RebuildMenu();
        }
    }

    private void LocateExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "TcpRoute2 executable|TcpRoute2.exe|Executable files|*.exe|All files|*.*",
            Title = "Locate TcpRoute2 executable"
        };

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            _controller.SetExecutablePath(dialog.FileName);
            ShowBalloon("TcpRoute2", "TcpRoute2 executable path saved.");
            RebuildMenu();
        }
    }

    private void ShowConfigEditor()
    {
        using var editor = new ConfigEditorForm(_controller);
        editor.ShowDialog();
        RebuildMenu();
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(4000);
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _menu.Dispose();
        base.ExitThreadCore();
    }
}
