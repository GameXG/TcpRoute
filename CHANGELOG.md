# TcpRoute2 Release Notes

## 1.1

- Android adds subscription management with multiple sources, User-Agent,
  configurable defaults and locks, node overrides, manual/optional scheduled
  refresh, cached nodes, and TOML imports that preserve user rules and settings.
  The VPN network proxy control is now labeled "Enable system proxy".
- Windows external `TcpRoute2Client.json` auto-reload defaults off via
  `AutoReloadClientSettings`; TOML auto-reload remains enabled. Language and
  appearance are selectable from the tray menu.

- Windows TUN now bypasses the process serving a local upstream proxy, preventing
  traffic from being routed back into that proxy. Optional process rules and
  destination IP/CIDR and port rules support additional bypass requirements.
  Socket ownership is checked for each new flow, and bypass connections use the
  selected physical outbound interface.
- The Windows activity window wraps long lines. STUN, QUIC and IPv6 settings are
  enforced for ordinary TUN traffic; explicit bypass traffic remains exempt.
- Proxy chains accept existing upstream names in `ProxyUrl`'s `upProxy` option.
  `DialProxyFor` lets a node additionally serve as another upstream's front proxy
  while retaining its own ordinary routing role. Invalid references and cycles
  are rejected.
- Windows subscriptions support URI lists, Base64 and JSON, optional `UserAgent`,
  defaults, locked fields and per-node overrides. Selected nodes are written to
  TOML with managed `sub_<subscription>_<node>` names. Refresh retains user-edited
  defaults and rules. Deselection and disappeared nodes use `Remove=true`;
  reselection restores them. Chains depending on a disabled node are skipped.
- Windows subscriptions also extract proxy entries from complete Clash YAML
  profiles (`proxies:`) and Surge configurations (`[Proxy]`).
- `[[GeoData]]` configures data URLs, optional checksum URLs and update intervals.
  Subscription and Geo updates remain manual by default. A custom source with
  no `ChecksumURL` performs no external checksum verification.
- Fixed loading all upstream entries after Direct and retained `Remove` and
  `DialProxyFor` through Android configuration editing. Reduced temporary
  allocation when checking the cached route's eligibility.

Packages include Android ARM64, Windows x64/ARM64 clients, Linux amd64/arm64
cores, macOS ARM64 core, and Windows amd64/arm64 cores, with SHA-256 checksums.
Android retains the 1.0 signing certificate and uses version code 113.
Windows packages are unsigned.
