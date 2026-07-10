$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $repoRoot 'src/UsageTracker.sln')