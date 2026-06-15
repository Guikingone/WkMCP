<#
.SYNOPSIS
  Construit le bundle d'extension Desktop (.mcpb) du serveur WolvenKit MCP.

.DESCRIPTION
  Compile le daemon puis le serveur MCP en Release, assemble le contenu attendu
  par le format MCPB (manifest.json + server/ + daemon/) et produit
  dist/wolvenkit-mcp.mcpb (une archive ZIP).

  Le .mcpb suppose un runtime .NET 8+ et `dotnet` sur le PATH de la machine
  cible (Windows). Les binaires natifs Windows (kraken.dll, DirectXTexNet.dll,
  opus-tools) sont déployés par le build du daemon et inclus tels quels.

.EXAMPLE
  ./build-mcpb.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$tfm = "net8.0"

Write-Host "==> Build daemon ($Configuration)..."
dotnet build (Join-Path $root "src\WolvenKitDaemon") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "build daemon a échoué" }

Write-Host "==> Build serveur MCP ($Configuration)..."
dotnet build (Join-Path $root "src\WolvenKitMcp") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "build serveur MCP a échoué" }

$stage = Join-Path $root "obj\mcpb-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$serverStage = Join-Path $stage "server"
$daemonStage = Join-Path $stage "daemon"
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
New-Item -ItemType Directory -Path $daemonStage -Force | Out-Null

$serverBin = Join-Path $root "src\WolvenKitMcp\bin\$Configuration\$tfm"
$daemonBin = Join-Path $root "src\WolvenKitDaemon\bin\$Configuration\$tfm"

Write-Host "==> Assemblage du bundle..."
Copy-Item (Join-Path $serverBin "*") $serverStage -Recurse -Force
Copy-Item (Join-Path $daemonBin "*") $daemonStage -Recurse -Force
Copy-Item (Join-Path $root "manifest.json") $stage -Force

# Mod Lua CETBridge (pont live in-game). Inclus tel quel dans le bundle pour que
# l'utilisateur puisse le copier dans <jeu>/bin/x64/plugins/cyber_engine_tweaks/mods/.
# Provenance MIT (Y4rd13/cyber-engine-tweak-mcp), cf. live-bridge/CETBridge/LICENSE.upstream.
$liveSrc = Join-Path $root "live-bridge\CETBridge"
if (Test-Path $liveSrc) {
    $liveStage = Join-Path $stage "live-bridge\CETBridge"
    New-Item -ItemType Directory -Path $liveStage -Force | Out-Null
    Copy-Item (Join-Path $liveSrc "*") $liveStage -Recurse -Force
    Write-Host "==> Mod CETBridge (live) inclus dans le bundle."
} else {
    Write-Warning "live-bridge/CETBridge introuvable — bundle sans le mod live."
}

$dist = Join-Path $root $OutputDir
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$mcpb = Join-Path $dist "wolvenkit-mcp.mcpb"
if (Test-Path $mcpb) { Remove-Item $mcpb -Force }

Write-Host "==> Compression -> $mcpb"
# Un .mcpb EST une archive ZIP, mais le format ZIP impose des séparateurs '/'.
# Compress-Archive sous Windows PowerShell 5.1 écrit des '\' (non conformes),
# donc on construit l'archive à la main en normalisant les chemins d'entrée.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stageFull = (Resolve-Path $stage).Path
$zipStream = [System.IO.File]::Open($mcpb, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $stageFull -Recurse -File) {
            $rel = $file.FullName.Substring($stageFull.Length).TrimStart('\', '/').Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $rel,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $zipStream.Dispose() }

$sizeMb = [Math]::Round((Get-Item $mcpb).Length / 1MB, 1)
Write-Host "OK : $mcpb ($sizeMb Mo)"
