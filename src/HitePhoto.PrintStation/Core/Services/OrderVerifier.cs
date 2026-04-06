using System.IO;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Core.Ingest;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.Data.Repositories;
using HitePhoto.Shared.Models;
using YamlDotNet.Serialization;
using OrderSource = HitePhoto.PrintStation.Core.Models.OrderSource;

namespace HitePhoto.PrintStation.Core.Services;

/// <summary>
/// Single verify-and-repair implementation. Pure orchestrator — calls parsers,
/// OrderHelpers, and repository methods. No direct SQL.
/// All callers use this — no other code compares source files to the database.
/// </summary>
public class OrderVerifier : IOrderVerifier
{
    private readonly IOrderRepository _orders;
    private readonly IFilesNeededDecision _filesNeededDecision;
    private readonly DakisIngestService _dakisIngest;
    private readonly IngestOrderWriter _writer;
    private readonly PixfizzOrderParser _pixfizzParser;
    private readonly AppSettings _settings;

    public OrderVerifier(
        IOrderRepository orders,
        IHistoryRepository history,
        IFilesNeededDecision filesNeededDecision,
        DakisIngestService dakisIngest,
        IngestOrderWriter writer,
        PixfizzOrderParser pixfizzParser,
        AppSettings settings)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        // history parameter kept for DI compatibility but no longer used — verify events go to AppLog
        _filesNeededDecision = filesNeededDecision ?? throw new ArgumentNullException(nameof(filesNeededDecision));
        _dakisIngest = dakisIngest ?? throw new ArgumentNullException(nameof(dakisIngest));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _pixfizzParser = pixfizzParser ?? throw new ArgumentNullException(nameof(pixfizzParser));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public VerifyResult VerifyRecentOrders(int days)
    {
        if (days <= 0) return new VerifyResult(0, 0, 0, 0);

        // Suspend alert persistence during verify — parsers alert on every missing file
        // and hundreds of SQLite writes lock up the UI thread.
        AlertCollector.SuspendPersistence = true;
        try
        {
            var cutoff = DateTime.Now.AddDays(-days);

            var folderList = new Dictionary<string, (string Path, string Source)>(StringComparer.OrdinalIgnoreCase);
            ScanFoldersIntoList(_settings.OrderOutputPath, "pixfizz", cutoff, folderList);
            ScanFoldersIntoList(_settings.DakisWatchFolder, "dakis", cutoff, folderList);

            var dbList = _orders.GetRecentOrders(days, _settings.StoreId);

            return VerifyOrders(folderList, dbList);
        }
        finally
        {
            AlertCollector.SuspendPersistence = false;
        }
    }

    public VerifyResult VerifyOrder(string externalOrderId, string folderPath,
        string sourceCode, string dbOrderId)
    {
        var repaired = RepairOrder(dbOrderId, folderPath, sourceCode);
        return new VerifyResult(1, 0, repaired, 0);
    }

    public VerifyResult VerifyOrders(
        Dictionary<string, (string Path, string Source)> folderList,
        Dictionary<string, (string Id, string FolderPath, string SourceCode)> dbList)
    {
        int inserted = 0, errors = 0;
        int matchCount = 0;

        // ── Orders in BOTH lists: already in DB, update file statuses + re-verify split state ──
        var matched = folderList.Keys.Intersect(dbList.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var orderId in matched)
        {
            var dbOrderId = dbList[orderId].Id;
            UpdateFileStatuses(dbOrderId);

            // Re-run ingest for Dakis orders to ensure multi-fulfiller state is correct
            // (idempotent — EnsureParentOrder finds existing, SetDisplayTab runs, child skipped if exists)
            if (folderList[orderId].Source == "dakis")
            {
                try { InsertDakisFromDisk(orderId, folderList[orderId].Path); }
                catch (Exception ex) { AppLog.Error($"Verify re-check failed for {orderId}: {ex.Message}"); }
            }

            folderList.Remove(orderId);
            dbList.Remove(orderId);
            matchCount++;
        }

        // ── Leftover in folder list: on disk but not in DB → insert ──
        int insertErrors = 0;
        foreach (var kvp in folderList)
        {
            try
            {
                if (kvp.Value.Source == "pixfizz")
                    InsertPixfizzFromDisk(kvp.Value.Path);
                else
                    InsertDakisFromDisk(kvp.Key, kvp.Value.Path);
                inserted++;
            }
            catch (Exception ex)
            {
                insertErrors++;
                if (insertErrors <= 10)
                {
                    AlertCollector.Error(AlertCategory.Parsing,
                        $"Verify insert failed for {kvp.Key}",
                        orderId: kvp.Key, ex: ex);
                }
                if (insertErrors == 10)
                {
                    AlertCollector.Error(AlertCategory.Parsing,
                        $"Verify: too many insert errors ({insertErrors}+), skipping remaining");
                    break;
                }
                errors++;
            }
        }

        // Leftover in DB list = local orders whose folder wasn't found on disk.
        if (dbList.Count > 0)
            AppLog.Info($"Verify: {dbList.Count} local orders with no folder on disk");

        AppLog.Info($"Verify complete: {matchCount} matched, {inserted} inserted, {errors} errors");
        return new VerifyResult(matchCount, inserted, 0, errors);
    }

