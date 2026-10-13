using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TcpRouteTray;

internal static class WindowsProxy
{
    internal sealed record Snapshot(bool HadEnable, int Enable, bool HadServer, string? Server, RegistryValueKind ServerKind = RegistryValueKind.String);

    [SupportedOSPlatform("windows")]
    public static Snapshot Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey);
        var server = key?.GetValue("ProxyServer", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (server is not null && server is not string) throw new InvalidOperationException("Unsupported Windows proxy format; settings were retained.");
        return new Snapshot(key?.GetValue("ProxyEnable") is not null, Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0),
            server is not null, server as string,
            server is not null ? key!.GetValueKind("ProxyServer") : RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    public static void Restore(Snapshot snapshot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException("Cannot restore Windows proxy settings.");
        if (snapshot.HadEnable) key.SetValue("ProxyEnable", snapshot.Enable, RegistryValueKind.DWord);
        else key.DeleteValue("ProxyEnable", throwOnMissingValue: false);
        if (snapshot.HadServer) key.SetValue("ProxyServer", snapshot.Server ?? "", snapshot.ServerKind);
        else key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        NotifySettingsChanged();
    }
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    public static string ServerValue(int port) => $"http=127.0.0.1:{port};https=127.0.0.1:{port};socks=127.0.0.1:{port}";

    [SupportedOSPlatform("windows")]
    public static void SetSocks(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", ServerValue(port), RegistryValueKind.String);
        NotifySettingsChanged();
    }

    [SupportedOSPlatform("windows")]
    public static void Clear()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open Windows Internet Settings registry key.");

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifySettingsChanged();
    }

    private static void NotifySettingsChanged()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);
}
