# dev-run-tests.ps1 — Inject test orders and verify results
# Requires both PrintStation instances to be running (use dev-launch.ps1 first)
# Run from repo root: .\dev-run-tests.ps1

param(
    [switch]$CleanOnly,    # Just clean up test data, don't inject
    [switch]$NoCleanup,    # Skip cleanup after tests
    [int]$WaitSeconds = 8  # Seconds to wait for ingestion
)

$ErrorActionPreference = "Stop"

$TestRoot = "S:\HitePhotoTest"
$SampleDir = Join-Path $PSScriptRoot "dev\sample-orders"

$BhSqlite = Join-Path $TestRoot "BH\sqlite\orders.db"
$WbSqlite = Join-Path $TestRoot "WB\sqlite\orders.db"
$BhLog = Join-Path $TestRoot "BH\logs\printstation.log"
$WbLog = Join-Path $TestRoot "WB\logs\printstation.log"

$PassCount = 0
$FailCount = 0
$Results = @()

# ── Helpers ──────────────────────────────────────────────────────────────────

function Query-Sqlite($dbPath, $sql) {
    if (-not (Test-Path $dbPath)) { return @() }
    $result = sqlite3 $dbPath $sql 2>&1
    if ($LASTEXITCODE -ne 0) { return @() }
    return $result
}

function Check($name, $condition, $detail) {
    if ($condition) {
        Write-Host "  PASS: $name" -ForegroundColor Green
        $script:PassCount++
        $script:Results += @{ Name = $name; Status = "PASS"; Detail = "" }
    } else {
        Write-Host "  FAIL: $name — $detail" -ForegroundColor Red
        $script:FailCount++
        $script:Results += @{ Name = $name; Status = "FAIL"; Detail = $detail }
    }
}

function Inject-Order($scenario, $store) {
    $src = Join-Path $SampleDir $scenario
    $dest = Join-Path $TestRoot "$store\dakis-watch\$scenario"

    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }

    # Copy everything EXCEPT the marker first, then add marker to trigger watcher
    Copy-Item -Recurse $src $dest
    $marker = Join-Path $dest "metadata\download_complete"
    if (Test-Path $marker) { Remove-Item $marker }

    # Small delay then create marker to trigger FileSystemWatcher
    Start-Sleep -Milliseconds 200
    New-Item -ItemType File -Path $marker -Force | Out-Null
}

function Clean-TestOrders() {
    Write-Host "`nCleaning up test orders..."

    # Remove injected folders from watch dirs
    foreach ($store in @("BH", "WB")) {
        $watchDir = Join-Path $TestRoot "$store\dakis-watch"
        if (Test-Path $watchDir) {
            Get-ChildItem $watchDir -Directory | Where-Object { $_.Name -like "TEST-*" -or $_.Name -like "pickup-*" -or $_.Name -like "shipped-*" -or $_.Name -like "interstore-*" -or $_.Name -like "bad-*" -or $_.Name -like "multi-*" } | Remove-Item -Recurse -Force
        }
    }

    # Delete test orders from SQLite (by TEST- prefix in external_order_id)
    foreach ($db in @($BhSqlite, $WbSqlite)) {
        if (Test-Path $db) {
            sqlite3 $db "DELETE FROM order_items WHERE order_id IN (SELECT id FROM orders WHERE external_order_id LIKE 'TEST-%');"
            sqlite3 $db "DELETE FROM order_history WHERE order_id IN (SELECT id FROM orders WHERE external_order_id LIKE 'TEST-%');"
            sqlite3 $db "DELETE FROM alerts WHERE message LIKE '%TEST-%';"
            sqlite3 $db "DELETE FROM orders WHERE external_order_id LIKE 'TEST-%';"
            Write-Host "  Cleaned $db"
        }
    }

    Write-Host "  Done"
}

# ── Clean only mode ──────────────────────────────────────────────────────────

if ($CleanOnly) {
    Clean-TestOrders
    exit 0
}

# ── Pre-flight checks ────────────────────────────────────────────────────────

