# dev-launch.ps1 — Launch two PrintStation instances (BH + WB) for dual-store testing
# Run from the PrintStation repo root: .\dev-launch.ps1

$ErrorActionPreference = "Stop"

$TestRoot = "C:\Users\dhite\HitePhotoTest"
$RepoRoot = $PSScriptRoot
$ProfileBase = Join-Path $env:APPDATA "HitePhoto\PrintStation\profiles"

$Stores = @(
    @{ Name = "BH"; StoreId = 1 },
    @{ Name = "WB"; StoreId = 2 }
)

# ── Step 1: Create folder structure ──────────────────────────────────────────

foreach ($store in $Stores) {
    $name = $store.Name
    foreach ($sub in @("dakis-watch", "orders", "sqlite", "logs")) {
        $dir = Join-Path $TestRoot "$name\$sub"
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            Write-Host "  Created $dir"
        }
    }
}

# ── Step 2: Seed profile settings if missing ─────────────────────────────────

foreach ($store in $Stores) {
    $name = $store.Name
    $profileDir = Join-Path $ProfileBase $name
    $settingsFile = Join-Path $profileDir "settings.json"

    if (-not (Test-Path $settingsFile)) {
        New-Item -ItemType Directory -Path $profileDir -Force | Out-Null

        $settings = @{
            StoreId            = $store.StoreId
            DakisWatchFolder   = Join-Path $TestRoot "$name\dakis-watch"
            OrderOutputPath    = Join-Path $TestRoot "$name\orders"
            SqlitePath         = Join-Path $TestRoot "$name\sqlite\orders.db"
            LogDirectory       = Join-Path $TestRoot "$name\logs"
            DbHost             = "192.168.1.149"
            DbPort             = 3306
            DbName             = "hitephoto"
            DbUser             = "labapi"
            DbPassword         = "SlantedPeanuts2026"
            SyncEnabled        = $false
            SyncIntervalSeconds = 30
            DakisEnabled       = $true
            PixfizzEnabled     = $false
            EnableLogging      = $true
            RefreshIntervalSeconds = 5
            Theme              = "Light"
            DeveloperMode      = $true
        }

        $settings | ConvertTo-Json -Depth 3 | Set-Content $settingsFile -Encoding UTF8
        Write-Host "  Seeded settings: $settingsFile"
    } else {
        Write-Host "  Settings exist: $settingsFile"
    }
}

# ── Step 3: Build the project ────────────────────────────────────────────────

Write-Host "`nBuilding PrintStation..."
Push-Location $RepoRoot
dotnet build --no-restore -q 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED — fix errors before launching." -ForegroundColor Red
    Pop-Location
    exit 1
}
Pop-Location
Write-Host "  Build OK"

# ── Step 4: Launch both instances ────────────────────────────────────────────

Write-Host "`nLaunching PrintStation instances..."

foreach ($store in $Stores) {
    $name = $store.Name
    Write-Host "  Starting $name (StoreId=$($store.StoreId))..."
    Start-Process dotnet -ArgumentList "run", "--project", "$RepoRoot\src\HitePhoto.PrintStation", "--no-build", "--", "--profile", $name -WindowStyle Normal
}

# ── Step 5: Tail logs in split terminal ──────────────────────────────────────

Start-Sleep -Seconds 2

$bhLog = Join-Path $TestRoot "BH\logs\printstation.log"
$wbLog = Join-Path $TestRoot "WB\logs\printstation.log"

# Create empty log files if they don't exist yet (so tail doesn't error)
if (-not (Test-Path $bhLog)) { New-Item -ItemType File -Path $bhLog -Force | Out-Null }
if (-not (Test-Path $wbLog)) { New-Item -ItemType File -Path $wbLog -Force | Out-Null }

Write-Host "`nOpening log viewer..."
wt -w 0 new-tab --title "BH Log" pwsh -NoExit -Command "Write-Host 'BH Log' -ForegroundColor Cyan; Get-Content '$bhLog' -Wait -Tail 50" `; split-pane --title "WB Log" pwsh -NoExit -Command "Write-Host 'WB Log' -ForegroundColor Green; Get-Content '$wbLog' -Wait -Tail 50"

Write-Host "`nDual-store test environment running!" -ForegroundColor Green
Write-Host "  BH window: PrintStation [BH]"
Write-Host "  WB window: PrintStation [WB]"
Write-Host "  Logs: Windows Terminal split pane"
Write-Host ""
Write-Host "To inject a test order:"
Write-Host "  Copy-Item -Recurse .\dev\sample-orders\pickup-bh $TestRoot\BH\dakis-watch\"
