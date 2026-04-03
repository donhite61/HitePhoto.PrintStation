#!/bin/bash
# MariaDB Sync Test Suite
# Run while both BH and WB PrintStation instances are running

BH_DB="/s/HitePhotoTest/BH/sqlite/orders.db"
WB_DB="/s/HitePhotoTest/WB/sqlite/orders.db"
SAMPLE="$(dirname "$0")/dev/sample-orders"
BH_WATCH="/s/HitePhotoTest/BH/dakis-watch"
WB_WATCH="/s/HitePhotoTest/WB/dakis-watch"

PASS=0
FAIL=0

check() {
    local name="$1"
    local result="$2"
    local expected="$3"
    if [ "$result" = "$expected" ]; then
        echo "  PASS: $name (got: $result)"
        ((PASS++))
    else
        echo "  FAIL: $name (expected: $expected, got: $result)"
        ((FAIL++))
    fi
}

check_gt() {
    local name="$1"
    local result="$2"
    local threshold="$3"
    if [ "$result" -gt "$threshold" ] 2>/dev/null; then
        echo "  PASS: $name (got: $result)"
        ((PASS++))
    else
        echo "  FAIL: $name (expected > $threshold, got: $result)"
        ((FAIL++))
    fi
}

q() {
    sqlite3 "$1" "$2" 2>/dev/null
}

inject() {
    local src="$1"
    local dest="$2"
    # Copy folder WITHOUT marker, then add marker after delay (triggers watcher properly)
    cp -r "$src" "$dest"
    local marker="$dest/metadata/download_complete"
    rm -f "$marker" 2>/dev/null
    sleep 1
    touch "$marker"
}

echo ""
echo "============================================================"
echo " TEST 1: Inject BH order -> appears in BH + syncs to WB"
echo "============================================================"

inject "$SAMPLE/pickup-bh" "$BH_WATCH/pickup-bh"
echo "  Injected pickup-bh -> BH. Waiting 20s for ingest + sync..."
sleep 20

bh_test=$(q "$BH_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-BH-001';")
check "TEST-BH-001 in BH SQLite" "$bh_test" "1"

bh_source=$(q "$BH_DB" "SELECT source_code FROM orders WHERE external_order_id='TEST-BH-001';")
check "BH source = dakis" "$bh_source" "dakis"

bh_store=$(q "$BH_DB" "SELECT pickup_store_id FROM orders WHERE external_order_id='TEST-BH-001';")
check "BH pickup_store = 1" "$bh_store" "1"

wb_test=$(q "$WB_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-BH-001';")
check "TEST-BH-001 synced to WB SQLite" "$wb_test" "1"

bh_pending=$(q "$BH_DB" "SELECT COUNT(*) FROM sync_outbox WHERE pushed_at IS NULL;")
check "BH outbox empty" "$bh_pending" "0"

bh_idmap=$(q "$BH_DB" "SELECT COUNT(*) FROM id_map WHERE table_name='orders';")
check_gt "BH id_map has entries" "$bh_idmap" "0"

echo ""
echo "============================================================"
echo " TEST 2: Inject WB order -> appears in WB + syncs to BH"
echo "============================================================"

inject "$SAMPLE/pickup-wb" "$WB_WATCH/pickup-wb"
echo "  Injected pickup-wb -> WB. Waiting 20s..."
sleep 20

wb_local=$(q "$WB_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-WB-001';")
check "TEST-WB-001 in WB SQLite" "$wb_local" "1"

bh_pulled=$(q "$BH_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-WB-001';")
check "TEST-WB-001 synced to BH SQLite" "$bh_pulled" "1"

wb_pending=$(q "$WB_DB" "SELECT COUNT(*) FROM sync_outbox WHERE pushed_at IS NULL;")
check "WB outbox empty" "$wb_pending" "0"

echo ""
echo "============================================================"
echo " TEST 3: Both stores see all orders"
echo "============================================================"

bh_total=$(q "$BH_DB" "SELECT COUNT(*) FROM orders;")
wb_total=$(q "$WB_DB" "SELECT COUNT(*) FROM orders;")
check "BH and WB have same total" "$bh_total" "$wb_total"

bh_wb_orders=$(q "$BH_DB" "SELECT COUNT(*) FROM orders WHERE pickup_store_id=2;")
check_gt "BH sees WB orders (Other Store tab)" "$bh_wb_orders" "0"

wb_bh_orders=$(q "$WB_DB" "SELECT COUNT(*) FROM orders WHERE pickup_store_id=1;")
check_gt "WB sees BH orders (Other Store tab)" "$wb_bh_orders" "0"

echo ""
echo "============================================================"
echo " TEST 4: Multi-item order + items synced"
echo "============================================================"

