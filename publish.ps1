# publish.ps1 — Build PrintStation in Release mode and package for auto-update.
# Outputs:
#   publish\version.txt         — UTC build timestamp
#   publish\PrintStation.zip    — everything needed to run (no .pdb)
#
# Usage:
#   .\publish.ps1

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectDir = Join-Path $PSScriptRoot "src\HitePhoto.PrintStation"
$outputDir  = Join-Path $projectDir "bin\Publish\net10.0-windows"
$publishDir = Join-Path $PSScriptRoot "publish"

Write-Host "=== Building $Configuration ===" -ForegroundColor Cyan
dotnet build "$projectDir\HitePhoto.PrintStation.csproj" -c $Configuration -o $outputDir
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Create publish output folder
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir | Out-Null

# Write version.txt with UTC timestamp of the built exe
$exe = Join-Path $outputDir "HitePhoto.PrintStation.exe"
if (-not (Test-Path $exe)) {
    Write-Host "ERROR: Built exe not found at $exe" -ForegroundColor Red
    exit 1
}
$buildTimeUtc = (Get-Item $exe).LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
$versionFile = Join-Path $publishDir "version.txt"
Set-Content -Path $versionFile -Value $buildTimeUtc -NoNewline
Write-Host "version.txt: $buildTimeUtc" -ForegroundColor Green

# Create zip from build output, skip .pdb files
$zipPath = Join-Path $publishDir "PrintStation.zip"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    Get-ChildItem -Path $outputDir -Recurse -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
        $relativePath = $_.FullName.Substring($outputDir.Length + 1).Replace('\', '/')
        $stream = [System.IO.File]::Open($_.FullName, 'Open', 'Read', 'ReadWrite')
        try {
            $entry = $zip.CreateEntry($relativePath, 'Optimal')
            $entryStream = $entry.Open()
            try {
                $stream.CopyTo($entryStream)
            } finally {
                $entryStream.Close()
            }
        } finally {
            $stream.Close()
        }
    }
} finally {
    $zip.Dispose()
}

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "PrintStation.zip: ${zipSize} MB" -ForegroundColor Green
Write-Host ""
Write-Host "=== Done! ===" -ForegroundColor Cyan
Write-Host "  $versionFile"
Write-Host "  $zipPath"
