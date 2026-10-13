using System.IO;

namespace TcpRouteTray;

internal static class GeoDataManagerSelfTest
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"TcpRoute2-GeoDataSelfTest-{Guid.NewGuid():N}");
        try
        {
            GeoDataManager.EnsureInstalled(directory);
            var snapshot = GeoDataManager.Snapshot(directory, "daily", null, null);
            Expect(snapshot.Interval == "daily", "Geo data default interval was not retained");
            Expect(snapshot.GeositeBytes > 0 && snapshot.GeoipBytes > 0, "Bundled Geo data was not installed");
            Expect(File.ReadAllText(Path.Combine(directory, "geosite.dat.sha256")).Length == 64,
                "Geosite checksum marker is invalid");
            Expect(File.ReadAllText(Path.Combine(directory, "geoip.dat.sha256")).Length == 64,
                "GeoIP checksum marker is invalid");
            Expect(!GeoDataManager.IsDue(snapshot with { Interval = "off" }),
                "Disabled Geo data schedule was considered due");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
