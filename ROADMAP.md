# HitePhoto PrintStation — Roadmap

**Last updated:** 2026-03-29 (Session 68)
**Read this before starting any work.**

## What This Is

PrintStation replaces both PrintRouter and IngestService as a single app. It ingests orders (Pixfizz API + Dakis folders), stores them in local SQLite, and manages the full print workflow. MariaDB sync is for inter-store communication only — PrintStation works standalone.

This is the foundation for the full HitePhoto lab management system. Every piece built here must be solid enough to survive the additions below without breaking.

## System Architecture

```
Pixfizz OHD API + FTP ──┐
                         ├──→ PrintStation (SQLite)  ←──→  MariaDB (Dell)
Dakis Folder Watch ──────┘         ↓                          ↕
                            Color Correct              Dashboard/LabApi
                                 ↓                          ↕
                            Noritsu MRK              Other Store's
                                 ↓                    PrintStation
                              Printer
```

Two stores (BH + WB), each running their own PrintStation. MariaDB on Dell server is the shared hub for inter-store visibility and the Dashboard.

## Design Principles

1. **SQLite is the local source of truth.** All reads/writes go to local SQLite. MariaDB is for sharing, not for operating.
2. **One decision maker per decision.** Each question (can this print? is it held? which channel?) has ONE function that answers it. No other code checks that state.
3. **TXT file is the ultimate authority** for order content. On verify, if TXT and database disagree, reread TXT and overwrite the database. Don't overwrite messages or history.
4. **Don't trust the database.** Check disk for file existence, check TXT for order content. The database is a record of what we think happened — the disk is reality.
5. **One function, multiple callers.** File verification (exists, >1KB, JPEG magic bytes) is one function called at ingest, verify, and print time.
6. **Every error is visible.** AlertCollector in every catch block. Operator sees plain English; developer gets method, file, line, stack trace.
7. **SOLID principles.** Single responsibility, depend on interfaces, built to survive additions without refactoring.
8. **Go slow, validate assumptions.** Step through each step one at a time. Many past problems came from building on invalid assumptions.

## Where We Are

**Last status update:** Session 68 (2026-03-29)

| Component | State |
|-----------|-------|
| **PrintStation UI** | Done — 3-tab tree, detail panel, multi-select, context menu, settings, themes, build # on titlebar |
| **Processing** | Done — NoritsuMrkWriter, LayoutProcessor (with print integration), ImagePreparer, multi-select print, mark printed/unprinted |
| **Color Correction** | Done — full pipeline, 6-up page view, CorrectionStore |
| **Email/Notify** | Done — EmailService wired via NotificationService, template chooser + preview, pickup/shipped, Send Test |
| **Alerts/Logging** | Done — AlertCollector, AppLog, alert panel, alert history, ON CONFLICT dedup, error caps on sync/verify |
| **SQLite schema** | Done — all tables including delivery_methods, shipping address columns, file_status, id_map, sync_outbox |
| **Decision makers** | Done — Hold, FilesNeeded, Channel, Fulfillment |
| **Services** | Done — Print (with layout support), Hold, Notification, OrderVerifier (file_status column, no disk I/O on refresh) |
| **Ingest (Pixfizz)** | Done — OHD API poll, FTP download, TXT parse, SQLite write, received push |
| **Ingest (Dakis)** | Done — folder watch, YML parse, shipping/delivery fields, SQLite write |
| **Channel mapping** | Done — search dropdown, CSV + DB + layout channels, click-to-assign, tree shows layout name |
| **Layout designer** | Done — new/edit/delete in Settings, live preview |
| **Layout printing** | Done — PrintService invokes LayoutProcessor, uses layout target size/channel, tree shows [Layout] label |
| **Auto-updater** | Done — startup check + Settings button, tested |
| **MariaDB sync** | Done — decorator pattern, outbox, background pull, push on write, error caps, FK guard on notes |
| **Repository consolidation** | Done — single InsertItemCore, named column access, ViewModel SQL extracted to IOrderRepository |
| **Inter-store transfers** | Not started |
| **LabApi vendors/services** | Done — migrations 012-014, CRUD endpoints, OrderDomain shared logic |

## Build Phases

### Phase 1 — Core Foundation (COMPLETE)

Walk through each workflow step by step, validate assumptions, build decision makers and services.

