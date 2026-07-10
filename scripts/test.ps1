$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet test (Join-Path $repoRoot 'src/UsageTracker.sln')