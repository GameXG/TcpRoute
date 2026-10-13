param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$SelfContained = $true,
    [string]$TcpRouteExe = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $PSScriptRoot "TcpRouteTray\TcpRouteTray.csproj"
$dist = Join-Path $repoRoot "dist"
$packageBuildRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tcproute-windows-package-" + [Guid]::NewGuid().ToString("N"))
$output = Join-Path $packageBuildRoot "TcpRoute2-$Runtime"
$selfContainedValue = if ($SelfContained.IsPresent) { "true" } else { "false" }
if (-not $TcpRouteExe) {
    $coreArch = if ($Runtime -eq "win-x64") { "amd64" } else { "arm64" }
    $TcpRouteExe = Join-Path $repoRoot ".build\release-core\TcpRoute2-windows-$coreArch.exe"
}
if (-not (Test-Path -Path $TcpRouteExe -PathType Leaf)) { throw "TcpRoute2 core is missing: $TcpRouteExe" }
if (-not $Version) {
    $Version = (Get-Content (Join-Path $repoRoot "VERSION") -Raw).Trim()
}

New-Item -ItemType Directory -Force -Path $dist, $packageBuildRoot | Out-Null

try {
    $geoSnapshot = Join-Path $packageBuildRoot "geo"
    New-Item -ItemType Directory -Force -Path $geoSnapshot | Out-Null
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/Loyalsoldier/v2ray-rules-dat/releases/latest"
    $geoVersion = $release.tag_name
    if ($geoVersion -notmatch '^[A-Za-z0-9._-]+$') { throw "Invalid Geo release tag" }
    foreach ($name in @("geosite.dat", "geoip.dat")) {
        $baseUrl = "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/download/$geoVersion"
        $checksumPath = Join-Path $geoSnapshot "$name.sha256sum"
        $filePath = Join-Path $geoSnapshot $name
        Invoke-WebRequest -Uri "$baseUrl/$name.sha256sum" -OutFile $checksumPath
        $expectedHash = ((Get-Content $checksumPath -Raw).Trim() -split '\s+')[0]
        if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid $name checksum" }
        Invoke-WebRequest -Uri "$baseUrl/$name" -OutFile $filePath
        if ((Get-FileHash $filePath -Algorithm SHA256).Hash -ne $expectedHash) { throw "$name checksum mismatch" }
    }
    Write-Host "Verified latest Geo pair: $geoVersion"
    $wintunArchive = Join-Path $packageBuildRoot "wintun.zip"
    Invoke-WebRequest -Uri "https://www.wintun.net/builds/wintun-0.14.1.zip" -OutFile $wintunArchive
    if ((Get-FileHash $wintunArchive -Algorithm SHA256).Hash -ne "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51") { throw "Wintun archive checksum mismatch" }
    Expand-Archive -Path $wintunArchive -DestinationPath $packageBuildRoot
    dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained:$selfContainedValue `
        -p:Version="$Version.0" `
        -p:AssemblyVersion="$Version.0.0" `
        -p:FileVersion="$Version.0.0" `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $output

    $clientName = "TcpRoute2_Windows_" + ($Runtime -replace '^win-', '') + ".exe"
    Move-Item -Force -Path (Join-Path $output "TcpRoute2.exe") -Destination (Join-Path $output $clientName)
    $wintunArch = if ($Runtime -eq "win-x64") { "amd64" } else { "arm64" }
    Copy-Item -Path (Join-Path $packageBuildRoot "wintun\bin\$wintunArch\wintun.dll") -Destination (Join-Path $output "wintun.dll")
    Copy-Item -Path (Join-Path $packageBuildRoot "wintun\LICENSE.txt") -Destination (Join-Path $output "WINTUN-LICENSE.txt")

    if ($TcpRouteExe) {
        Copy-Item -Force -Path $TcpRouteExe -Destination (Join-Path $output "TcpRoute2.exe")
    }

    $geosite = Join-Path $geoSnapshot "geosite.dat"
    if (-not (Test-Path $geosite)) {
        throw "geosite.dat is missing; run scripts/fetch-geosite.sh first"
    }
    Copy-Item -Force -Path $geosite -Destination (Join-Path $output "geosite.dat")
    $geoip = Join-Path $geoSnapshot "geoip.dat"
    if (-not (Test-Path $geoip)) {
        throw "geoip.dat is missing; run scripts/fetch-geosite.sh first"
    }
    Copy-Item -Force -Path $geoip -Destination (Join-Path $output "geoip.dat")

    $archive = Join-Path $dist "TcpRoute2-v$Version-$Runtime.zip"
    Compress-Archive -Force -Path $output -DestinationPath $archive

    Write-Host "Created $archive"
}
finally {
    Remove-Item -Recurse -Force -Path $packageBuildRoot -ErrorAction SilentlyContinue
}
