using System.Diagnostics;
using System.Text;

namespace TcpRouteTray;

internal static class WintunNetworkSelfTest
{
    public static void Run()
    {
        var executable = Environment.GetEnvironmentVariable("TCPROUTE_TEST_POWERSHELL");
        if (string.IsNullOrEmpty(executable)) return;
        foreach (var (preexisting, ipv6) in new[] { (false, true), (true, true), (false, false) })
        {
            var state = new WintunNetworkState { Name = "TcpRoute-1234ABCD", InterfaceIndex = 7, EnableIpv6 = ipv6 };
            var apply = WintunNetwork.BuildApplyScript(state);
            var setup = $$$"""
                $ErrorActionPreference = 'Stop'
                $global:Addresses = @{}
                $global:Routes = @{}
                $global:NewAddressCount = 0
                $global:NewRouteCount = 0
                $global:TunHasIpv6 = {{{(ipv6 ? "$true" : "$false")}}}
                function Get-NetAdapter { [pscustomobject]@{ifIndex=999} }
                function Get-NetIPInterface {
                    param($InterfaceIndex, $AddressFamily, $ErrorAction)
                    if ($InterfaceIndex -eq 999 -and $AddressFamily -eq 'IPv6' -and -not $global:TunHasIpv6) { return }
                    [pscustomobject]@{WeakHostSend='Disabled'}
                }
                function Set-NetIPInterface { }
                function Get-NetIPAddress {
                    param($InterfaceIndex, $IPAddress, $PolicyStore, $ErrorAction)
                    $key = "$InterfaceIndex|$IPAddress"
                    if ($global:Addresses.ContainsKey($key)) { $global:Addresses[$key] }
                }
                function New-NetIPAddress {
                    param($InterfaceIndex, $IPAddress, $PrefixLength, $SkipAsSource, $PolicyStore)
                    $key = "$InterfaceIndex|$IPAddress"
                    if ($global:Addresses.ContainsKey($key)) { throw 'Instance MSFT_NetIPAddress already exists' }
                    if ($InterfaceIndex -ne 999 -or $PolicyStore -ne 'ActiveStore') { throw 'Address creation escaped the owned active interface' }
                    $global:Addresses[$key] = [pscustomobject]@{PrefixLength=$PrefixLength;SkipAsSource=$SkipAsSource}
                    $global:NewAddressCount++
                }
                function Set-NetIPAddress {
                    param($InterfaceIndex, $IPAddress, $PrefixLength, $SkipAsSource, $PolicyStore)
                    if ($InterfaceIndex -ne 999 -or $PolicyStore -ne 'ActiveStore') { throw 'Address update escaped the owned active interface' }
                    $global:Addresses["$InterfaceIndex|$IPAddress"].PrefixLength = $PrefixLength
                    $global:Addresses["$InterfaceIndex|$IPAddress"].SkipAsSource = $SkipAsSource
                }
                function Get-NetRoute {
                    param($InterfaceIndex, $DestinationPrefix, $NextHop, $PolicyStore, $ErrorAction)
                    $key = "$InterfaceIndex|$DestinationPrefix|$NextHop"
                    if ($global:Routes.ContainsKey($key)) { $global:Routes[$key] }
                }
                function New-NetRoute {
                    param($InterfaceIndex, $DestinationPrefix, $NextHop, $RouteMetric, $PolicyStore)
                    $key = "$InterfaceIndex|$DestinationPrefix|$NextHop"
                    if ($global:Routes.ContainsKey($key)) { throw 'Route already exists' }
                    if ($InterfaceIndex -ne 999 -or $PolicyStore -ne 'ActiveStore') { throw 'Route creation escaped the owned active interface' }
                    $global:Routes[$key] = [pscustomobject]@{RouteMetric=$RouteMetric}
                    $global:NewRouteCount++
                }
                function Set-NetRoute {
                    param($InterfaceIndex, $DestinationPrefix, $NextHop, $RouteMetric, $PolicyStore)
                    if ($InterfaceIndex -ne 999 -or $PolicyStore -ne 'ActiveStore') { throw 'Route update escaped the owned active interface' }
                    $global:Routes["$InterfaceIndex|$DestinationPrefix|$NextHop"].RouteMetric = $RouteMetric
                }
                function Set-DnsClient { }
                function Set-DnsClientServerAddress { }
                function Clear-DnsClientCache { }
                $global:Addresses['77|192.0.2.1'] = [pscustomobject]@{PrefixLength=24;SkipAsSource=$true}
                $global:Routes['77|0.0.0.0/1|0.0.0.0'] = [pscustomobject]@{RouteMetric=99}
                if ({{{(preexisting ? "$true" : "$false")}}}) {
                    $global:Addresses['999|198.18.0.2'] = [pscustomobject]@{PrefixLength=24;SkipAsSource=$true}
                    $global:Addresses['999|fd73:7463:7072::2'] = [pscustomobject]@{PrefixLength=64;SkipAsSource=$true}
                    foreach ($prefix in @('0.0.0.0/1','128.0.0.0/1')) { $global:Routes["999|$prefix|0.0.0.0"] = [pscustomobject]@{RouteMetric=99} }
                    foreach ($prefix in @('::/1','8000::/1')) { $global:Routes["999|$prefix|::"] = [pscustomobject]@{RouteMetric=99} }
                }
                """;
            var assertions = $$$"""
                if ($global:Addresses['999|198.18.0.2'].PrefixLength -ne 30 -or $global:Addresses['999|198.18.0.2'].SkipAsSource) { throw 'IPv4 address was not reconciled' }
                if ($global:TunHasIpv6 -and $global:Addresses['999|fd73:7463:7072::2'].PrefixLength -ne 126) { throw 'IPv6 address was not reconciled' }
                if ($global:Routes['999|0.0.0.0/1|0.0.0.0'].RouteMetric -ne 5) { throw 'Route metric was not reconciled' }
                if ($global:NewAddressCount -ne {{{(preexisting ? 0 : ipv6 ? 2 : 1)}}} -or $global:NewRouteCount -ne {{{(preexisting ? 0 : ipv6 ? 4 : 2)}}}) { throw 'Repeated application created duplicate entries' }
                if ($global:Addresses['77|192.0.2.1'].PrefixLength -ne 24 -or $global:Routes['77|0.0.0.0/1|0.0.0.0'].RouteMetric -ne 99) { throw 'Unrelated interface state changed' }
                'PASS Wintun repeated configuration'
                """;
            var script = setup + "\n" + apply + "\n" + apply + "\n" + assertions;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(start) ?? throw new Exception("Could not start PowerShell regression test.");
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("PowerShell regression test timed out."); }
            var text = output.GetAwaiter().GetResult(); var error = errors.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !text.Contains("PASS Wintun")) throw new Exception("Wintun script regression failed: " + error + text);
        }
        Console.WriteLine("Wintun PowerShell repeated-configuration tests passed.");
    }
}
