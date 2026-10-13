using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TcpRouteTray;

internal static class WindowsTheme
{
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;

    private const int DwmSystemBackdropType = 38;
    private const int DwmMainWindow = 2;

    public static bool IsDark(string appearance)
    {
        if (appearance == "dark") return true;
        if (appearance == "light") return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value == 0;
    }

    public static void Apply(Window window, string appearance)
    {
        var dark = IsDark(appearance);
        SetBrush(window, "WindowBrush", dark ? "#A6151821" : "#B8F4F7FA");
        SetBrush(window, "NavBrush", dark ? "#B31B1F2A" : "#C7F7F9FC");
        SetBrush(window, "SurfaceBrush", dark ? "#D9202531" : "#E6FFFFFF");
        SetBrush(window, "RaisedBrush", dark ? "#E0282E3B" : "#E6E9EEF4");
        SetBrush(window, "HoverBrush", dark ? "#E8323949" : "#F2DDE5ED");
        SetBrush(window, "BorderBrush", dark ? "#52394153" : "#3D667383");
        SetBrush(window, "TextBrush", dark ? "#FFF2F4F8" : "#FF18202A");
        SetBrush(window, "MutedBrush", dark ? "#FFABB4C2" : "#FF536171");
        SetBrush(window, "InputBrush", dark ? "#E6171B24" : "#F2FFFFFF");
        SetBrush(window, "ToolTipBrush", dark ? "#F0303746" : "#FAFFFFFF");
        SetBrush(window, "AlternateBrush", dark ? "#D9232936" : "#E6F3F6F9");
        SetBrush(window, "HeaderBrush", dark ? "#E62B3240" : "#F0E5EBF1");
        SetBrush(window, "SelectionBrush", dark ? "#E6354052" : "#E6D5E8EC");
        SetBrush(window, "GridLineBrush", dark ? "#59343B4B" : "#40667383");
        SetBrush(window, "InfoBrush", dark ? "#D919212A" : "#E6E7F6F8");
        SetBrush(window, "InfoBorderBrush", dark ? "#8031505A" : "#8066A6AF");
        SetBrush(window, "InfoTextBrush", dark ? "#FF9CCBD2" : "#FF285F67");
        window.Background = System.Windows.Media.Brushes.Transparent;
        if (!OperatingSystem.IsWindows()) return;
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        var backdrop = DwmMainWindow;
        _ = DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, sizeof(int));
    }

    private static void SetBrush(Window window, string key, string color)
    {
        var converted = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        if (window.Resources[key] is SolidColorBrush brush)
        {
            if (brush.IsFrozen) window.Resources[key] = new SolidColorBrush(converted);
            else brush.Color = converted;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