Write-Host "=== PrintStation Dual-Store Test Runner ===" -ForegroundColor Cyan
Write-Host ""

# Check SQLite DBs exist (instances must be running)
if (-not (Test-Path $BhSqlite)) {
    Write-Host "ERROR: BH SQLite not found at $BhSqlite" -ForegroundColor Red
    Write-Host "       Run .\dev-launch.ps1 first to start both instances." -ForegroundColor Yellow
    exit 1
}
if (-not (Test-Path $WbSqlite)) {
    Write-Host "ERROR: WB SQLite not found at $WbSqlite" -ForegroundColor Red
    Write-Host "       Run .\dev-launch.ps1 first to start both instances." -ForegroundColor Yellow
    exit 1
}

# Check sqlite3 is available
try { sqlite3 --version | Out-Null } catch {
    Write-Host "ERROR: sqlite3 not found in PATH" -ForegroundColor Red
    exit 1
}

# Clean previous test data
Clean-TestOrders

# Record log positions before injection
$bhLogBefore = if (Test-Path $BhLog) { (Get-Item $BhLog).Length } else { 0 }
$wbLogBefore = if (Test-Path $WbLog) { (Get-Item $WbLog).Length } else { 0 }

# ── Inject test orders ───────────────────────────────────────────────────────

Write-Host "`nInjecting test orders..."

# Good orders
Write-Host "  pickup-bh -> BH"
Inject-Order "pickup-bh" "BH"

Write-Host "  pickup-wb -> WB"
Inject-Order "pickup-wb" "WB"

Write-Host "  shipped-bh -> BH"
Inject-Order "shipped-bh" "BH"

Write-Host "  interstore-bh-to-wb -> BH"
Inject-Order "interstore-bh-to-wb" "BH"

Write-Host "  multi-item -> BH"
Inject-Order "multi-item" "BH"

# Bad orders (expect errors)
Write-Host "  bad-missing-customer -> BH"
Inject-Order "bad-missing-customer" "BH"

Write-Host "  bad-missing-items -> BH"
Inject-Order "bad-missing-items" "BH"

Write-Host "  bad-missing-store -> BH"
Inject-Order "bad-missing-store" "BH"

# ── Wait for ingestion ───────────────────────────────────────────────────────

Write-Host "`nWaiting ${WaitSeconds}s for ingestion..." -ForegroundColor Yellow
Start-Sleep -Seconds $WaitSeconds

# ── Verify results ───────────────────────────────────────────────────────────

Write-Host "`n=== Results ===" -ForegroundColor Cyan

# 1. pickup-bh should be in BH SQLite
Write-Host "`n[pickup-bh]"
$bhPickup = Query-Sqlite $BhSqlite "SELECT external_order_id, customer_first_name, delivery_method_id FROM orders WHERE external_order_id = 'TEST-BH-001';"
Check "Order in BH SQLite" ($bhPickup -match "TEST-BH-001") "Not found in BH orders table"
Check "Customer name parsed" ($bhPickup -match "Test") "Customer name missing"
Check "Delivery = pickup (1)" ($bhPickup -match "\|1$" -or $bhPickup -match "\|1\|") "delivery_method_id not 1"

# 2. pickup-wb should be in WB SQLite
Write-Host "`n[pickup-wb]"
$wbPickup = Query-Sqlite $WbSqlite "SELECT external_order_id, customer_first_name FROM orders WHERE external_order_id = 'TEST-WB-001';"
Check "Order in WB SQLite" ($wbPickup -match "TEST-WB-001") "Not found in WB orders table"
$wbNotInBh = Query-Sqlite $BhSqlite "SELECT COUNT(*) FROM orders WHERE external_order_id = 'TEST-WB-001';"
Check "NOT in BH SQLite" ($wbNotInBh -match "^0$") "Unexpectedly found in BH"

# 3. shipped-bh
Write-Host "`n[shipped-bh]"
$shipped = Query-Sqlite $BhSqlite "SELECT external_order_id, delivery_method_id, shipping_address1 FROM orders WHERE external_order_id = 'TEST-SHIP-001';"
Check "Shipped order in BH" ($shipped -match "TEST-SHIP-001") "Not found"
Check "Delivery = ship (2)" ($shipped -match "2") "delivery_method_id not 2"

