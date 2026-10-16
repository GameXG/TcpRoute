using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace TcpRouteTray;

internal sealed record SubscriptionIdentity(string SourceId, string NodeId, string NodeName = "", bool Delete = false);
internal sealed record SubscriptionTomlResult(string Content, SubscriptionIdentity[] Selections);
internal sealed record SubscriptionTomlChange(string Path, string Before, string After);

internal static class SubscriptionTomlEditor
{
    public static async Task<SubscriptionTomlChange[]> PrepareSyncAsync(string executable, SubscriptionSource source, string activeProfile, IEnumerable<TcpRouteConfig> profiles, List<SubscriptionNode> nodes, List<SubscriptionNode>? previousNodes = null)
    {
        var changes = new List<SubscriptionTomlChange>();
        foreach (var profile in profiles)
        {
            if (profile.Id != activeProfile && !profile.Content.Contains("# TcpRoute2 subscription ", StringComparison.Ordinal)) continue;
            var snapshot = await EditAsync(executable, profile.Content, [], []).ConfigureAwait(false);
            if (profile.Id != activeProfile && !snapshot.Selections.Any(node => node.SourceId == source.Id)) continue;
            var ownership = snapshot.Selections.Select(node => node.SourceId == source.Id && node.NodeName == "" && previousNodes?.FirstOrDefault(old => old.Id == node.NodeId) is { } oldNode
                ? node with { NodeName = oldNode.Name } : node).ToArray();
            var plan = SubscriptionManager.PlanSync(source, profile.Id, nodes, ownership);
            var next = await EditAsync(executable, profile.Content, plan.Imports, plan.Removals).ConfigureAwait(false);
            if (next.Content != profile.Content) changes.Add(new(profile.Path, profile.Content, next.Content));
        }
        return changes.ToArray();
    }
    public static async Task<SubscriptionTomlChange[]> PrepareSourceRemovalAsync(string executable, IEnumerable<TcpRouteConfig> profiles, HashSet<string> sourceIds)
    {
        if (sourceIds.Count == 0) return [];
        var changes = new List<SubscriptionTomlChange>();
        foreach (var profile in profiles)
        {
            if (!profile.Content.Contains("# TcpRoute2 subscription ", StringComparison.Ordinal)) continue;
            var snapshot = await EditAsync(executable, profile.Content, [], []).ConfigureAwait(false);
            var removals = snapshot.Selections.Where(node => sourceIds.Contains(node.SourceId)).ToArray();
            if (removals.Length == 0) continue;
            var next = await EditAsync(executable, profile.Content, [], removals).ConfigureAwait(false);
            changes.Add(new(profile.Path, profile.Content, next.Content));
        }
        return changes.ToArray();
    }

    public static void ApplyChanges(IReadOnlyList<SubscriptionTomlChange> changes)
    {
        var applied = new List<SubscriptionTomlChange>();
        try {
            foreach (var change in changes) { WriteIfUnchanged(change.Path, change.Before, change.After); applied.Add(change); }
        }
        catch { RollbackChanges(applied); throw; }
    }

    public static void RollbackChanges(IEnumerable<SubscriptionTomlChange> changes)
    {
        foreach (var change in changes.Reverse())
            if (File.Exists(change.Path) && File.ReadAllText(change.Path) == change.After) WriteIfUnchanged(change.Path, change.After, change.Before);
    }

    public static async Task<SubscriptionTomlResult> EditAsync(string executable, string content, object[] imports, SubscriptionIdentity[] removals)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(executable) {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--subscription-toml");
        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Content = content, Imports = imports, Removals = removals }).AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var detail = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("Subscription TOML edit failed: " + detail.Trim());
            try { return JsonSerializer.Deserialize<SubscriptionTomlResult>(text) ?? throw new InvalidDataException("Core returned no subscription edit result."); }
            catch (JsonException) { throw new InvalidDataException("The selected core does not support subscription TOML editing. Use the matching TcpRoute2 core."); }
        }
        catch
        {
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); }
            throw;
        }
    }

    public static void WriteIfUnchanged(string path, string expected, string content)
    {
        Encoding encoding;
        using (var reader = new StreamReader(path, new UTF8Encoding(false), true))
        {
            if (reader.ReadToEnd() != expected) throw new IOException("The TOML file changed during subscription editing; retry the operation.");
            encoding = reader.CurrentEncoding;
        }
        var temporary = path + ".subscription-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, encoding);
            if (File.ReadAllText(path) != expected) throw new IOException("The TOML file changed during subscription editing; retry the operation.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
