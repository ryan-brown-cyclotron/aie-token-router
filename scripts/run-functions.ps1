$ErrorActionPreference = 'Stop'

# Runs the Function App locally via the Azure Functions Core Tools (func). Extra args are
# forwarded to `func start` (e.g. --port 7072). For the full orchestrated experience (Cosmos
# emulator, etc.), run the AppHost instead: scripts/run-apphost.ps1.

$repoRoot = Split-Path -Parent $PSScriptRoot
$functionsDir = Join-Path $repoRoot 'src/UsageTracker.Functions'

if (-not $env:AZURE_FUNCTIONS_ENVIRONMENT) { $env:AZURE_FUNCTIONS_ENVIRONMENT = 'Development' }

Push-Location $functionsDir
try {
    func start @args
}
finally {
    Pop-Location
}