inject "$SAMPLE/multi-item" "$BH_WATCH/multi-item"
echo "  Injected multi-item -> BH. Waiting 20s..."
sleep 20

bh_multi=$(q "$BH_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-MULTI-001';")
check "multi-item in BH" "$bh_multi" "1"

bh_items=$(q "$BH_DB" "SELECT COUNT(*) FROM order_items WHERE order_id IN (SELECT id FROM orders WHERE external_order_id='TEST-MULTI-001');")
check_gt "BH has multiple items" "$bh_items" "1"

wb_multi=$(q "$WB_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-MULTI-001';")
check "multi-item synced to WB" "$wb_multi" "1"

wb_items=$(q "$WB_DB" "SELECT COUNT(*) FROM order_items WHERE order_id IN (SELECT id FROM orders WHERE external_order_id='TEST-MULTI-001');")
check_gt "WB has items for multi-item" "$wb_items" "0"

echo ""
echo "============================================================"
echo " TEST 5: Shipped order preserves delivery method"
echo "============================================================"

inject "$SAMPLE/shipped-bh" "$BH_WATCH/shipped-bh"
echo "  Injected shipped-bh -> BH. Waiting 20s..."
sleep 20

bh_ship=$(q "$BH_DB" "SELECT delivery_method_id FROM orders WHERE external_order_id='TEST-SHIP-001';")
check "BH shipped delivery=2" "$bh_ship" "2"

wb_ship=$(q "$WB_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-SHIP-001';")
check "Shipped synced to WB" "$wb_ship" "1"

echo ""
echo "============================================================"
echo " TEST 6: Inter-store order"
echo "============================================================"

inject "$SAMPLE/interstore-bh-to-wb" "$BH_WATCH/interstore-bh-to-wb"
echo "  Injected interstore -> BH. Waiting 20s..."
sleep 20

bh_inter=$(q "$BH_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-INTER-001';")
check "inter-store in BH" "$bh_inter" "1"

wb_inter=$(q "$WB_DB" "SELECT COUNT(*) FROM orders WHERE external_order_id='TEST-INTER-001';")
check "inter-store synced to WB" "$wb_inter" "1"

echo ""
echo "============================================================"
echo " TEST 7: History notes exist"
echo "============================================================"

bh_notes=$(q "$BH_DB" "SELECT COUNT(*) FROM order_history WHERE order_id IN (SELECT id FROM orders WHERE external_order_id='TEST-BH-001');")
check_gt "BH has notes for TEST-BH-001" "$bh_notes" "0"

echo ""
echo "============================================================"
echo " TEST 8: sync_metadata timestamps"
echo "============================================================"

bh_pull_ts=$(q "$BH_DB" "SELECT COUNT(*) FROM sync_metadata WHERE table_name='orders' AND direction='pull' AND last_sync_at != '';")
check "BH has pull timestamp" "$bh_pull_ts" "1"

wb_pull_ts=$(q "$WB_DB" "SELECT COUNT(*) FROM sync_metadata WHERE table_name='orders' AND direction='pull' AND last_sync_at != '';")
check "WB has pull timestamp" "$wb_pull_ts" "1"

echo ""
echo "============================================================"
echo " TEST 9: Bad order does not crash sync"
echo "============================================================"

inject "$SAMPLE/bad-missing-customer" "$BH_WATCH/bad-missing-customer"
echo "  Injected bad order -> BH. Waiting 15s..."
sleep 15

alive=$(tasklist 2>&1 | grep -c "HitePhoto.PrintStation")
check "Both instances still running" "$alive" "2"

echo ""
echo "============================================================"
echo " TEST 10: No unexpected errors in logs"
echo "============================================================"

bh_sync_errors=$(grep -ci "MariaDB sync.*failed\|SyncService.*exception" /s/HitePhotoTest/BH/logs/printstation.log 2>/dev/null; echo $?)
if [ "$bh_sync_errors" = "1" ]; then bh_sync_errors="0"; else bh_sync_errors="1"; fi
check "No BH sync errors in log" "$bh_sync_errors" "0"

wb_sync_errors=$(grep -ci "MariaDB sync.*failed\|SyncService.*exception" /s/HitePhotoTest/WB/logs/printstation.log 2>/dev/null; echo $?)
if [ "$wb_sync_errors" = "1" ]; then wb_sync_errors="0"; else wb_sync_errors="1"; fi
check "No WB sync errors in log" "$wb_sync_errors" "0"

echo ""
echo "============================================================"
echo " SUMMARY"
echo "============================================================"
echo "  Passed: $PASS"
echo "  Failed: $FAIL"
echo ""
if [ "$FAIL" -eq 0 ]; then
    echo "  ALL TESTS PASSED"
else
    echo "  $FAIL TEST(S) FAILED"
fi