# 4. interstore — should be in BH (billing store) but current_store is WB
Write-Host "`n[interstore-bh-to-wb]"
$inter = Query-Sqlite $BhSqlite "SELECT external_order_id, pickup_store_id FROM orders WHERE external_order_id = 'TEST-INTER-001';"
Check "Inter-store order in BH" ($inter -match "TEST-INTER-001") "Not found in BH"

# 5. multi-item
Write-Host "`n[multi-item]"
$multi = Query-Sqlite $BhSqlite "SELECT external_order_id FROM orders WHERE external_order_id = 'TEST-MULTI-001';"
Check "Multi-item order in BH" ($multi -match "TEST-MULTI-001") "Not found"
$itemCount = Query-Sqlite $BhSqlite "SELECT COUNT(*) FROM order_items WHERE order_id IN (SELECT id FROM orders WHERE external_order_id = 'TEST-MULTI-001');"
Check "Has multiple items" ([int]$itemCount -gt 1) "Expected >1 items, got $itemCount"

# 6. bad-missing-customer — should trigger alert
Write-Host "`n[bad-missing-customer]"
$badCust = Query-Sqlite $BhSqlite "SELECT COUNT(*) FROM alerts WHERE message LIKE '%TEST-BAD-CUST%' OR message LIKE '%Customer name%';"
Check "Alert fired for missing customer" ([int]$badCust -gt 0) "No alert found for missing customer"

# 7. bad-missing-items — should trigger alert
Write-Host "`n[bad-missing-items]"
$badItems = Query-Sqlite $BhSqlite "SELECT COUNT(*) FROM alerts WHERE message LIKE '%TEST-BAD-ITEMS%' OR message LIKE '%print_formats%' OR message LIKE '%photo_gift_orders%';"
Check "Alert fired for missing items" ([int]$badItems -gt 0) "No alert found for missing items"

# 8. bad-missing-store — should trigger alert
Write-Host "`n[bad-missing-store]"
$badStore = Query-Sqlite $BhSqlite "SELECT COUNT(*) FROM alerts WHERE message LIKE '%TEST-BAD-STORE%' OR message LIKE '%BillingStoreId%' OR message LIKE '%CurrentStoreId%';"
Check "Alert fired for missing store" ([int]$badStore -gt 0) "No alert found for missing store"

# ── Check logs for unexpected errors ─────────────────────────────────────────

Write-Host "`n[Log check]"
if (Test-Path $BhLog) {
    $bhNewLog = Get-Content $BhLog | Select-Object -Skip ([math]::Max(0, $bhLogBefore / 100))
    $bhErrors = $bhNewLog | Where-Object { $_ -match "\[ERROR\]" -and $_ -notmatch "TEST-BAD" }
    Check "No unexpected BH errors" ($bhErrors.Count -eq 0) "$($bhErrors.Count) unexpected error(s) in BH log"
}

if (Test-Path $WbLog) {
    $wbNewLog = Get-Content $WbLog | Select-Object -Skip ([math]::Max(0, $wbLogBefore / 100))
    $wbErrors = $wbNewLog | Where-Object { $_ -match "\[ERROR\]" -and $_ -notmatch "TEST-BAD" }
    Check "No unexpected WB errors" ($wbErrors.Count -eq 0) "$($wbErrors.Count) unexpected error(s) in WB log"
}

# ── Summary ──────────────────────────────────────────────────────────────────

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
Write-Host "  Passed: $PassCount" -ForegroundColor Green
if ($FailCount -gt 0) {
    Write-Host "  Failed: $FailCount" -ForegroundColor Red
} else {
    Write-Host "  Failed: 0" -ForegroundColor Green
}
Write-Host ""

# ── Cleanup ──────────────────────────────────────────────────────────────────

if (-not $NoCleanup) {
    Clean-TestOrders
}

if ($FailCount -gt 0) { exit 1 } else { exit 0 }
