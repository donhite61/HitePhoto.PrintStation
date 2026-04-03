# HitePhoto.PrintStation

## Session Start
Read these files before starting any work:
1. **ROADMAP.md** — master plan (shared with LabApi + PrintRouter)
2. **Architecture docs** — in LabApi repo at `docs/architecture/`

Do not start work that contradicts the roadmap without discussing it first.

## What This Is
DB-first WPF print queue app. Replaces PrintRouter's disk-scanning approach with direct MariaDB queries. Reads orders written by IngestService, displays them in a 3-tab tree view (Pending / Printed / Other Store), and manages the print workflow.

PrintRouter stays installed as the offline fallback — if DB is ever down, open PrintRouter (it still reads from disk).

## Tech Stack
- .NET 10.0 (WPF, Windows desktop)
- Dapper 2.1.72 + MySqlConnector 2.5.0 (direct MariaDB access)
- HitePhoto.Shared (project reference from LabApi repo — Order, OrderItem, OrderNote models)

## Solution Structure
- **src/HitePhoto.PrintStation/** — the WPF app
  - `Data/PrintStationDb.cs` — Dapper repository for all DB queries
  - `Core/` — AlertCollector, AppLog (ported from PrintRouter)
  - `UI/` — MainWindow (3-tab tree), SettingsWindow, themes
  - `UI/ViewModels/` — OrderTreeItem, SizeTreeItem (tree node view models)

## DB Connection
MariaDB on Dell server at 192.168.1.149:3306, database `hitephoto`, user `labapi`.
Dapper `MatchNamesWithUnderscores = true` maps snake_case columns to PascalCase properties.

Store IDs: BH=1, WB=2. Status IDs: new=1, in_progress=2, on_hold=3, ready=4, notified=5, picked_up=6, cancelled=7, sent_to_store=8, shipped=9.

## Sync Field Ownership — CRITICAL
The ingesting machine OWNS its orders in SQLite. MariaDB is a relay between stores, not an authority.

**Sync pull INSERT** (new order from another store): Set all fields — we don't own this order.
**Sync pull UPDATE** (existing local order): Only update shared fields (status, customer info, hold state). NEVER overwrite: `folder_path`, `harvested_by_store_id`, `is_printed`, `supersedes`, `alteration_type`, `is_test`.
**Sync push** (local → MariaDB): Send everything — MariaDB needs the full picture for other stores.

This is enforced in `SyncService.cs` via split bind methods: `BindPullUpdateParams` (whitelist of safe fields) and `BindPullInsertParams` (all fields). Do not merge these methods or add locally-owned fields to the UPDATE path.

See: `project_alteration_system.md`, `project_sync_authority.md` in memory.

## Build & Run
```bash
dotnet build
dotnet run --project src/HitePhoto.PrintStation
```

## Build Phases
- Phase 1: Core shell (DONE) — 3-tab tree view, DB queries, settings UI, file verification
- Phase 2: Printing — port NoritsuMrkWriter, channel assignment, MRK output
- Phase 3: Color correction — port from PrintRouter
- Phase 4: Notifications & transfers — email, Pixfizz API, SFTP
- Phase 5: Polish — auto-updater, themes, alert window, dev mode

## Related Repos
- **LabApi**: `donhite61/HitePhoto.LabApi.git` — Shared models + REST API + Dashboard
- **PrintRouter**: `donhite61/PrintRouter.git` — legacy disk-based app (offline fallback)
- **IngestService**: `donhite61/HitePhoto.IngestService-.git` — downloads orders → DB
