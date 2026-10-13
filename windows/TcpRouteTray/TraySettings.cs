using System.IO;
using System.Text.Json;

namespace TcpRouteTray;

internal sealed class TraySettings
{
    public string? SelectedConfigId { get; set; }

    public string? ExecutablePath { get; set; }

    public bool AllowLan { get; set; }

    public bool SystemProxyEnabled { get; set; }

    public bool AllowStun { get; set; }

    public bool AllowQuic { get; set; }

    public bool EnableIpv6 { get; set; }
    public bool UseWintun { get; set; }
    public string OutboundInterfaceId { get; set; } = "";
    public WintunNetworkState? TunRecovery { get; set; }
    public WindowsProxy.Snapshot? ProxyRecovery { get; set; }
    public int ProxyOwnedPort { get; set; }

    public string Appearance { get; set; } = "system";

    public string Language { get; set; } = "system";

    public string GeoDataInterval { get; set; } = "daily";

    public DateTimeOffset? GeoDataLastSuccess { get; set; }

    public string? GeoDataLastError { get; set; }

    public void CopyManagedStateFrom(TraySettings source)
    {
        TunRecovery = source.TunRecovery;
        ProxyRecovery = source.ProxyRecovery;
        ProxyOwnedPort = source.ProxyOwnedPort;
        GeoDataLastSuccess = source.GeoDataLastSuccess;
        GeoDataLastError = source.GeoDataLastError;
    }

    public void ValidatePreferences()
    {
        if (!string.IsNullOrEmpty(OutboundInterfaceId) && !Guid.TryParse(OutboundInterfaceId, out _))
            throw new InvalidDataException("OutboundInterfaceId must be empty for Automatic, or an interface GUID.");
        if (!string.IsNullOrEmpty(OutboundInterfaceId)) OutboundInterfaceId = Guid.Parse(OutboundInterfaceId).ToString("D");
        if (Appearance is not ("system" or "light" or "dark") || Language is not ("system" or "en" or "zh-Hans" or "zh-Hant") || GeoDataInterval is not ("off" or "daily" or "weekly"))
            throw new InvalidDataException("Invalid client appearance, language, or Geo data interval.");
    }

    public string UserConfigurationJson() => JsonSerializer.Serialize(new {
        SelectedConfigId, ExecutablePath, AllowLan, SystemProxyEnabled, AllowStun, AllowQuic,
        EnableIpv6, UseWintun, OutboundInterfaceId, Appearance, Language, GeoDataInterval
    }, new JsonSerializerOptions { WriteIndented = true });

    public void SaveUserConfiguration(string path) => SaveJson(path, UserConfigurationJson());

    public static TraySettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new TraySettings();
        }

        try
        {
            return JsonSerializer.Deserialize<TraySettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Client configuration must be a JSON object.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("TcpRoute2Client.json is invalid; the original file was retained.", error);
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        SaveJson(path, json);
    }

    private static void SaveJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
