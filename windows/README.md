# TcpRoute2 Windows Client

Windows distribution is a portable x64/ARM64 ZIP with a tray-only client.
There is no main or configuration window. Left-clicking the tray icon opens
only the activity log; closing that window hides it without stopping TcpRoute2.

## Tray Menu

Right-click the tray icon for status, Start/Stop, Restart, connectivity checks,
network mode, outbound interface, runtime settings, configuration files, Geo
data updates, log export, and Quit. Commands and selected settings live in this
menu. Upstreams, routing rules, Hosts, and DNS are edited in TOML files.

Proxy mode exposes SOCKS5, HTTP, and CONNECT on one listener. Optional Windows
system proxy settings are restored on stop. TUN / VPN (Wintun) mode uses the
pinned xray-core TUN stack and requests administrator privileges through UAC.
Modes are mutually exclusive; system proxy and LAN controls are unavailable in
TUN mode. Stop before switching modes, interfaces, STUN, QUIC, or IPv6.

Outbound interface defaults to **Automatic**. The menu lists interfaces whose
status is **UP**, without requiring a gateway for inclusion. Manual selection
is persisted by interface GUID. GUID comparison accepts case and braces. Startup
uses the same UP interface inventory as the menu and maps the GUID to its IPv4
interface index, including logical/hidden interfaces. A selected interface must have a connected IPv4 default route and usable
DNS. An unavailable manual selection produces a readable error; choose Automatic
to allow default-route selection.

Wintun requires the bundled architecture-matched `wintun.dll`. If Windows has
disabled its IPv6 binding and IPv6 is off in TcpRoute2, startup skips IPv6
interface configuration. Enabling IPv6 requires that binding. When IPv6 is
available, TUN captures it so the core can enforce the IPv6 policy.
PowerShell progress is suppressed and failures are shown as readable text.

TUN address and route setup reuses existing entries on the owned adapter and
corrects their prefix/metric when needed. Newly added entries use ActiveStore,
so repeated starts and partially completed setup do not fail on duplicate IPs.
Listener readiness uses a complete local SOCKS5 UDP ASSOCIATE exchange without
contacting an upstream or sending UDP traffic.

Wintun has no per-application routing, comprehensive leak blocking, ICMP/ping,
or automatic network handover. The DNS gateway supports A/AAAA; other record
types receive NOTIMP. Interface loss stops the session; restart to select again.

## Configuration Files

The portable directory contains:

```text
TcpRoute2_Windows_x64.exe or TcpRoute2_Windows_arm64.exe
TcpRoute2.exe
TcpRoute2Client.json
config.toml
config.toml.example
geosite.dat
geoip.dat
wintun.dll
```

Use **Configuration > Edit file** to open a TOML profile in Notepad.
Use **Runtime settings > Edit client configuration** for client preferences.
Changes are applied automatically after two stable checks, about 1.5 seconds.
A running session restarts to apply changed configuration. If startup fails,
the client tries to restore the previous running configuration, retains the
user's edited file, and reports the error in the activity log. Stopped sessions
remain stopped. **Reload now** is also available.

Client preferences are stored in `TcpRoute2Client.json`:

```json
{
  "SelectedConfigId": "config.toml",
  "ExecutablePath": null,
  "AllowLan": false,
  "SystemProxyEnabled": false,
  "AllowStun": false,
  "AllowQuic": false,
  "EnableIpv6": false,
  "UseWintun": false,
  "OutboundInterfaceId": "",
  "Appearance": "system",
  "Language": "system",
  "GeoDataInterval": "daily"
}
```

`UseWintun=true` selects TUN. An empty `OutboundInterfaceId` selects Automatic;
the tray menu can select an UP interface and write its GUID. Appearance accepts
`system|light|dark`, language accepts `system|en|zh-Hans|zh-Hant`, and Geo data
interval accepts `off|daily|weekly`. STUN, QUIC, and IPv6 default off.
STUN permits recognized discovery traffic; QUIC permits UDP port 443; IPv6
permits locally known IPv6 destinations and DNS candidates. A remote upstream
controls the address family when it resolves the destination itself.

Network recovery and Geo update metadata are stored separately in the client
runtime directory under the system temporary directory. Existing recovery
metadata in older `TcpRoute2Client.json` files is retained during migration.

Upgrades also import the former `TcpRouteClient.json` when the new preferences
file is absent. The old Wintun/runtime identifiers remain stable for recovery.

All core settings, including listener address, upstream URLs, nested per-route
whitelists/blacklists, Hosts, and optional DoH/system DNS, remain in TOML.
The file is passed to the core without conversion to a visual model.
See [the configuration reference](../help.md).

For `DnsResolve=true`, matching Hosts entries override DNS; only misses query
DNS/DoH. Multiple matching hosts addresses race with each other, retaining
source Credit. Filtering or connection failure does not fall back to DNS.
`DnsResolve=false` keeps remote upstream resolution.

The verified Geosite/GeoIP pair stays beside the core. Update it on demand from
the tray or configure a schedule in the client JSON. Failed updates retain the
verified pair.

Activity reads UTF-8 stdout/stderr into bounded memory: at most 1,000 lines,
2,048 characters each. Only explicit exports create log files; normal operation
creates no `logs/` directory.

## Build And Package

```powershell
cd windows
dotnet run --project .\TcpRouteTray\TcpRouteTray.csproj
powershell -ExecutionPolicy Bypass -File .\package-tray.ps1 -Runtime win-x64
```

On macOS, cross-build or package with:

```bash
dotnet build windows/TcpRouteTray/TcpRouteTray.csproj -p:EnableWindowsTargeting=true
bash windows/package-tray.sh win-x64
bash windows/package-tray.sh win-arm64
```

The core must first exist under `../.build/release-core`, or be selected with
`TCPROUTE_EXE` for the shell packaging script. Packaging verifies downloaded
Geo data and Wintun. Versioned ZIPs and `SHA256SUMS.txt` belong under root
`dist/`; expanded packaging trees use temporary directories.

`TcpRoute2Client.ps1` remains a debugging/automation fallback. The old
`MainWindow`, `AdvancedTomlWindow`, and visual editor sources are historical
and excluded from the tray client's build.

Cross-builds and portable self-tests do not verify Windows UAC, drivers,
registry changes, or live routing. These require a Windows machine.
