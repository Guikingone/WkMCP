<#
.SYNOPSIS
  Builds dist/wkmcp-nexus.zip — a Nexus-friendly, NO-BINARY package.

.DESCRIPTION
  Nexus Mods auto-quarantines any upload containing an .exe or .dll, so the
  compiled server (wkmcp.mcpb) is distributed via GitHub Releases instead. This
  archive carries only text assets — the install README and the optional in-game
  CETBridge Lua mod — and a guard fails the build if any binary slips in.
#>
param([string]$OutputDir = "dist")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$stage = Join-Path $root "obj\nexus-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "==> Assembling the Nexus package (no binaries)..."
Copy-Item (Join-Path $root "nexus\README.txt") $stage -Force
Copy-Item (Join-Path $root "live-bridge\CETBridge") (Join-Path $stage "CETBridge") -Recurse -Force

# Guard: this package must never contain anything Nexus would quarantine.
$forbidden = @('.dll', '.exe', '.so', '.dylib', '.pdb', '.bin', '.a', '.node')
$bad = Get-ChildItem $stage -Recurse -File |
    Where-Object { $forbidden -contains $_.Extension.ToLower() }
if ($bad) {
    throw "Nexus package would contain binaries: $(( $bad | ForEach-Object Name ) -join ', ')"
}
Write-Host "    Binary-free check passed ($((Get-ChildItem $stage -Recurse -File).Count) files)."

$dist = Join-Path $root $OutputDir
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip = Join-Path $dist "wkmcp-nexus.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "==> Compression -> $zip"
# Build the ZIP by hand with '/' separators (Windows PowerShell 5.1's
# Compress-Archive writes '\', which some extractors mishandle).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stageFull = (Resolve-Path $stage).Path
$zipStream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
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

$sizeKb = [Math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host "OK: $zip ($sizeKb KB)"
