using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TcpRouteTray;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SubscriptionOptions
{
    public bool? DnsResolve { get; set; }
    public int? Credit { get; set; }
    public int? Sleep { get; set; }
    public int? CorrectDelay { get; set; }
    public string? UpProxy { get; set; }
    public string? DialProxyFor { get; set; }

    public void Validate()
    {
        if (DialProxyFor is not null && (string.IsNullOrWhiteSpace(DialProxyFor) || DialProxyFor.Any(char.IsControl)))
            throw new InvalidDataException("Subscription DialProxyFor must name an existing upstream.");
        if (DialProxyFor is not null && UpProxy is not null) throw new InvalidDataException("Subscription DialProxyFor and UpProxy are mutually exclusive.");
        if (Sleep < 0 || CorrectDelay < 0) throw new InvalidDataException("Subscription delays must be nonnegative milliseconds.");
        if (UpProxy is not null && (string.IsNullOrWhiteSpace(UpProxy) || UpProxy.Any(char.IsWhiteSpace)))
            throw new InvalidDataException("Subscription UpProxy must be a proxy URL or upstream name without whitespace.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SubscriptionSource
{
    [JsonIgnore]
    public string ManagedPrefix => "sub_" + Regex.Replace(string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim(), @"[^\p{L}\p{Nd}_-]", "_") + "_";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public int UpdateIntervalMinutes { get; set; } = 0;
    public string[] SelectedNodes { get; set; } = [];
    public Dictionary<string, string[]> ExcludedNodesByProfile { get; set; } = new(StringComparer.Ordinal);
    public SubscriptionOptions Defaults { get; set; } = new() { Sleep = 80, Credit = 0, DnsResolve = false, CorrectDelay = 0 };
    public string[] LockedFields { get; set; } = [];
    public Dictionary<string, SubscriptionOptions> Overrides { get; set; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        if (!Regex.IsMatch(Id ?? "", "^[A-Za-z0-9_-]{1,48}$")) throw new InvalidDataException("Subscription Id must use 1-48 letters, digits, underscores or hyphens.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException($"Subscription {Id} needs an HTTP(S) URL.");
        if (UpdateIntervalMinutes < 0 || Defaults is null || SelectedNodes is null || LockedFields is null || Overrides is null || ExcludedNodesByProfile is null)
            throw new InvalidDataException($"Invalid subscription settings: {Id}.");
        if (UserAgent is null || UserAgent.Length > 512 || UserAgent.Any(char.IsControl)) throw new InvalidDataException($"Invalid UserAgent for subscription {Id}.");
        Defaults.Validate();
        if (SelectedNodes.Length > 2048 || SelectedNodes.Any(id => !ValidNodeId(id)) || SelectedNodes.Distinct(StringComparer.Ordinal).Count() != SelectedNodes.Length)
            throw new InvalidDataException($"Subscription {Id} needs unique selected node IDs (maximum 2048).");
        foreach (var (profile, excluded) in ExcludedNodesByProfile)
            if (Path.GetFileName(profile) != profile || !profile.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) || excluded is null || excluded.Length > 2048 || excluded.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)))
                throw new InvalidDataException($"Subscription {Id} contains invalid profile exclusions.");
        foreach (var field in LockedFields)
        {
            var property = typeof(SubscriptionOptions).GetProperty(field ?? "");
            if (property is null || property.GetValue(Defaults) is null)
                throw new InvalidDataException($"Subscription {Id}: locked field {field} needs an explicit default value.");
        }
        foreach (var (id, options) in Overrides)
        {
            if (!ValidNodeId(id) || options is null) throw new InvalidDataException($"Invalid subscription override in {Id}.");
            options.Validate();
        }
    }

    internal static bool ValidNodeId(string? id) => id is not null && Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,96}$");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SubscriptionNode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ProxyUrl { get; set; } = "";
    public SubscriptionOptions Options { get; set; } = new();
    public bool Removed { get; set; }
}

internal sealed class SubscriptionCache
{
    public DateTimeOffset UpdatedAt { get; set; }
    public List<SubscriptionNode> Nodes { get; set; } = [];
}