    /// <summary>
    /// Full repair for a single order: compare source file to DB, fix mismatches, update file statuses.
    /// Called on click (Printed tab) and periodically (Pending tab).
    /// </summary>
    public int RepairOrder(string dbOrderId, string folderPath, string sourceCode)
    {
        // Child orders share the parent's folder — the parent's verify covers the files.
        // Repairing a child against the parent's source file would add items from other stores.
        if (_orders.GetParentOrder(dbOrderId) != null)
            return 0;

        int repairs = 0;
        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
        {
            if (sourceCode.Equals("pixfizz", StringComparison.OrdinalIgnoreCase))
            {
                var txtPath = Path.Combine(folderPath, "darkroom_ticket.txt");
                if (File.Exists(txtPath))
                    repairs = RepairFromTxt(dbOrderId, folderPath, txtPath);
            }
            else
            {
                var ymlPath = Path.Combine(folderPath, "order.yml");
                if (File.Exists(ymlPath))
                    repairs = RepairFromYml(dbOrderId, folderPath, ymlPath);
            }
        }

        UpdateFileStatuses(dbOrderId);
        return repairs;
    }

    /// <summary>
    /// Check each item's file on disk and write file_status to DB.
    /// Called during verify so tree can read status from DB without disk I/O.
    /// </summary>
    private void UpdateFileStatuses(string dbOrderId)
    {
        var items = _orders.GetItems(dbOrderId);
        var updates = new List<(string ItemId, int Status)>();

        foreach (var item in items)
        {
            if (!item.IsLocalProduction)
            {
                updates.Add((item.Id, 1)); // not our files — always OK
                continue;
            }

            var error = OrderHelpers.VerifyFile(item.ImageFilepath);
            updates.Add((item.Id, error == null ? 1 : -1));
        }

        if (updates.Count > 0)
            _orders.BatchUpdateFileStatus(updates);
    }

    // ── TXT-vs-DB compare-and-repair (Pixfizz — TXT is source of truth) ──

