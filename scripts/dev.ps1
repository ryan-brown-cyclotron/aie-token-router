#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Dev harness for the UsageTracker daemon + CLI debug loop.

.DESCRIPTION
  The daemon is a resident process launched from src/UsageTracker.Daemon/bin, so on Windows it holds a
  file lock on the built DLLs. That makes the inner loop: STOP the daemon -> BUILD -> re-INIT (restart).
  This script wraps that loop plus a few conveniences (status, logs, smoke test).

.EXAMPLE
  ./scripts/dev.ps1 up            # stop daemon, build, init against http://localhost:7071, verify health
  ./scripts/dev.ps1 stop          # kill the running daemon (release DLL locks / detach debugger)
  ./scripts/dev.ps1 restart       # stop + init (no rebuild)
  ./scripts/dev.ps1 build         # stop daemon, then build the solution
  ./scripts/dev.ps1 status        # usagetracker status
  ./scripts/dev.ps1 logs          # tail the daemon log
  ./scripts/dev.ps1 test          # send a sample PostToolUse payload through the CLI (trace)
  ./scripts/dev.ps1 up -Remote http://localhost:7071 -Configuration Debug
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('up', 'stop', 'restart', 'build', 'init', 'status', 'logs', 'test')]
    [string]$Action = 'up',

    [string]$Remote = 'http://localhost:7071',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Solution   = Join-Path $RepoRoot 'src/UsageTracker.sln'
$CliExe     = Join-Path $RepoRoot "src/UsageTracker.Cli/bin/$Configuration/net8.0/usagetracker.exe"
$DaemonExe  = Join-Path $RepoRoot "src/UsageTracker.Daemon/bin/$Configuration/net8.0/usagetracker-daemon.exe"
$LogFile    = Join-Path $env:APPDATA 'UsageTracker/daemon.log'
$DaemonProc = 'usagetracker-daemon'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Stop-Daemon {
    $procs = Get-Process -Name $DaemonProc -ErrorAction SilentlyContinue
    if (-not $procs) { Write-Step "Daemon not running."; return }

    Write-Step "Stopping daemon (PID $($procs.Id -join ', '))..."
    $procs | Stop-Process -Force
    # Wait for the OS to release the DLL file locks before a build touches bin/.
    for ($i = 0; $i -lt 25; $i++) {
        if (-not (Get-Process -Name $DaemonProc -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 100
    }
    Write-Warning "Daemon process still present after wait; a build may hit locked files."
}

function Invoke-Build {
    Stop-Daemon
    Write-Step "Building $Configuration ..."
    dotnet build $Solution -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

function Invoke-Init {
    if (-not (Test-Path $CliExe))    { throw "CLI not built: $CliExe  (run: ./scripts/dev.ps1 build)" }
    if (-not (Test-Path $DaemonExe)) { throw "Daemon not built: $DaemonExe  (run: ./scripts/dev.ps1 build)" }
    Write-Step "Initializing (remote=$Remote) and starting daemon..."
    & $CliExe init --remote $Remote --daemon-path $DaemonExe
}

function Invoke-Status {
    if (-not (Test-Path $CliExe)) { throw "CLI not built: $CliExe" }
    & $CliExe status
}

function Show-Logs {
    if (-not (Test-Path $LogFile)) { Write-Warning "No log file yet: $LogFile"; return }
    Write-Step "Tailing $LogFile (Ctrl+C to stop)"
    Get-Content -Path $LogFile -Tail 40 -Wait
}

function Invoke-Test {
    if (-not (Test-Path $CliExe)) { throw "CLI not built: $CliExe" }
    Write-Step "Sending sample PostToolUse payload (trace)..."
    $payload = '{"hook_event_name":"PostToolUse","session_id":"dev-test","tool_name":"Read","tool_input":{"file_path":"x"},"tool_response":{"content":"hello"}}'
    $payload | & $CliExe trace claude-code --stdin
    Write-Host "`n(exit=$LASTEXITCODE)" -ForegroundColor DarkGray
}

switch ($Action) {
    'up'      { Invoke-Build; Invoke-Init; Write-Host ''; Invoke-Status }
    'build'   { Invoke-Build }
    'init'    { Invoke-Init }
    'restart' { Stop-Daemon; Invoke-Init }
    'stop'    { Stop-Daemon }
    'status'  { Invoke-Status }
    'logs'    { Show-Logs }
    'test'    { Invoke-Test }
}