internal sealed class SubscriptionManager
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _directory;
    private readonly HttpClient _client;
    private readonly Dictionary<string, SubscriptionCache> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    public SubscriptionManager(string directory, HttpClient? client = null) { _directory = directory; _client = client ?? Client; }

    private string Key(SubscriptionSource source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Id + "\0" + source.Url))).ToLowerInvariant();

    public SubscriptionCache Load(SubscriptionSource source)
    {
        var key = Key(source);
        if (_loaded.TryGetValue(key, out var cached)) return cached;
        var path = Path.Combine(_directory, key + ".json");
        if (!File.Exists(path)) return new SubscriptionCache();
        try
        {
            if (new FileInfo(path).Length > MaxBytes * 2) throw new InvalidDataException("Subscription cache is too large.");
            cached = JsonSerializer.Deserialize<SubscriptionCache>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid subscription cache.");
            if (cached.Nodes.Count != 0) _ = ValidateNodes(cached.Nodes, requireUniqueNames: false);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or NullReferenceException) { return new SubscriptionCache(); }
        _loaded[key] = cached;
        return cached;
    }

    public bool IsDue(SubscriptionSource source)
    {
        if (source.UpdateIntervalMinutes == 0) return false;
        if (_retryAfter.TryGetValue(Key(source), out var retry) && retry > DateTimeOffset.UtcNow) return false;
        return DateTimeOffset.UtcNow - Load(source).UpdatedAt >= TimeSpan.FromMinutes(source.UpdateIntervalMinutes);
    }

    public async Task RefreshAsync(SubscriptionSource source, IReadOnlyCollection<SubscriptionIdentity>? imported = null, CancellationToken cancellationToken = default)
    {
        source.Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            if (!string.IsNullOrEmpty(source.UserAgent)) request.Headers.UserAgent.ParseAdd(source.UserAgent);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("Subscription exceeds 4 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (data.Length + length > MaxBytes) throw new InvalidDataException("Subscription exceeds 4 MiB.");
                data.Write(buffer, 0, length);
            }
            var nodes = Parse(new UTF8Encoding(false, true).GetString(data.ToArray()));
            ReconcileNodeNames(nodes, Load(source).Nodes, imported ?? []);
            var next = new SubscriptionCache { UpdatedAt = DateTimeOffset.UtcNow, Nodes = nodes };
            foreach (var node in nodes) _ = GenerateUpstream(source, node);
            Store(source, next);
            _retryAfter.Remove(Key(source));
        }
        catch (Exception error)
        {
            _retryAfter[Key(source)] = DateTimeOffset.UtcNow.AddMinutes(1);
            var reason = error is InvalidDataException ? " " + error.Message : "";
            throw new InvalidOperationException($"Subscription {source.Id} update failed; cached nodes retained.{reason}");
        }
    }

    internal static void ReconcileNodeNames(List<SubscriptionNode> incoming, List<SubscriptionNode> previous, IReadOnlyCollection<SubscriptionIdentity> imported)
    {
        var ownedIds = imported.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        var incomingNames = incoming.Select(node => node.Name).ToHashSet(StringComparer.Ordinal);
        if (previous.GroupBy(node => node.Name, StringComparer.Ordinal).Any(group => incomingNames.Contains(group.Key) && group.Count(node => ownedIds.Contains(node.Id)) > 1))
            throw new InvalidDataException("Subscription has ambiguous existing same-name nodes.");
        var oldNames = previous.GroupBy(node => node.Name, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => group.FirstOrDefault(node => ownedIds.Contains(node.Id)) ?? group.FirstOrDefault(node => !node.Removed) ?? group.First(), StringComparer.Ordinal);
        var ownedNames = imported.Where(node => !string.IsNullOrEmpty(node.NodeName)).GroupBy(node => node.NodeName, StringComparer.Ordinal).ToArray();
        if (ownedNames.Any(group => group.Count() > 1)) throw new InvalidDataException("Subscription has ambiguous imported node names.");
        var nameIds = oldNames.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.Ordinal);
        foreach (var group in ownedNames) nameIds[group.Key] = group.Single().NodeId;
        var reservedIds = previous.Select(node => node.Id).Concat(ownedIds).ToHashSet(StringComparer.Ordinal);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in incoming)
        {
            if (nameIds.TryGetValue(node.Name, out var id)) node.Id = id;
            else if (reservedIds.Contains(node.Id)) node.Id = NameId(node.Name);
            if (!usedIds.Add(node.Id)) throw new InvalidDataException("Subscription contains ambiguous node identities.");
        }
    }

    internal static (object[] Imports, SubscriptionIdentity[] Removals) PlanSync(SubscriptionSource source, string profile, List<SubscriptionNode> nodes, IReadOnlyCollection<SubscriptionIdentity> imported)
    {
        var local = nodes.Select(node => new SubscriptionNode { Id = node.Id, Name = node.Name, ProxyUrl = node.ProxyUrl, Options = node.Options }).ToList();
        ReconcileNodeNames(local, [], imported.Where(node => node.SourceId == source.Id).ToArray());
        var excluded = source.ExcludedNodesByProfile.GetValueOrDefault(profile, []).ToHashSet(StringComparer.Ordinal);
        var active = local.Where(node => !excluded.Contains(node.Id) && !excluded.Contains(node.Name)).ToArray();
        var activeIds = active.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var imports = active.Select(node => (object)new { SourceId = source.Id, NodeId = node.Id, NodeName = node.Name, Upstream = GenerateUpstream(source, node), source.LockedFields }).ToArray();
        var removals = imported.Where(node => node.SourceId == source.Id && !activeIds.Contains(node.NodeId)).ToArray();
        return (imports, removals);
    }

    private void Store(SubscriptionSource source, SubscriptionCache cache)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Key(source) + ".json");
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(cache)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        _loaded[Key(source)] = cache;
    }

    public void Restore(SubscriptionSource source, SubscriptionCache cache)
    {
        Store(source, cache);
        _retryAfter[Key(source)] = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    internal static object GenerateUpstream(SubscriptionSource source, SubscriptionNode node)
    {
        source.Overrides.TryGetValue(node.Id, out var local);
        T Resolve<T>(string field, T? fallback) where T : struct
        {
            var property = typeof(SubscriptionOptions).GetProperty(field)!;
            var baseline = (T?)property.GetValue(source.Defaults);
            if (source.LockedFields.Contains(field, StringComparer.Ordinal)) return baseline ?? throw new InvalidDataException("A locked subscription field has no default.");
            return (T?)property.GetValue(local ?? new SubscriptionOptions()) ?? (T?)property.GetValue(node.Options) ?? baseline ?? fallback!.Value;
        }
        var uri = new Uri(node.ProxyUrl);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var urlCredit = query.AllKeys.FirstOrDefault(key => string.Equals(key, "credit", StringComparison.OrdinalIgnoreCase));
        var nodeOptions = node.Options;
        if (urlCredit is not null && nodeOptions.Credit is null)
        {
            if (!int.TryParse(query[urlCredit], out var credit)) throw new InvalidDataException("Invalid subscription URL credit.");
            nodeOptions = new SubscriptionOptions { Credit = credit, DnsResolve = node.Options.DnsResolve, Sleep = node.Options.Sleep, CorrectDelay = node.Options.CorrectDelay, UpProxy = node.Options.UpProxy };
        }
        var finalCredit = source.LockedFields.Contains("Credit", StringComparer.Ordinal) ? source.Defaults.Credit!.Value : local?.Credit ?? nodeOptions.Credit ?? source.Defaults.Credit ?? 0;
        var parentKey = query.AllKeys.FirstOrDefault(key => string.Equals(key, "upProxy", StringComparison.OrdinalIgnoreCase));
        var parent = source.LockedFields.Contains("UpProxy", StringComparer.Ordinal) ? source.Defaults.UpProxy : local?.UpProxy ?? node.Options.UpProxy ?? (parentKey is null ? null : query[parentKey]) ?? source.Defaults.UpProxy;
        var dialProxyFor = source.LockedFields.Contains("DialProxyFor", StringComparer.Ordinal) ? source.Defaults.DialProxyFor : local?.DialProxyFor ?? node.Options.DialProxyFor ?? source.Defaults.DialProxyFor;
        if (dialProxyFor is not null && parent is not null) throw new InvalidDataException("Subscription DialProxyFor conflicts with upProxy.");
        var proxyUrl = node.ProxyUrl;
        if (parent is not null || urlCredit is not null)
        {
            if (parent is not null && uri.Scheme is not ("http" or "https" or "socks5" or "socks4" or "socks4a"))
                throw new InvalidDataException("Subscription UpProxy and URL credit rewriting require an HTTP/HTTPS/SOCKS URL.");
            if (urlCredit is not null) query.Remove(urlCredit);
            if (parentKey is not null) query.Remove(parentKey);
            if (parent is not null) query["upProxy"] = parent;
            var fragment = node.ProxyUrl.IndexOf('#');
            var end = fragment < 0 ? node.ProxyUrl.Length : fragment;
            var start = node.ProxyUrl.IndexOf('?', 0, end);
            var prefix = node.ProxyUrl[..(start < 0 ? end : start)];
            var suffix = fragment < 0 ? "" : node.ProxyUrl[fragment..];
            var encoded = query.ToString();
            proxyUrl = prefix + (string.IsNullOrEmpty(encoded) ? "" : "?" + encoded) + suffix;
        }
        return new {
            Name = source.ManagedPrefix + node.Id,
            ProxyUrl = proxyUrl,
            DialProxyFor = dialProxyFor ?? "",
            DnsResolve = Resolve<bool>("DnsResolve", false), Credit = finalCredit,
            Sleep = Resolve<int>("Sleep", 0), CorrectDelay = Resolve<int>("CorrectDelay", 0)
        };
    }

    public static List<SubscriptionNode> Parse(string content)
    {
        var text = content.Trim().TrimStart('\uFEFF').Trim();
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) throw new InvalidDataException("Subscription exceeds 4 MiB.");
        if (LooksLikeSurge(text)) return ValidateNodes(ParseSurge(text));
        if (LooksLikeClash(text)) return ValidateNodes(ParseClash(text));
        if (!text.StartsWith('[') && !text.Contains("://", StringComparison.Ordinal))
        {
            try { text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(PadBase64(text))); }
            catch (Exception error) when (error is FormatException or DecoderFallbackException) { throw new InvalidDataException("Subscription must be a URI list, Base64 URI list, or JSON node array."); }
        }
        List<SubscriptionNode> nodes;
        var jsonArray = text.TrimStart().StartsWith('[');
        if (jsonArray)
            nodes = JsonSerializer.Deserialize<List<SubscriptionNode>>(text) ?? throw new InvalidDataException("Empty JSON subscription.");
        else
        {
            nodes = [];
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                var url = line.Trim();
                if (url.Length == 0 || url.StartsWith('#')) continue;
                var fragment = url.IndexOf('#');
                var name = fragment < 0 ? "" : Uri.UnescapeDataString(url[(fragment + 1)..]);
                nodes.Add(new SubscriptionNode { Name = name, ProxyUrl = fragment < 0 ? url : url[..fragment] });
            }
        }
        if (!jsonArray && nodes.Count == 0) throw new InvalidDataException("Subscription response contains no nodes; use an explicit JSON [] to remove all nodes.");
        var validated = ValidateNodes(nodes);
        foreach (var node in validated) node.Removed = false;
        return validated;
    }

    private static bool LooksLikeClash(string text) =>
        Regex.IsMatch(text, "(?m)^\\s*proxies\\s*:", RegexOptions.IgnoreCase);

    private static bool LooksLikeSurge(string text) =>
        Regex.IsMatch(text, "(?m)^\\s*\\[Proxy(?:\\s+Group)?\\]\\s*$", RegexOptions.IgnoreCase);

    private static List<SubscriptionNode> ParseClash(string text)
    {
        try
        {
            var yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
            var document = yaml.Deserialize<Dictionary<string, object>>(text);
            if (!document.TryGetValue("proxies", out var raw) || raw is not IEnumerable<object> items)
                throw new InvalidDataException("Clash configuration has no proxies list.");
            var nodes = new List<SubscriptionNode>();
            foreach (var item in items)
            {
                if (item is not IDictionary<object, object> map) continue;
                var values = map.ToDictionary(pair => pair.Key?.ToString() ?? "", pair => pair.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
                var name = values.GetValueOrDefault("name", "");
                var type = values.GetValueOrDefault("type", "").ToLowerInvariant();
                var server = values.GetValueOrDefault("server", "");
                var port = values.GetValueOrDefault("port", "");
                var url = ClashProxyUrl(type, server, port, values);
                nodes.Add(new SubscriptionNode { Name = name, ProxyUrl = url });
            }
            return nodes;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) { throw new InvalidDataException($"Invalid Clash YAML subscription: {error.Message}"); }
    }

    private static string ClashProxyUrl(string type, string server, string port, IReadOnlyDictionary<string, string> values)
    {
        if (server == "" || port == "") throw new InvalidDataException("Clash proxy is missing server or port.");
        var user = values.GetValueOrDefault("username", "");
        var password = values.GetValueOrDefault("password", "");
        var auth = user == "" ? "" : Uri.EscapeDataString(user) + (password == "" ? "" : ":" + Uri.EscapeDataString(password)) + "@";
        return type switch
        {
            "http" or "https" or "socks5" or "socks4" or "socks4a" => $"{type}://{auth}{server}:{port}",
            "ss" or "shadowsocks" => $"ss://{Uri.EscapeDataString(values.GetValueOrDefault("cipher", "aes-128-gcm"))}:{Uri.EscapeDataString(password)}@{server}:{port}",
            "trojan" => $"trojan://{Uri.EscapeDataString(password)}@{server}:{port}?security=tls&sni={Uri.EscapeDataString(values.GetValueOrDefault("sni", server))}",
            "vmess" => VmessUrl(values),
            _ => throw new InvalidDataException($"Unsupported Clash proxy type: {type}")
        };
    }

    private static string VmessUrl(IReadOnlyDictionary<string, string> values)
    {
        var payload = JsonSerializer.Serialize(new { v = "2", ps = values.GetValueOrDefault("name", "vmess"), add = values["server"], port = values["port"], id = values.GetValueOrDefault("uuid", ""), aid = values.GetValueOrDefault("alterId", "0"), net = values.GetValueOrDefault("network", "tcp"), tls = values.GetValueOrDefault("tls", "") == "true" ? "tls" : "" });
        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    }

    private static List<SubscriptionNode> ParseSurge(string text)
    {
        var nodes = new List<SubscriptionNode>();
        var inProxy = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inProxy = line.Equals("[Proxy]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inProxy || line == "" || line.StartsWith('#') || !line.Contains('=')) continue;
            var parts = line.Split('=', 2);
            var name = parts[0].Trim();
            var fields = SplitSurgeFields(parts[1]);
            if (fields.Count < 3) throw new InvalidDataException($"Invalid Surge proxy entry: {name}");
            var type = fields[0].ToLowerInvariant();
            var server = fields[1];
            var port = fields[2];
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fields.Count >= 5 && !fields[3].Contains('=') && !fields[4].Contains('=') &&
                (type is "http" or "https" or "socks5" or "socks" or "ss" or "shadowsocks"))
            {
                values["username"] = fields[3];
                values["password"] = fields[4];
                for (var i = 5; i + 1 < fields.Count; i += 2) values[fields[i]] = fields[i + 1];
            }
            else
            {
                for (var i = 3; i + 1 < fields.Count; i += 2) values[fields[i]] = fields[i + 1];
            }
            values["name"] = name;
            values["username"] = values.GetValueOrDefault("username", "");
            values["password"] = values.GetValueOrDefault("password", "");
            nodes.Add(new SubscriptionNode { Name = name, ProxyUrl = ClashProxyUrl(type, server, port, values) });
        }
        if (nodes.Count == 0) throw new InvalidDataException("Surge configuration has no [Proxy] entries.");
        return nodes;
    }

    private static List<string> SplitSurgeFields(string value) =>
        value.Split(',').Select(field => field.Trim().Trim('"')).Where(field => field != "").ToList();

    private static List<SubscriptionNode> ValidateNodes(List<SubscriptionNode> nodes, bool requireUniqueNames = true)
    {
        if (nodes is null || nodes.Count > 2048) throw new InvalidDataException("Subscription may contain at most 2048 nodes.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (node is null || node.Options is null || node.Name is null || node.ProxyUrl is null || EditableTcpRouteConfigCodec.ProxyUrlError(node.ProxyUrl) is not null || node.ProxyUrl.StartsWith("direct:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Subscription contains an invalid or unsupported proxy URL.");
            node.Options.Validate();
            var fragment = node.ProxyUrl.IndexOf('#');
            var identity = fragment < 0 ? node.ProxyUrl : node.ProxyUrl[..fragment];
            if (node.Name == "" && node.ProxyUrl.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
            {
                try {
                    using var vmess = JsonDocument.Parse(Convert.FromBase64String(PadBase64(node.ProxyUrl[8..].Split('#')[0])));
                    if (vmess.RootElement.TryGetProperty("ps", out var label) && label.ValueKind == JsonValueKind.String) node.Name = label.GetString() ?? "";
                } catch (Exception error) when (error is JsonException or FormatException) { }
            }
            node.Name = new string(node.Name.Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
            if (node.Id == "") node.Id = node.Name == "" ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24] : NameId(node.Name);
            if (!SubscriptionSource.ValidNodeId(node.Id) || !ids.Add(node.Id)) throw new InvalidDataException("Subscription contains an invalid or duplicate node ID.");
            if (node.Name == "") node.Name = new Uri(node.ProxyUrl).Scheme + " " + node.Id[..Math.Min(8, node.Id.Length)];
            if (requireUniqueNames && !names.Add(node.Name)) throw new InvalidDataException("Subscription contains duplicate node names.");
        }
        return nodes;
    }

    private static string NameId(string name) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("name:" + name))).ToLowerInvariant()[..24];

    private static string PadBase64(string value)
    {
        var result = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
        return result + new string('=', (4 - result.Length % 4) % 4);
    }
}
