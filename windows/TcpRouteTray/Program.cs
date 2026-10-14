namespace TcpRouteTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            ConfigCodecSelfTest.Run();
            GeoDataManagerSelfTest.Run();
            WintunDependenciesSelfTest.Run();
            TrayConfigurationSelfTest.Run();
            PowerShellOutputSelfTest.Run();
            return;
        }
        using var instanceLock = new Mutex(false, @"Local\" + WintunNetwork.AdapterName(AppContext.BaseDirectory) + "-Client");
        try
        {
            if (!instanceLock.WaitOne(TimeSpan.FromSeconds(10)))
            {
                System.Windows.MessageBox.Show(WindowsLocalization.T("system", "TcpRoute2 is already running.", "TcpRoute2 已经运行。", "TcpRoute2 已經執行。"), "TcpRoute2");
                return;
            }
        }
        catch (AbandonedMutexException) { }
        try { RunClient(args); }
        catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "TcpRoute2"); }
        finally { instanceLock.ReleaseMutex(); }
    }

    private static void RunClient(string[] args)
    {
        using var controller = new TcpRouteController();
        if (args.Contains("--start-wintun")) controller.SetUseWintun(true);
        if (controller.HasTunRecoveryState && !WintunNetwork.IsAdministrator)
        {
            try { TcpRouteController.RelaunchElevated("--recover-wintun"); }
            catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "TcpRoute2"); }
            return;
        }
        controller.Initialize();

        var application = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };
        var window = new LogWindow(controller);
        using var tray = new TrayIconHost(controller, window, application);
        if (args.Contains("--start-wintun"))
            application.Dispatcher.BeginInvoke(new Action(async () => await tray.StartAsync()));
        application.Run();
    }
}
