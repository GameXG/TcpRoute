using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TcpRouteTray;

internal sealed record GeoDataSnapshot(
    string Interval,
    DateTimeOffset? LastSuccess,
    string? LastError,
    long GeositeBytes,
    long GeoipBytes);

internal static class GeoDataManager
{
    private static readonly Uri ReleaseBase = new("https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private static readonly Regex ChecksumPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] FileNames = ["geosite.dat", "geoip.dat"];

    public static void EnsureInstalled(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in FileNames)
        {
            var destination = Path.Combine(directory, name);
            if (File.Exists(destination)) continue;
            var source = FindBundledFile(name);
            File.Copy(source, destination);
            File.WriteAllText(Path.Combine(directory, $"{name}.sha256"), Sha256(File.ReadAllBytes(destination)));
        }
    }

    public static GeoDataSnapshot Snapshot(
        string directory,
        string interval,
        DateTimeOffset? lastSuccess,
        string? lastError)
    {
        DateTimeOffset? fallback = FileNames
            .Select(name => Path.Combine(directory, name))
            .Where(File.Exists)
            .Select(path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))
            .Order()
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();
        return new GeoDataSnapshot(
            NormalizeInterval(interval),
            lastSuccess ?? fallback,
            lastError,
            FileSize(Path.Combine(directory, "geosite.dat")),
            FileSize(Path.Combine(directory, "geoip.dat")));
    }

    public static bool IsDue(GeoDataSnapshot snapshot)
    {
        var period = snapshot.Interval switch
        {
            "daily" => TimeSpan.FromDays(1),
            "weekly" => TimeSpan.FromDays(7),
            _ => (TimeSpan?)null
        };
        return period is not null &&
               (snapshot.LastSuccess is null || DateTimeOffset.UtcNow - snapshot.LastSuccess >= period);
    }

    public static string NormalizeInterval(string? value) => value is "off" or "weekly" ? value : "daily";

    public static async Task UpdateAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!await UpdateLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("Geo data update is already running.");
        try
        {
            EnsureInstalled(directory);
            var limits = new Dictionary<string, int>
            {
                ["geosite.dat"] = 32 * 1024 * 1024,
                ["geoip.dat"] = 64 * 1024 * 1024
            };
            var downloads = new Dictionary<string, byte[]>();
            var checksums = new Dictionary<string, string>();
            foreach (var name in FileNames)
            {
                var checksumBytes = await DownloadAsync($"{name}.sha256sum", 4 * 1024, cancellationToken);
                var checksum = System.Text.Encoding.UTF8.GetString(checksumBytes)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.ToLowerInvariant() ?? "";
                if (!ChecksumPattern.IsMatch(checksum)) throw new InvalidDataException($"Invalid checksum for {name}.");
                var data = await DownloadAsync(name, limits[name], cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(checksum), SHA256.HashData(data)))
                    throw new InvalidDataException($"Checksum mismatch for {name}.");
                downloads[name] = data;
                checksums[name] = checksum;
            }
            ReplaceTogether(directory, downloads, checksums);
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    private static async Task<byte[]> DownloadAsync(string name, int maximumBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ReleaseBase, name));
        request.Headers.UserAgent.ParseAdd("TcpRoute2-Windows");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Geo data redirect must use HTTPS.");
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException($"Geo data response is too large: {name}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (output.Length + count > maximumBytes)
                throw new InvalidDataException($"Geo data response is too large: {name}.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static void ReplaceTogether(
        string directory,
        IReadOnlyDictionary<string, byte[]> downloads,
        IReadOnlyDictionary<string, string> checksums)
    {
        var backups = new List<(string Destination, string Backup)>();
        var installed = new List<string>();
        try
        {
            foreach (var name in FileNames)
            {
                foreach (var suffix in new[] { "", ".sha256" })
                {
                    var destination = Path.Combine(directory, name + suffix);
                    var backup = destination + ".bak";
                    File.Delete(backup);
                    if (!File.Exists(destination)) continue;
                    File.Move(destination, backup);
                    backups.Add((destination, backup));
                }
            }
            foreach (var name in FileNames)
            {
                var destination = Path.Combine(directory, name);
                File.WriteAllBytes(destination, downloads[name]);
                installed.Add(destination);
                var marker = Path.Combine(directory, $"{name}.sha256");
                File.WriteAllText(marker, checksums[name]);
                installed.Add(marker);
            }
            foreach (var item in backups) File.Delete(item.Backup);
        }
        catch
        {
            foreach (var path in installed) File.Delete(path);
            foreach (var item in backups.AsEnumerable().Reverse()) File.Move(item.Backup, item.Destination);
            throw;
        }
    }

    private static string FindBundledFile(string name)
    {
        foreach (var directory in EnumerateAncestors(AppContext.BaseDirectory).Take(8))
        {
            foreach (var candidate in new[] { Path.Combine(directory, name), Path.Combine(directory, "assets", name) })
                if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Bundled Geo data is missing: {name}");
    }

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start)); directory is not null; directory = directory.Parent)
            yield return directory.FullName;
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
