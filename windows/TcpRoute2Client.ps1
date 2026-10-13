param(
    [string]$TcpRouteExe = "",
    [string]$ConfigName = "config",
    [switch]$AllowLAN,
    [switch]$SetSystemProxy,
    [ValidateSet("start", "stop", "restart", "status", "open-config", "clear-proxy")]
    [string]$Command = "start"
)

$ErrorActionPreference = "Stop"

$ConfigDir = $PSScriptRoot
$RuntimeDir = Join-Path $env:TEMP "TcpRouteClient"
$LogDir = Join-Path $PSScriptRoot "logs"
$PidFile = Join-Path $RuntimeDir "tcproute.pid"

function Ensure-Directories {
    New-Item -ItemType Directory -Force -Path $ConfigDir, $RuntimeDir, $LogDir | Out-Null
}

function Ensure-DefaultConfig {
    $hasConfig = Get-ChildItem -Path $ConfigDir -Filter "*.toml" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $hasConfig) {
        return
    }

    @'
addr="127.0.0.1:7070"

[[UpStreams]]
Name="direct"
ProxyUrl="direct://0.0.0.0:0000"
DnsResolve=true
Credit=0
Sleep=0
CorrectDelay=0
'@ | Set-Content -Path (Join-Path $ConfigDir "config.toml") -Encoding UTF8
}

function Resolve-TcpRouteExe {
    if ($TcpRouteExe -and (Test-Path $TcpRouteExe)) {
        return (Resolve-Path $TcpRouteExe).Path
    }

    $candidates = @(
        (Join-Path $PSScriptRoot "TcpRoute2.exe"),
        (Join-Path $PSScriptRoot "..\.build\release-core\TcpRoute2-windows-amd64.exe"),
        (Join-Path $PSScriptRoot "..\.build\release-core\TcpRoute2-windows-arm64.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "TcpRoute2 executable not found. Pass -TcpRouteExe or place TcpRoute2.exe next to this script."
}

function Get-ConfigPath {
    $path = Join-Path $ConfigDir "$ConfigName.toml"
    if (!(Test-Path $path)) {
        throw "Config not found: $path"
    }
    return $path
}

function Get-ListenPort([string]$Content) {
    foreach ($line in $Content -split "`r?`n") {
        $clean = ($line -replace '#.*$', '').Trim()
        if ($clean -match '^addr\s*=\s*["'']([^"'']+)["'']') {
            $address = $Matches[1]
            if ($address -match ':(\d+)$') {
                return [int]$Matches[1]
            }
            throw "Invalid addr value: $address"
        }
    }
    throw "Config must contain addr."
}

function New-RuntimeConfig([string]$ConfigPath) {
    $content = Get-Content -Raw -Path $ConfigPath
    $port = Get-ListenPort $content
    $host = if ($AllowLAN) { "0.0.0.0" } else { "127.0.0.1" }
    $runtime = $content -replace '(?m)^\s*addr\s*=\s*["''][^"'']+["'']', "addr=`"$host`:$port`""
    $runtimePath = Join-Path $RuntimeDir "tcproute-runtime.toml"
    $runtime | Set-Content -Path $runtimePath -Encoding UTF8
    return @{ Path = $runtimePath; Port = $port }
}

function Stop-TcpRoute2 {
    if (!(Test-Path $PidFile)) {
        return
    }
    $pidValue = Get-Content -Path $PidFile -ErrorAction SilentlyContinue
    if ($pidValue) {
        $process = Get-Process -Id ([int]$pidValue) -ErrorAction SilentlyContinue
        if ($process) {
            Stop-Process -Id $process.Id -Force
        }
    }
    Remove-Item -Force -Path $PidFile -ErrorAction SilentlyContinue
}

function Set-WindowsSocksProxy([int]$Port) {
    $internetSettings = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
    Set-ItemProperty -Path $internetSettings -Name ProxyEnable -Value 1
    Set-ItemProperty -Path $internetSettings -Name ProxyServer -Value "socks=127.0.0.1:$Port"
}

function Clear-WindowsProxy {
    $internetSettings = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
    Set-ItemProperty -Path $internetSettings -Name ProxyEnable -Value 0
}

function Start-TcpRoute2 {
    Stop-TcpRoute2
    $exe = Resolve-TcpRouteExe
    $config = Get-ConfigPath
    $runtime = New-RuntimeConfig $config
    $logFile = Join-Path $LogDir "tcproute.log"

    $process = Start-Process -FilePath $exe -ArgumentList "-config", $runtime.Path -RedirectStandardOutput $logFile -RedirectStandardError $logFile -PassThru -WindowStyle Hidden
    $process.Id | Set-Content -Path $PidFile

    if ($SetSystemProxy) {
        Set-WindowsSocksProxy $runtime.Port
    }

    Write-Host "TcpRoute2 started. PID=$($process.Id) SOCKS=127.0.0.1:$($runtime.Port)"
}

Ensure-Directories
Ensure-DefaultConfig

switch ($Command) {
    "start" { Start-TcpRoute2 }
    "restart" { Start-TcpRoute2 }
    "stop" { Stop-TcpRoute2; if ($SetSystemProxy) { Clear-WindowsProxy }; Write-Host "TcpRoute2 stopped." }
    "status" { if (Test-Path $PidFile) { Get-Content $PidFile } else { Write-Host "Stopped" } }
    "open-config" { Start-Process $ConfigDir }
    "clear-proxy" { Clear-WindowsProxy; Write-Host "Windows proxy cleared." }
}
