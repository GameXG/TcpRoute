using System.Globalization;

namespace TcpRouteTray;

internal static class WindowsLocalization
{
    public static string Resolve(string preference)
    {
        if (preference is "en" or "zh-Hans" or "zh-Hant") return preference;
        var name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
        return name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en";
    }

    public static string T(string preference, string english, string simplified, string traditional)
        => Resolve(preference) switch
        {
            "zh-Hans" => simplified,
            "zh-Hant" => traditional,
            _ => english
        };
}
