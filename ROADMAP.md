# HitePhoto PrintStation — Roadmap

**Last updated:** 2026-04-20 (Session 88)

**The canonical roadmap lives in the LabApi repo.** Read that first:
`C:\Users\dhite\source\repos\HitePhoto.LabApi\ROADMAP.md`

This file is a thin PrintStation-specific status page. All step planning, architectural direction, and future-phase decisions are in the LabApi roadmap.

## PrintStation Status (Session 88)

PrintStation is **feature-complete for Phase 1-4** and deployed to both stores. Per the Session 88 pivot documented in the LabApi roadmap, no new features will be added to PrintStation until the LabApi state-model work (Step 9a) is done. After 9a, PrintStation will migrate its tree/tab logic to read the canonical `order_statuses` column and eventually host embedded Blazor pages (via WebView2) for secondary windows.

### What's working in production
- Dakis + Pixfizz ingest
- Noritsu MRK output, color correction, layout printing
- Hold / files-needed / channel decision makers
- Mark Ready workflow (Session 87)
- Inter-store Send for Production + Get from Production (Session 88)
- Per-station TestMode filter (Session 88)
- MariaDB sync via SyncingOrderRepository decorator + outbox
- Auto-updater (checks update folder on startup)
- AlertCollector + log rotation + NAS upload of error batches

### Known issues on PrintStation side (queued for after 9a)
- `is_printed` and `display_tab` drift after Send for Production — fix is part of 9a (both columns deprecated)
- Alert system redo (9c in LabApi roadmap)
- Test system redesign (9d in LabApi roadmap)
- Send/Get chain nesting (`-S1-R1-S1-…`) — strip-logic fix planned
- Verifier flags files as missing for orders that belong to the other store — to be fixed as part of 9c alert work
- Intermittent internet outage noise (spam every 5s) — fixed by 9c exponential backoff

### What stays WPF-native (never moves to Blazor)
- Tree view (performance, right-click, keyboard)
- Offline print queue
- Noritsu MRK writing
- Local file operations (Dakis watch, file verification)
- Anything that must work with LabApi unreachable

### What migrates to embedded Blazor pages (after 9a)
- Order detail view
- Transfer / Send for Production / Get from Production dialogs
- Alert history window
- Most Settings tabs (routing, layouts, appearance, notifications)
- "In Production" monitoring view
- Future POS screens