**Pixfizz ingest flow:**
1. Poll OHD API `/jobs/pending`
2. Check disk for existing folder — if exists, verify; if not, download
3. Download artwork + darkroom_ticket.txt from FTP
4. Parse TXT (source of truth)
5. Verify files (exists, >1KB, JPEG magic bytes)
6. Write to SQLite — insert new or compare-and-repair existing (don't overwrite messages/history, add repair note)
7. Order appears in tree
8. Background task marks `/received` after 24 hours verified

**Dakis ingest flow:**
1. Watch incoming folder for new order folders
2. Check for `metadata/download_complete` marker
3. Parse `order.yml` (YamlDotNet)
4. Verify files (same function as Pixfizz)
5. Write to SQLite (same insert-or-repair logic)
6. Order appears in tree

**Print flow:**
1. Operator selects order/size, clicks Print
2. Check hold — if held, overridable popup
3. For each `is_noritsu = 1` item: verify files on disk, check channel assigned
4. Skip items with no channel (popup: "not printed — no channel assigned")
5. Skip items with bad/missing files (popup)
6. Color correction if operator chose it
7. NoritsuMrkWriter.WriteMrk() (same for all sources)
8. Set `is_printed = 1` on items, add history note
9. When all noritsu items printed, order sorts to Printed tab

**Hold flow:**
- Toggle `is_held` in SQLite
- Add history note
- Pending/Printed tabs have identical capabilities — tabs are just sorting

**Files-needed decision:**
- Pixfizz → always yes
- Dakis → yes if this is the production store
- One function, three callers (ingest, verify, print)

**Decision makers:**
- `IHoldDecision` — reads `orders.is_held`
- `IFilesNeededDecision` — source + store determines answer
- `IChannelDecision` — routing key → channel from `channel_mappings` table
- `IPrintEligibility` — single pre-print gate, calls the others
- `IFulfillmentDecision` — in-house vs outlab vendor

### Phase 2 — Full Feature Parity with PrintRouter (COMPLETE)

- ~~Hold/release with history~~ DONE
- ~~Customer notifications (email + Pixfizz API)~~ DONE (Pixfizz API call still TODO)
- ~~Channel mapping UI (learn new mappings, edit existing)~~ DONE
- ~~Layout system (multi-up tiling for wallets etc.)~~ DONE — designer + print integration
- ~~Auto-updater~~ DONE
- ~~Settings UI for all configuration~~ DONE — including database settings tab
- PrintRouter and IngestService are ARCHIVED — PrintStation is the replacement

### Phase 3 — MariaDB Sync (COMPLETE)

- ~~Bidirectional sync connector between local SQLite and MariaDB~~ DONE
- ~~Push: decorator pattern wraps repositories, fire-and-forget after every mutating write~~ DONE
- ~~Pull: background timer, all orders/items/notes from MariaDB into local SQLite~~ DONE
- ~~Outbox pattern for offline resilience~~ DONE
- ~~Error caps on sync/verify loops (max 10 per cycle) to prevent UI lockup~~ DONE
- ~~FK guard on notes pull (skip notes for deleted local orders)~~ DONE
- ~~Only pickup store inserts new orders; all other ops push from any store~~ DONE
- ~~is_printed never downgraded on pull (local printed state wins)~~ DONE

### Phase 4 — Second Store (NEXT UP)

- BH PrintStation connected to MariaDB on same LAN
- Both stores see each other's orders via sync
- Inter-store transfers via database (hold at one store, release at another)
- Add store 3 to MariaDB for home dev machine

### Phase 5 — Order Creation (Dashboard)

- Products table (Noritsu print sizes + media types with crop proportions)
- Image upload endpoint
- Crop UI (JavaScript canvas in Blazor)
- Create order endpoint → DB → PrintStation picks up via sync
- QR code upload for customers in-store
- Email upload link for remote customers

---

## Future Phases (not yet planned in detail)

These build on the foundation above. Each will be planned when preceding phases are stable.

| Phase | What |
|-------|------|
| 6 | Walk-In POS — cash register, payments |
| 7 | Physical Job Tracking — tubs, deposits, lab workflow |
| 8 | Barcode Production Tracking |
| 9 | Commercial Accounts — net 30, custom pricing |
| 10 | Inventory |
| 11 | CRM & Promotions — coupons, loyalty |
| 12 | Time Clock |
| 13 | Customer Portal |
| — | Staff Mobile App (after Phase 5) |
| — | Reporting & QuickBooks (after Phase 5) |

---

## SQLite Tables (designed for day one)

**Mirrored (sync with MariaDB):** orders, order_items, stores, order_statuses, order_sources

**Business data:** vendors, product_categories, services (product catalog with pricing, match keys, turnaround times)

**Local only:** color_corrections, channel_mappings, sync_outbox, sync_metadata

**History:** order_history (notes, status changes, repairs, print events — never overwritten by sync)

---

## Key Decisions Made

- **PrintStation + IngestService combined** — they're too intertwined to keep separate
- **SQLite only, no direct MariaDB reads** — MariaDB sync is for inter-store sharing
- **No print_history table** — `is_printed` flag + history notes are sufficient
- **`is_noritsu` replaces `is_gift_product`** — clear name for "goes to the Noritsu printer"
- **Don't block reprints** — if operator clicks print, print it. Pending/Printed tabs are just sorting.
- **Don't trust the database for file status** — always check disk
- **Files-needed: Pixfizz=always, Dakis=only if production store** — no vendor/service lookup needed for this decision
- **Don't call `/received` immediately** — wait 24 hours verified as safety window
- **Dakis: no customer.txt needed** — all customer info is in order.yml
- **PrintRouter and IngestService archived** — replaced by PrintStation, repos kept as reference
- **Decorator pattern for sync** — wraps IOrderRepository/IHistoryRepository, zero changes to existing service code
- **is_printed drives tabs** — not status codes. Pending/Printed sorting is based on item printed state.
- **Sync pull never downgrades is_printed** — local printed state wins (MAX)
- **Error caps on loops** — sync and verify loops cap at 10 errors per cycle to prevent UI lockup
- **Layout quantities pass through 1:1** — sold as sheets (2-up, 4-up), no division math

## Repos

- **PrintStation**: `https://github.com/donhite61/HitePhoto.PrintStation.git` (active)
- **LabApi**: `https://github.com/donhite61/HitePhoto.LabApi.git` (Shared models, Dashboard, API — active)
- **TestServer**: `https://github.com/donhite61/HitePhoto.TestServer.git` (mock Pixfizz for testing)
- **PrintRouter**: `https://github.com/donhite61/PrintRouter.git` (archived — reference only)
- **IngestService**: `https://github.com/donhite61/HitePhoto.IngestService-.git` (archived — absorbed into PrintStation)
