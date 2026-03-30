using System;
using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation.Data;

/// <summary>
/// Local SQLite database for all PrintStation order data.
/// This is the SINGLE source of truth for the running app.
/// MariaDB sync happens separately via SyncConnector.
/// </summary>
public class OrderDb
{
    private readonly string _dbPath;

    public OrderDb()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HitePhoto.PrintStation");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "orders.db");
        Initialize();
    }

    /// <summary>For testing: pass an explicit path.</summary>
    public OrderDb(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Initialize();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        // Enable WAL mode for better concurrent read/write performance
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public string DbPath => _dbPath;

    /// <summary>Re-create the database after file deletion.</summary>
    public void Reinitialize() => Initialize();

    // ══════════════════════════════════════════════════════════════════════
    //  Schema initialization
    // ══════════════════════════════════════════════════════════════════════

    private void Initialize()
    {
        try
        {
            using var conn = OpenConnection();
            using var transaction = conn.BeginTransaction();

            Execute(conn, CreateLookupTables);
            Execute(conn, CreateStoresTable);
            Execute(conn, CreateStoreIdentifiersTable);
            Execute(conn, CreateVendorsTable);
            Execute(conn, CreateProductCategoriesTable);
            Execute(conn, CreateServicesTable);
            Execute(conn, CreateOrdersTable);
            Execute(conn, CreateOrderItemsTable);
            Execute(conn, CreateOrderHistoryTable);
            Execute(conn, CreateColorCorrectionsTable);
            Execute(conn, CreateChannelMappingsTable);
            Execute(conn, CreateSyncOutboxTable);
            Execute(conn, CreateSyncMetadataTable);

            SeedLookups(conn);
            RunMigrations(conn);

            transaction.Commit();

            AppLog.Info($"OrderDb initialized at {_dbPath}");
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Database,
                "Failed to initialize OrderDb",
                detail: $"Attempted: create/verify SQLite schema at '{_dbPath}'. " +
                        $"Expected: all tables created successfully. " +
                        $"Found: {ex.Message}. " +
                        $"Context: app startup. " +
                        $"State: database may be incomplete.",
                ex: ex);
        }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── Lookup tables ─────────────────────────────────────────────────────

    private const string CreateLookupTables = """
        CREATE TABLE IF NOT EXISTS order_statuses (
            id           INTEGER PRIMARY KEY,
            status_code  TEXT NOT NULL,
            display_name TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS order_sources (
            id           INTEGER PRIMARY KEY,
            source_code  TEXT NOT NULL,
            display_name TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS delivery_methods (
            id           INTEGER PRIMARY KEY,
            method_code  TEXT NOT NULL UNIQUE,
            method_name  TEXT NOT NULL,
            sort_order   INTEGER NOT NULL DEFAULT 0
        );
        """;

    // ── Stores ────────────────────────────────────────────────────────────

    private const string CreateStoresTable = """
        CREATE TABLE IF NOT EXISTS stores (
            id         INTEGER PRIMARY KEY,
            store_name TEXT NOT NULL,
            short_name TEXT DEFAULT '',
            is_local   INTEGER NOT NULL DEFAULT 0
        );
        """;

    // ── Store Identifiers ──────────────────────────────────────────────

    private const string CreateStoreIdentifiersTable = """
        CREATE TABLE IF NOT EXISTS store_identifiers (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            store_id    INTEGER NOT NULL,
            source      TEXT NOT NULL,
            external_id TEXT NOT NULL,
            FOREIGN KEY (store_id) REFERENCES stores(id),
            UNIQUE(source, external_id)
        );
        """;

    // ── Vendors ───────────────────────────────────────────────────────────

    private const string CreateVendorsTable = """
        CREATE TABLE IF NOT EXISTS vendors (
            id                         INTEGER PRIMARY KEY AUTOINCREMENT,
            vendor_name                TEXT NOT NULL,
            vendor_type                TEXT NOT NULL DEFAULT 'external',
            store_id                   INTEGER DEFAULT NULL,
            contact_name               TEXT DEFAULT '',
            contact_phone              TEXT DEFAULT '',
            contact_email              TEXT DEFAULT '',
            account_number             TEXT DEFAULT '',
            default_turnaround_minutes INTEGER DEFAULT NULL,
            default_urgency_minutes    INTEGER DEFAULT NULL,
            default_needs_files        INTEGER NOT NULL DEFAULT 1,
            notes                      TEXT DEFAULT '',
            is_active                  INTEGER NOT NULL DEFAULT 1,
            created_at                 TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                 TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    // ── Product categories ────────────────────────────────────────────────

    private const string CreateProductCategoriesTable = """
        CREATE TABLE IF NOT EXISTS product_categories (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            category_name TEXT NOT NULL UNIQUE,
            sort_order    INTEGER NOT NULL DEFAULT 0,
            created_at    TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    // ── Services (product catalog) ────────────────────────────────────────

    private const string CreateServicesTable = """
        CREATE TABLE IF NOT EXISTS services (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            service_name       TEXT NOT NULL,
            category_id        INTEGER NOT NULL REFERENCES product_categories(id),
            vendor_id          INTEGER NOT NULL REFERENCES vendors(id),
            cost_price         REAL DEFAULT NULL,
            retail_price       REAL DEFAULT NULL,
            minimum_price      REAL DEFAULT NULL,
            employee_price     REAL DEFAULT NULL,
            price_to_us        REAL DEFAULT NULL,
            turnaround_minutes INTEGER DEFAULT NULL,
            urgency_minutes    INTEGER DEFAULT NULL,
            needs_files        INTEGER DEFAULT NULL,
            match_key_dakis    TEXT DEFAULT NULL,
            match_key_pixfizz  TEXT DEFAULT NULL,
            is_active          INTEGER NOT NULL DEFAULT 1,
            created_at         TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at         TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    // ── Orders ────────────────────────────────────────────────────────────

    private const string CreateOrdersTable = """
        CREATE TABLE IF NOT EXISTS orders (
            id                        INTEGER PRIMARY KEY,
            external_order_id         TEXT NOT NULL,
            order_source_id           INTEGER NOT NULL DEFAULT 1,
            source_code               TEXT NOT NULL DEFAULT '',
            customer_first_name       TEXT DEFAULT '',
            customer_last_name        TEXT DEFAULT '',
            customer_email            TEXT DEFAULT '',
            customer_phone            TEXT DEFAULT '',
            order_status_id           INTEGER NOT NULL DEFAULT 1,
            status_code               TEXT NOT NULL DEFAULT 'new',
            is_held                   INTEGER NOT NULL DEFAULT 0,
            hold_reason               TEXT DEFAULT NULL,
            pickup_store_id           INTEGER NOT NULL,
            current_location_store_id INTEGER,
            files_local               INTEGER NOT NULL DEFAULT 0,
            total_amount              REAL DEFAULT 0,
            payment_status            TEXT DEFAULT '',
            special_instructions      TEXT DEFAULT '',
            order_type                TEXT DEFAULT '',
            is_rush                   INTEGER NOT NULL DEFAULT 0,
            is_test                   INTEGER NOT NULL DEFAULT 0,
            ordered_at                TEXT NOT NULL,
            folder_path               TEXT DEFAULT '',
            download_status           TEXT DEFAULT 'pending',
            delivery_method_id        INTEGER NOT NULL DEFAULT 1,
            shipping_first_name       TEXT NOT NULL DEFAULT '',
            shipping_last_name        TEXT NOT NULL DEFAULT '',
            shipping_address1         TEXT DEFAULT NULL,
            shipping_address2         TEXT DEFAULT NULL,
            shipping_city             TEXT DEFAULT NULL,
            shipping_state            TEXT DEFAULT NULL,
            shipping_zip              TEXT DEFAULT NULL,
            shipping_country          TEXT DEFAULT NULL,
            shipping_method           TEXT DEFAULT NULL,
            is_transfer               INTEGER NOT NULL DEFAULT 0,
            transfer_store_id         INTEGER DEFAULT NULL,
            pixfizz_job_id            TEXT DEFAULT NULL,
            is_received_pushed        INTEGER NOT NULL DEFAULT 0,
            is_notified               INTEGER NOT NULL DEFAULT 0,
            notified_at               TEXT DEFAULT NULL,
            created_at                TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at                TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(external_order_id, pickup_store_id)
        );
        """;

    // ── Order items ───────────────────────────────────────────────────────

    private const string CreateOrderItemsTable = """
        CREATE TABLE IF NOT EXISTS order_items (
            id                      INTEGER PRIMARY KEY,
            order_id                INTEGER NOT NULL REFERENCES orders(id),
            size_label              TEXT NOT NULL,
            media_type              TEXT DEFAULT '',
            category                TEXT DEFAULT '',
            sub_category            TEXT DEFAULT '',
            quantity                INTEGER NOT NULL DEFAULT 1,
            image_filename          TEXT DEFAULT '',
            image_filepath          TEXT DEFAULT '',
            original_image_filepath TEXT DEFAULT '',
            options_json            TEXT DEFAULT '[]',
            is_noritsu              INTEGER NOT NULL DEFAULT 1,
            is_printed              INTEGER NOT NULL DEFAULT 0,
            is_test                 INTEGER NOT NULL DEFAULT 0,
            service_id              INTEGER DEFAULT NULL REFERENCES services(id),
            fulfillment_vendor_id   INTEGER DEFAULT NULL REFERENCES vendors(id),
            fulfillment_status      TEXT DEFAULT 'pending',
            sent_at                 TEXT DEFAULT NULL,
            sent_by                 TEXT DEFAULT NULL,
            due_at                  TEXT DEFAULT NULL,
            received_at             TEXT DEFAULT NULL,
            match_key               TEXT DEFAULT NULL,
            files_expected          INTEGER DEFAULT NULL,
            created_at              TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at              TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE TABLE IF NOT EXISTS order_item_options (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            order_item_id INTEGER NOT NULL REFERENCES order_items(id),
            option_key    TEXT NOT NULL,
            option_value  TEXT NOT NULL,
            created_at    TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    // ── Order history (notes, status changes, repairs, prints — never overwritten by sync) ──

    private const string CreateOrderHistoryTable = """
        CREATE TABLE IF NOT EXISTS order_history (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            order_id   INTEGER NOT NULL REFERENCES orders(id),
            note       TEXT NOT NULL,
            created_by TEXT DEFAULT '',
            created_at TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    // ── Color corrections (consolidate from separate corrections.db) ─────

    private const string CreateColorCorrectionsTable = """
        CREATE TABLE IF NOT EXISTS color_corrections (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            order_id           INTEGER NOT NULL,
            image_path         TEXT NOT NULL,
            corrected_path     TEXT DEFAULT '',
            exposure           INTEGER DEFAULT 0,
            brightness         INTEGER DEFAULT 0,
            contrast           INTEGER DEFAULT 0,
            shadows            INTEGER DEFAULT 0,
            highlights         INTEGER DEFAULT 0,
            saturation         INTEGER DEFAULT 0,
            color_temp         INTEGER DEFAULT 0,
            red                INTEGER DEFAULT 0,
            green              INTEGER DEFAULT 0,
            blue               INTEGER DEFAULT 0,
            sigmoidal_contrast INTEGER DEFAULT 0,
            clahe              INTEGER DEFAULT 0,
            contrast_stretch   INTEGER DEFAULT 0,
            levels             INTEGER DEFAULT 0,
            auto_level         INTEGER DEFAULT 0,
            auto_gamma         INTEGER DEFAULT 0,
            white_balance      INTEGER DEFAULT 0,
            normalize          INTEGER DEFAULT 0,
            grayscale          INTEGER DEFAULT 0,
            sepia              INTEGER DEFAULT 0,
            created_at         TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(order_id, image_path)
        );
        """;

    // ── Channel mappings (routing rules) ──────────────────────────────────

    private const string CreateChannelMappingsTable = """
        CREATE TABLE IF NOT EXISTS channel_mappings (
            routing_key    TEXT PRIMARY KEY,
            channel_number INTEGER NOT NULL DEFAULT 0,
            layout_name    TEXT DEFAULT NULL,
            source         TEXT DEFAULT '',
            updated_at     TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """;

    // ── Sync outbox ───────────────────────────────────────────────────────

    private const string CreateSyncOutboxTable = """
        CREATE TABLE IF NOT EXISTS sync_outbox (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            table_name   TEXT NOT NULL,
            record_id    INTEGER NOT NULL,
            operation    TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            created_at   TEXT NOT NULL DEFAULT (datetime('now')),
            pushed_at    TEXT DEFAULT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_outbox_pending
            ON sync_outbox(pushed_at) WHERE pushed_at IS NULL;
        """;

    // ── Sync metadata ─────────────────────────────────────────────────────

    private const string CreateSyncMetadataTable = """
        CREATE TABLE IF NOT EXISTS sync_metadata (
            table_name   TEXT NOT NULL,
            direction    TEXT NOT NULL,
            last_sync_at TEXT NOT NULL,
            PRIMARY KEY (table_name, direction)
        );

        CREATE TABLE IF NOT EXISTS alerts (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            severity      TEXT    NOT NULL,
            category      TEXT    NOT NULL,
            summary       TEXT    NOT NULL,
            order_id      TEXT,
            detail        TEXT,
            exception     TEXT,
            source_method TEXT,
            source_file   TEXT,
            source_line   INTEGER,
            created_at    TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
            acknowledged  INTEGER NOT NULL DEFAULT 0
        );
        """;

    // ══════════════════════════════════════════════════════════════════════
    //  Seed data — lookup values that match MariaDB
    // ══════════════════════════════════════════════════════════════════════

    private static void SeedLookups(SqliteConnection conn)
    {
        // Order statuses — match MariaDB IDs exactly
        Execute(conn, """
            INSERT OR IGNORE INTO order_statuses (id, status_code, display_name) VALUES
                (1, 'new',           'New'),
                (2, 'in_progress',   'In Progress'),
                (3, 'on_hold',       'On Hold'),
                (4, 'ready',         'Ready'),
                (5, 'notified',      'Notified'),
                (6, 'picked_up',     'Picked Up'),
                (7, 'cancelled',     'Cancelled'),
                (8, 'sent_to_store', 'Sent to Store'),
                (9, 'shipped',       'Shipped');
            """);

        // Order sources — match MariaDB IDs exactly
        Execute(conn, """
            INSERT OR IGNORE INTO order_sources (id, source_code, display_name) VALUES
                (1, 'pixfizz',   'Pixfizz'),
                (2, 'dakis',     'Dakis'),
                (3, 'dashboard', 'Dashboard');
            """);

        // Delivery methods — match MariaDB IDs exactly
        Execute(conn, """
            INSERT OR IGNORE INTO delivery_methods (id, method_code, method_name, sort_order) VALUES
                (1, 'pickup',      'Pickup',      1),
                (2, 'ship',        'Ship',        2),
                (3, 'inter_store', 'Inter-Store', 3);
            """);

        // Stores — match MariaDB IDs exactly
        Execute(conn, """
            INSERT OR IGNORE INTO stores (id, store_name, short_name) VALUES
                (1, 'Bloomfield Hills', 'BH'),
                (2, 'West Bloomfield',  'WB');
            """);

        // Store identifiers — map external IDs to our store IDs
        Execute(conn, """
            INSERT OR IGNORE INTO store_identifiers (store_id, source, external_id) VALUES
                (1, 'dakis',   '881'),
                (2, 'dakis',   '882'),
                (2, 'pixfizz', 'hitephoto'),
                (1, 'pixfizz', 'hitephotobh'),
                (1, 'pixfizz', 'hitephoto_bh'),
                (2, 'pixfizz', '61f26d7f-1c65-4cb8-a44f-ab2cd8b1bafc'),
                (1, 'pixfizz', 'c3977291-4913-4bcd-a846-834f5df0b0c8');
            """);

        // In-house vendors (one per store)
        Execute(conn, """
            INSERT OR IGNORE INTO vendors (id, vendor_name, vendor_type, store_id, default_needs_files)
            VALUES
                (1, 'BH In-House', 'in_house', 1, 0),
                (2, 'WB In-House', 'in_house', 2, 0);
            """);

        // Default product categories
        Execute(conn, """
            INSERT OR IGNORE INTO product_categories (id, category_name, sort_order) VALUES
                (1, 'Prints',  1),
                (2, 'Metals',  2),
                (3, 'Canvas',  3),
                (4, 'Gifts',   4),
                (5, 'Statues', 5);
            """);
    }

    // ── Migrations ────────────────────────────────────────────────────────

    private static void RunMigrations(SqliteConnection conn)
    {
        // Migration 001: Fix Dakis orders that got store name as source_code
        // instead of "dakis". Any source_code not in (pixfizz, dakis, dashboard)
        // is a store name from the DakisOrderParser bug.
        Execute(conn, """
            UPDATE orders
            SET source_code = 'dakis', order_source_id = 2
            WHERE source_code NOT IN ('pixfizz', 'dakis', 'dashboard')
              AND source_code != '';
            """);

        // Migration 002: Strip "order " prefix from Dakis external_order_id.
        // DakisIngestService used full folder name instead of just the number.
        // Some orders exist BOTH as "order 123" and "123" — delete the prefixed
        // duplicates first, then rename the non-duplicates.
        Execute(conn, """
            DELETE FROM order_items WHERE order_id IN (
                SELECT o1.id FROM orders o1
                INNER JOIN orders o2
                    ON LTRIM(SUBSTR(o1.external_order_id, 6)) = o2.external_order_id
                    AND o1.pickup_store_id = o2.pickup_store_id
                WHERE LOWER(o1.external_order_id) LIKE 'order%'
            );
            """);
        Execute(conn, """
            DELETE FROM order_history WHERE order_id IN (
                SELECT o1.id FROM orders o1
                INNER JOIN orders o2
                    ON LTRIM(SUBSTR(o1.external_order_id, 6)) = o2.external_order_id
                    AND o1.pickup_store_id = o2.pickup_store_id
                WHERE LOWER(o1.external_order_id) LIKE 'order%'
            );
            """);
        Execute(conn, """
            DELETE FROM orders WHERE id IN (
                SELECT o1.id FROM orders o1
                INNER JOIN orders o2
                    ON LTRIM(SUBSTR(o1.external_order_id, 6)) = o2.external_order_id
                    AND o1.pickup_store_id = o2.pickup_store_id
                WHERE LOWER(o1.external_order_id) LIKE 'order%'
            );
            """);
        Execute(conn, """
            UPDATE orders
            SET external_order_id = LTRIM(SUBSTR(external_order_id, 6))
            WHERE LOWER(external_order_id) LIKE 'order%';
            """);

        // Migration 003: Add category/sub_category columns to order_items (for gift items)
        AddColumnIfMissing(conn, "order_items", "category", "TEXT DEFAULT ''");
        AddColumnIfMissing(conn, "order_items", "sub_category", "TEXT DEFAULT ''");

        // Migration 004: Add pixfizz_job_id and received tracking to orders
        AddColumnIfMissing(conn, "orders", "pixfizz_job_id", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "is_received_pushed", "INTEGER NOT NULL DEFAULT 0");

        // Migration 005: Partial unique index for alert dedup (enables ON CONFLICT upsert)
        Execute(conn, """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_alerts_dedup
                ON alerts (category, summary, COALESCE(order_id, ''))
                WHERE acknowledged = 0;
            """);

        // Migration 006: Option defaults table — operators mark boring option values as default
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS option_defaults (
                option_key   TEXT NOT NULL,
                option_value TEXT NOT NULL,
                created_at   TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (option_key, option_value)
            );
            """);

        // Migration 007: Sync infrastructure — remote_id on order_history + id_map table
        AddColumnIfMissing(conn, "order_history", "remote_id", "INTEGER DEFAULT NULL");
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS id_map (
                table_name TEXT    NOT NULL,
                local_id   INTEGER NOT NULL,
                remote_id  INTEGER NOT NULL,
                PRIMARY KEY (table_name, local_id)
            );
            """);

        // Migration 008: delivery_method_id + shipping fields on orders
        AddColumnIfMissing(conn, "orders", "delivery_method_id", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(conn, "orders", "shipping_first_name", "TEXT DEFAULT ''");
        AddColumnIfMissing(conn, "orders", "shipping_last_name", "TEXT DEFAULT ''");
        AddColumnIfMissing(conn, "orders", "shipping_address1", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "shipping_address2", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "shipping_city", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "shipping_state", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "shipping_zip", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "shipping_country", "TEXT DEFAULT NULL");
        AddColumnIfMissing(conn, "orders", "shipping_method", "TEXT DEFAULT NULL");

        // Migration 009: Drop channel_number from order_items — channel_mappings is sole authority.
        // SQLite 3.35.0+ supports ALTER TABLE DROP COLUMN (our min version is 3.46+).
        DropColumnIfExists(conn, "order_items", "channel_number");

        // Migration 010: file_status on order_items — verify writes, tree reads.
        // 0 = unchecked, 1 = OK, -1 = error (missing/invalid file)
        AddColumnIfMissing(conn, "order_items", "file_status", "INTEGER NOT NULL DEFAULT 0");

        // Migration 011: is_externally_modified on orders — set by transfer receive or LabApi edit.
        // When set, PrintService scans disk vs DB before printing and offers choice if mismatch.
        AddColumnIfMissing(conn, "orders", "is_externally_modified", "INTEGER NOT NULL DEFAULT 0");

        // Migration 012: files_local — 1 = this machine has the image files on disk.
        // Tree filters on this. Set on ingest, never cleared by sync or transfer.
        AddColumnIfMissing(conn, "orders", "files_local", "INTEGER NOT NULL DEFAULT 0");

        // Migration 013: One-time purge of all pre-existing history.
        // Old ingest/verify code spammed junk notes. Wipe the slate —
        // only operator actions (print, hold, notify) create notes going forward.
        RunOnce(conn, "013_purge_history", "DELETE FROM order_history");

        // Migration 014: is_local_production on order_items — 1 = this store produces it (files expected).
        // 0 = another store produces it (files not expected on this machine's disk).
        AddColumnIfMissing(conn, "order_items", "is_local_production", "INTEGER NOT NULL DEFAULT 1");
    }

    private static void DropColumnIfExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            { found = true; break; }
        }
        if (found)
            Execute(conn, $"ALTER TABLE {table} DROP COLUMN {column}");
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        Execute(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void RunOnce(SqliteConnection conn, string migrationId, string sql)
    {
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS migrations_applied (
                id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        using var check = conn.CreateCommand();
        check.CommandText = "SELECT 1 FROM migrations_applied WHERE id = @id";
        check.Parameters.AddWithValue("@id", migrationId);
        if (check.ExecuteScalar() != null) return;

        Execute(conn, sql);

        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO migrations_applied (id) VALUES (@id)";
        insert.Parameters.AddWithValue("@id", migrationId);
        insert.ExecuteNonQuery();
    }
}