    private int RepairFromTxt(string dbOrderId, string folderPath, string txtPath)
    {
        try
        {
            var txtContent = File.ReadAllText(txtPath);
            var orderId = Path.GetFileName(folderPath);

            var raw = new RawOrder(
                ExternalOrderId: orderId,
                SourceName: "pixfizz",
                RawData: txtContent,
                Metadata: new Dictionary<string, string> { ["folder_path"] = folderPath });

            var parsed = _pixfizzParser.Parse(raw);
            return CompareAndRepair(dbOrderId, parsed.Items, "darkroom_ticket.txt");
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"TXT repair failed for order {dbOrderId}", ex: ex);
            return 0;
        }
    }

    // ── YML-vs-DB compare-and-repair (Dakis — order.yml is source of truth) ──

    private int RepairFromYml(string dbOrderId, string folderPath, string ymlPath)
    {
        try
        {
            var ymlContent = File.ReadAllText(ymlPath);
            var orderId = Path.GetFileName(folderPath);

            var raw = new RawOrder(
                ExternalOrderId: orderId,
                SourceName: "dakis",
                RawData: ymlContent,
                Metadata: new Dictionary<string, string> { ["folder_path"] = folderPath });

            var parsed = _dakisIngest.Parser.Parse(raw);
            return CompareAndRepair(dbOrderId, parsed.Items, "order.yml");
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"YML repair failed for order {dbOrderId}", ex: ex);
            return 0;
        }
    }

    // ── Shared compare-and-repair: source items vs DB items ──

    private int CompareAndRepair(string dbOrderId, List<UnifiedOrderItem> sourceItems, string sourceFileName)
    {
        var dbItems = _orders.GetItems(dbOrderId);

        int repairs = 0;
        var unmatched = new List<UnifiedOrderItem>(sourceItems);

        foreach (var dbItem in dbItems)
        {
            var match = unmatched.FirstOrDefault(t =>
                string.Equals(t.ImageFilename, dbItem.ImageFilename, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.SizeLabel, dbItem.SizeLabel, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                unmatched.Remove(match);

                bool needsRepair = false;
                if (!string.Equals(match.MediaType ?? "", dbItem.MediaType ?? "", StringComparison.OrdinalIgnoreCase)) needsRepair = true;
                if (match.Quantity != dbItem.Quantity) needsRepair = true;
                if (!string.Equals(match.ImageFilepath ?? "", dbItem.ImageFilepath ?? "", StringComparison.OrdinalIgnoreCase)) needsRepair = true;
                if (match.IsNoritsu != dbItem.IsNoritsu) needsRepair = true;

                if (needsRepair)
                {
                    var matchCategory = match.Options.FirstOrDefault(o => o.Key == "Category")?.Value ?? "";
                    var matchSubCategory = match.Options.FirstOrDefault(o => o.Key == "SubCategory")?.Value ?? "";
                    _orders.UpdateItem(dbItem.Id,
                        match.SizeLabel ?? "", match.MediaType ?? "",
                        match.ImageFilename ?? "", match.ImageFilepath ?? "",
                        match.Quantity, match.IsNoritsu, matchCategory, matchSubCategory);
                    repairs++;
                }
            }
        }

        // Source items not in DB — insert them
        foreach (var missing in unmatched)
        {
            _orders.InsertItem(dbOrderId, missing);
            repairs++;
        }

        if (repairs > 0)
            AppLog.Info($"Repaired order {dbOrderId}: {repairs} item(s) updated from {sourceFileName}");

        return repairs;
    }

    // ── Insert from disk (order on disk but not in DB) ──

    private void InsertPixfizzFromDisk(string dir)
    {
        var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
        if (!File.Exists(txtPath)) return;

        var txtContent = File.ReadAllText(txtPath);
        var folderName = Path.GetFileName(dir);
        var raw = new RawOrder(
            ExternalOrderId: folderName,
            SourceName: "pixfizz",
            RawData: txtContent,
            Metadata: new Dictionary<string, string> { ["folder_path"] = dir });

        var order = _pixfizzParser.Parse(raw);
        _writer.WriteToSqlite(order, _settings.StoreId, "pixfizz", order.FolderPath ?? "");
    }

    private void InsertDakisFromDisk(string externalOrderId, string dir)
    {
        _dakisIngest.IngestOrder(externalOrderId, dir);
    }

    // ── Folder scanning ──

    private static void ScanFoldersIntoList(string root, string source, DateTime cutoff,
        Dictionary<string, (string Path, string Source)> list)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            bool hasOrderFile = source == "pixfizz"
                ? File.Exists(Path.Combine(dir, "darkroom_ticket.txt"))
                : File.Exists(Path.Combine(dir, "order.yml"));
            if (!hasOrderFile) continue;

            if (cutoff > DateTime.MinValue && new DirectoryInfo(dir).LastWriteTime < cutoff) continue;

            var folderName = Path.GetFileName(dir);
            string? orderId = null;

            if (source == "pixfizz")
            {
                var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
                try
                {
                    foreach (var line in File.ReadLines(txtPath))
                    {
                        if (line.StartsWith("ExtOrderNum=", StringComparison.OrdinalIgnoreCase))
                        {
                            orderId = line[12..].Trim();
                            break;
                        }
                        if (line.StartsWith("Orderid=", StringComparison.OrdinalIgnoreCase))
                        {
                            orderId = line[8..].Trim();
                            break;
                        }
                    }
                }
                catch { /* use folder name as fallback */ }
            }
            else if (source == "dakis")
            {
                // Read :id: from order.yml — source of truth, not folder name
                var ymlPath = Path.Combine(dir, "order.yml");
                try
                {
                    var yaml = File.ReadAllText(ymlPath);
                    var deserializer = new DeserializerBuilder().Build();
                    var ymlRoot = deserializer.Deserialize<object>(yaml);
                    if (ymlRoot is Dictionary<object, object> dict)
                    {
                        if (dict.TryGetValue(":id:", out var val) || dict.TryGetValue(":id", out val))
                            orderId = val?.ToString()?.Trim().Trim('"');
                    }
                }
                catch (Exception ex)
                {
                    AlertCollector.Error(AlertCategory.Parsing,
                        $"Failed to read :id: from order.yml in {folderName}",
                        orderId: folderName, ex: ex);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(orderId))
                {
                    AlertCollector.Error(AlertCategory.DataQuality,
                        $"Dakis order.yml missing :id: in {folderName}",
                        orderId: folderName,
                        detail: $"Attempted: read :id: from order.yml. Expected: numeric order ID. " +
                                $"Found: empty/missing. Context: folder scan '{dir}'. " +
                                $"State: order skipped — cannot identify without :id:.");
                    continue;
                }
            }

            // Fallback to folder name only if source file didn't yield an ID
            orderId ??= folderName;

            list.TryAdd(orderId, (dir, source));
        }
    }
}
