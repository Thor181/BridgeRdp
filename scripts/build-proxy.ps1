param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    dotnet build BridgeRdp.slnx -c $Configuration -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    dotnet publish src/BridgeRdp.Client -c $Configuration --no-restore -m:1 -p:UseSharedCompilation=false -o artifacts/client
    if ($LASTEXITCODE -ne 0) { throw "Client publish failed" }
    dotnet publish src/BridgeRdp.Agent -c $Configuration --no-restore -m:1 -p:UseSharedCompilation=false -o artifacts/agent
    if ($LASTEXITCODE -ne 0) { throw "Agent publish failed" }
    dotnet publish src/BridgeRdp.Explorer -c $Configuration --no-restore -m:1 -p:UseSharedCompilation=false -o artifacts/explorer
    if ($LASTEXITCODE -ne 0) { throw "Explorer publish failed" }
    Copy-Item -LiteralPath scripts/register-client.ps1 -Destination artifacts/client/register-client.ps1
    Copy-Item -LiteralPath scripts/unregister-client.ps1 -Destination artifacts/client/unregister-client.ps1
    Write-Host "Client: $repo\artifacts\client"
    Write-Host "Agent:  $repo\artifacts\agent"
    Write-Host "Explorer (local): $repo\artifacts\explorer"
} finally { Pop-Location }
