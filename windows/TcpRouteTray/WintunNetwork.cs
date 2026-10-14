using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace TcpRouteTray;

internal sealed record WintunNetworkState
{
    public string Name { get; init; } = "";
    public int InterfaceIndex { get; init; }
    public string WeakHostSend4 { get; init; } = "Disabled";
    public string WeakHostSend6 { get; init; } = "Disabled";
    public bool EnableIpv6 { get; init; }
    public bool InterfaceSelectionFallback { get; init; }
    public string[] DnsServers { get; init; } = [];
    public string[] Addresses { get; init; } = [];
}

internal static class WintunNetwork
{
    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string AdapterName(string root) => "TcpRoute-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..8];

    public static IReadOnlyList<(string Id, string Name)> AvailableInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
        .Where(adapter => Guid.TryParse(adapter.Id, out _))
        .Select(adapter => (Id: Guid.Parse(adapter.Id).ToString("D"), Name: adapter.Name))
        .OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    [SupportedOSPlatform("windows")]
    public static async Task<WintunNetworkState> CaptureAsync(string root, bool ipv6, string interfaceId = "")
    {
        if (!IsAdministrator) throw new InvalidOperationException("Wintun requires administrator privileges.");
        var name = AdapterName(root);
        var requestedInterface = string.IsNullOrEmpty(interfaceId) ? "" : Guid.Parse(interfaceId).ToString("D");
        var script = $$$"""
        $ErrorActionPreference = 'Stop'
        if (Get-NetAdapter -Name '{{{name}}}' -ErrorAction SilentlyContinue) { throw 'The TcpRoute2 Wintun adapter is already in use.' }
        $routes = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Where-Object { $_.NextHop -ne '0.0.0.0' -and (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).ConnectionState -eq 'Connected' } | Sort-Object @{Expression={$_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric}})
        $interfaceSelectionFallback = $false
        if ('{{{requestedInterface}}}' -ne '') {
            $selected = @(Get-NetAdapter | Where-Object { $_.InterfaceGuid -eq [guid]'{{{requestedInterface}}}' -and $_.Status -eq 'Up' } | Select-Object -First 1)
            if ($selected.Count -eq 0) {
                $interfaceSelectionFallback = $true
            } else {
                $selectedRoutes = @($routes | Where-Object { $_.InterfaceIndex -eq $selected[0].ifIndex })
                if ($selectedRoutes.Count -eq 0) { $interfaceSelectionFallback = $true } else { $routes = $selectedRoutes }
            }
        }
        if ($routes.Count -eq 0) { throw 'No physical IPv4 default route is available.' }
        $index = $routes[0].InterfaceIndex
        $v4 = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4
        $v6 = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv6 -ErrorAction SilentlyContinue
        if (-not $v6) { $v6 = [pscustomobject]@{WeakHostSend='Disabled';Forwarding='Disabled'} }
        if ($v4.Forwarding -eq 'Enabled' -or $v6.Forwarding -eq 'Enabled') { throw 'Disable Internet Connection Sharing / forwarding on the physical interface before using Wintun.' }
        if ({{{(ipv6 ? "$true" : "$false")}}} -and -not (Get-NetRoute -InterfaceIndex $index -AddressFamily IPv6 -DestinationPrefix '::/0' -ErrorAction SilentlyContinue)) { throw 'The selected physical interface has no IPv6 default route.' }
        $servers = @((Get-DnsClientServerAddress -InterfaceIndex $index).ServerAddresses | Where-Object {
            $ip = [System.Net.IPAddress]::Parse($_)
            -not [System.Net.IPAddress]::IsLoopback($ip) -and $_ -ne '198.18.0.1' -and ({{{(ipv6 ? "$true" : "$false")}}} -or $ip.AddressFamily -eq 'InterNetwork')
        })
        if ($servers.Count -eq 0) { throw 'The physical interface has no usable DNS server.' }
        [ordered]@{Name='{{{name}}}'; InterfaceIndex=$index; WeakHostSend4=[string]$v4.WeakHostSend; WeakHostSend6=[string]$v6.WeakHostSend; EnableIpv6={{{(ipv6 ? "$true" : "$false")}}}; InterfaceSelectionFallback=$interfaceSelectionFallback; DnsServers=$servers; Addresses=@((Get-NetIPAddress -InterfaceIndex $index).IPAddress)} | ConvertTo-Json -Depth 4 -Compress
        """;
        var json = await RunAsync(script).ConfigureAwait(false);
        return JsonSerializer.Deserialize<WintunNetworkState>(json) ?? throw new InvalidOperationException("Could not read physical interface state.");
    }

