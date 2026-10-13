# Shared assets

`TcpRoute2-AppIcon.png` and `TcpRoute2-AppIcon.ico` are desktop renditions of the
Android adaptive icon, which is the canonical TcpRoute2 product mark. Keep the
desktop renditions consistent with the Android launcher artwork.

## Geo data

`geosite.dat` is the Xray-compatible domain-list resource used by
`Type="geosite"`, `Path="geosite:gfw,geosite:cn"` rules. The core loads the data
beside its executable; Android uses its private Geo directory.

- Source: `Loyalsoldier/v2ray-rules-dat`
- Release: `202608012248`
- SHA-256: `814a2028109e9e361d67efe9eadb18068c19a72a6eb8f3988da59617f4761fbb`
- License: GPL-3.0 (see the source repository)

Run `bash scripts/fetch-geosite.sh` to download or verify the pinned pair before
building. Downloaded data and checksum files are local build resources and are
not tracked in Git.

`geoip.dat` is the matching Xray-compatible IP-list resource used by
`Type="geoip"`, `Path="geoip:cn,geoip:private"` rules, with the same data-location
policy as Geosite.

- Source: `Loyalsoldier/v2ray-rules-dat`
- Release: `202608012248`
- SHA-256: `6ba63d75f307d16a81ae09406ddcf2779fa75cb642d4aae59613370d62d33509`
- License: GPL-3.0 (see the source repository)

Supported client packages bundle both files. Android also exposes an independent Geo data manager that updates the pair only after both release checksums pass.
