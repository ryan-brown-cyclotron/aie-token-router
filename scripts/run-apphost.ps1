$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $repoRoot 'src/UsageTracker.AppHost/UsageTracker.AppHost.csproj') -- @args