    public static async Task ApplyAsync(WintunNetworkState state)
    {
        Validate(state);
        await RunAsync($$$"""
        $ErrorActionPreference = 'Stop'
        $adapter = Get-NetAdapter -Name '{{{state.Name}}}'
        $index = $adapter.ifIndex
        $tun4 = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv4 -ErrorAction SilentlyContinue
        $tun6 = Get-NetIPInterface -InterfaceIndex $index -AddressFamily IPv6 -ErrorAction SilentlyContinue
        if (-not $tun4) { throw 'The Wintun adapter has no IPv4 interface. Check the Windows IPv4 binding.' }
        if ({{{(state.EnableIpv6 ? "$true" : "$false")}}} -and -not $tun6) { throw 'IPv6 is enabled in TcpRoute2, but the Wintun adapter has no IPv6 interface. Check the Windows IPv6 binding.' }
        foreach ($family in @('IPv4','IPv6')) {
            if ($family -eq 'IPv6' -and -not $tun6) { continue }
            if (Get-NetIPInterface -InterfaceIndex {{{state.InterfaceIndex}}} -AddressFamily $family -ErrorAction SilentlyContinue) {
                Set-NetIPInterface -InterfaceIndex {{{state.InterfaceIndex}}} -AddressFamily $family -WeakHostSend Disabled
            }
            Set-NetIPInterface -InterfaceIndex $index -AddressFamily $family -NlMtuBytes 1500 -AutomaticMetric Disabled -InterfaceMetric 5
        }
        New-NetIPAddress -InterfaceIndex $index -IPAddress '198.18.0.2' -PrefixLength 30 | Out-Null
        if ($tun6) { New-NetIPAddress -InterfaceIndex $index -IPAddress 'fd73:7463:7072::2' -PrefixLength 126 | Out-Null }
        Set-DnsClient -InterfaceIndex $index -RegisterThisConnectionsAddress $false
        Set-DnsClientServerAddress -InterfaceIndex $index -ServerAddresses '198.18.0.1'
        foreach ($prefix in @('0.0.0.0/1','128.0.0.0/1')) { New-NetRoute -InterfaceIndex $index -DestinationPrefix $prefix -NextHop '0.0.0.0' -RouteMetric 5 -PolicyStore ActiveStore | Out-Null }
        if ($tun6) {
            foreach ($prefix in @('::/1','8000::/1')) { New-NetRoute -InterfaceIndex $index -DestinationPrefix $prefix -NextHop '::' -RouteMetric 5 -PolicyStore ActiveStore | Out-Null }
        }
        Clear-DnsClientCache
        """).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    public static async Task RestoreAsync(WintunNetworkState state)
    {
        if (!IsAdministrator) throw new InvalidOperationException("Administrator privileges are required to recover Wintun.");
        Validate(state);
        await RunAsync($$$"""
        $ErrorActionPreference = 'Stop'
        $adapter = Get-NetAdapter -Name '{{{state.Name}}}' -ErrorAction SilentlyContinue
        if ($adapter) {
            $owned = @('0.0.0.0/1','128.0.0.0/1','::/1','8000::/1')
            Get-NetRoute -InterfaceIndex $adapter.ifIndex -ErrorAction SilentlyContinue | Where-Object { $_.DestinationPrefix -in $owned -and $_.RouteMetric -eq 5 } | Remove-NetRoute -Confirm:$false
            if (Get-NetAdapter -Name '{{{state.Name}}}' -ErrorAction SilentlyContinue) { Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ResetServerAddresses }
        }
        foreach ($entry in @(@{Family='IPv4';Original='{{{state.WeakHostSend4}}}'}, @{Family='IPv6';Original='{{{state.WeakHostSend6}}}'})) {
            $interface = Get-NetIPInterface -InterfaceIndex {{{state.InterfaceIndex}}} -AddressFamily $entry.Family -ErrorAction SilentlyContinue
            if ($interface -and $entry.Original -eq 'Enabled' -and $interface.WeakHostSend -eq 'Disabled') {
                Set-NetIPInterface -InterfaceIndex {{{state.InterfaceIndex}}} -AddressFamily $entry.Family -WeakHostSend Enabled
            }
        }
        Clear-DnsClientCache
        """).ConfigureAwait(false);
    }

    public static bool PhysicalInterfaceIsAvailable(WintunNetworkState state) => NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
        adapter.Supports(NetworkInterfaceComponent.IPv4) && adapter.OperationalStatus == OperationalStatus.Up && adapter.GetIPProperties().GetIPv4Properties()?.Index == state.InterfaceIndex &&
        new HashSet<string>(adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString().Split('%')[0]), StringComparer.OrdinalIgnoreCase)
            .SetEquals(state.Addresses));

    private static void Validate(WintunNetworkState state)
    {
        if (state.InterfaceIndex <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(state.Name, "^TcpRoute-[A-F0-9]{8}$") ||
            state.WeakHostSend4 is not ("Enabled" or "Disabled") || state.WeakHostSend6 is not ("Enabled" or "Disabled"))
            throw new InvalidOperationException("Invalid saved Wintun interface state.");
    }

    private static async Task<string> RunAsync(string script)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-OutputFormat"); start.ArgumentList.Add("Text"); start.ArgumentList.Add("-EncodedCommand");
        var utf8Script = """
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            $ProgressPreference = 'SilentlyContinue'
            $ErrorActionPreference = 'Stop'
            try {
            """ + "\n" + script + "\n" + """
            } catch {
                [Console]::Error.WriteLine(($_ | Out-String).Trim())
                exit 1
            }
            """;
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(utf8Script)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows network configuration.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); throw; }
        var text = await output.ConfigureAwait(false); var error = CleanPowerShellError(await errors.ConfigureAwait(false));
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows network configuration failed: " + error);
        return text.Trim();
    }

    private static string CleanPowerShellError(string error)
    {
        var text = error.Trim();
        var clixml = text.IndexOf("<Objs", StringComparison.OrdinalIgnoreCase);
        if (clixml >= 0) text = text[..clixml].Trim();
        return string.IsNullOrWhiteSpace(text) ? "PowerShell network configuration failed." : text;
    }
}
