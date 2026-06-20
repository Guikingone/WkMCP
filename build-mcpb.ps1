<#
.SYNOPSIS
  Builds the Desktop Extension bundle (.mcpb) of the WkMCP server.

.DESCRIPTION
  Compiles the daemon then the MCP server in Release, assembles the content
  expected by the MCPB format (manifest.json + server/ + daemon/) and produces
  dist/wkmcp.mcpb (a ZIP archive).

  The .mcpb assumes a .NET 8+ runtime and `dotnet` on the PATH of the target
  machine (Windows). The Windows native binaries (kraken.dll, DirectXTexNet.dll,
  opus-tools) are deployed by the daemon build and included as-is.

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
dotnet build (Join-Path $root "src\WkDaemon") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "daemon build failed" }

Write-Host "==> Build MCP server ($Configuration)..."
dotnet build (Join-Path $root "src\WkMcp") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "MCP server build failed" }

$stage = Join-Path $root "obj\mcpb-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$serverStage = Join-Path $stage "server"
$daemonStage = Join-Path $stage "daemon"
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
New-Item -ItemType Directory -Path $daemonStage -Force | Out-Null

$serverBin = Join-Path $root "src\WkMcp\bin\$Configuration\$tfm"
$daemonBin = Join-Path $root "src\WkDaemon\bin\$Configuration\$tfm"

Write-Host "==> Assembling the bundle..."
Copy-Item (Join-Path $serverBin "*") $serverStage -Recurse -Force
Copy-Item (Join-Path $daemonBin "*") $daemonStage -Recurse -Force
Copy-Item (Join-Path $root "manifest.json") $stage -Force

# CETBridge Lua mod (live in-game bridge). Included as-is in the bundle so that
# the user can copy it into <game>/bin/x64/plugins/cyber_engine_tweaks/mods/.
# MIT provenance (Y4rd13/cyber-engine-tweak-mcp), cf. live-bridge/CETBridge/LICENSE.upstream.
$liveSrc = Join-Path $root "live-bridge\CETBridge"
if (Test-Path $liveSrc) {
    $liveStage = Join-Path $stage "live-bridge\CETBridge"
    New-Item -ItemType Directory -Path $liveStage -Force | Out-Null
    Copy-Item (Join-Path $liveSrc "*") $liveStage -Recurse -Force
    Write-Host "==> CETBridge mod (live) included in the bundle."
} else {
    Write-Warning "live-bridge/CETBridge not found — bundle without the live mod."
}

# Slim the bundle: this is a Windows-x64-only tool, but the .NET build output
# carries native runtimes for every RID (linux-*, osx-*, maccatalyst-*, win-x86,
# win-arm64) plus .pdb debug symbols. None are loaded on win-x64 — dropping them
# removes ~28 MB of dead weight and trips fewer antivirus heuristics.
foreach ($stageDir in @($serverStage, $daemonStage)) {
    $rt = Join-Path $stageDir "runtimes"
    if (Test-Path $rt) {
        Get-ChildItem $rt -Directory |
            Where-Object { $_.Name -ne "win-x64" -and $_.Name -ne "win" } |
            Remove-Item -Recurse -Force
    }
}
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force

$dist = Join-Path $root $OutputDir
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$mcpb = Join-Path $dist "wkmcp.mcpb"
if (Test-Path $mcpb) { Remove-Item $mcpb -Force }

Write-Host "==> Compression -> $mcpb"
# A .mcpb IS a ZIP archive, but the ZIP format requires '/' separators.
# Compress-Archive under Windows PowerShell 5.1 writes '\' (non-compliant),
# so we build the archive by hand, normalizing the entry paths.
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
Write-Host "OK: $mcpb ($sizeMb MB)"
