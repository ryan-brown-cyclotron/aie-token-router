param(
    [string] $ImageName = 'usage-tracker:local'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
docker build -f (Join-Path $repoRoot 'src/UsageTracker.Functions/Dockerfile') -t $ImageName $repoRoot