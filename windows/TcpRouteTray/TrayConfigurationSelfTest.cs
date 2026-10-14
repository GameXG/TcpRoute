using System.IO;
using System.Text.Json;

namespace TcpRouteTray;

internal static class TrayConfigurationSelfTest
{
    public static void Run()
    {
        var changes = new ConfigurationChangeTracker("original");
        Expect(!changes.Observe("partial"), "A partial save reloaded immediately");
        Expect(!changes.Observe("complete"), "An unstable save was accepted");
        Expect(changes.Observe("complete"), "A stable save did not trigger reload");
        Expect(!changes.Observe("complete"), "An unchanged file reloaded repeatedly");
        changes.Accept("menu update");
        Expect(!changes.Observe("menu update"), "A client write triggered a reload loop");

        var directory = Path.Combine(Path.GetTempPath(), "TcpRoute2-TrayConfiguration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "TcpRoute2Client.json");
            var settings = new TraySettings {
                SelectedConfigId = "config.toml", UseWintun = true, EnableIpv6 = true,
                OutboundInterfaceId = Guid.NewGuid().ToString("D"), GeoDataLastError = "managed state",
                TunRecovery = new WintunNetworkState { Name = "TcpRoute-1234ABCD", InterfaceIndex = 7, InterfaceSelectionFallback = true }
            };
            settings.SaveUserConfiguration(path);
            var userJson = File.ReadAllText(path);
            using var json = JsonDocument.Parse(userJson);
            Expect(!json.RootElement.TryGetProperty("TunRecovery", out _), "Network recovery leaked into user configuration");
            var loaded = TraySettings.Load(path);
            loaded.ValidatePreferences();
            Expect(loaded.UseWintun && loaded.EnableIpv6 && loaded.OutboundInterfaceId == settings.OutboundInterfaceId, "Runtime preferences did not round-trip");
            loaded.CopyManagedStateFrom(settings);
            Expect(loaded.TunRecovery?.InterfaceIndex == 7, "Reload discarded network recovery");
            Expect(loaded.TunRecovery?.InterfaceSelectionFallback == true, "Reload discarded interface fallback state");
            Expect(loaded.UserConfigurationJson() == userJson, "Managed-state changes altered the configuration signature");
            loaded.OutboundInterfaceId = "invalid";
            try { loaded.ValidatePreferences(); throw new Exception("Invalid interface ID was accepted"); }
            catch (InvalidDataException) { }
            File.WriteAllText(path, "{invalid");
            try { _ = TraySettings.Load(path); throw new Exception("Invalid JSON was accepted"); }
            catch (InvalidDataException) { }
            Expect(File.ReadAllText(path) == "{invalid", "Invalid user configuration was overwritten");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
