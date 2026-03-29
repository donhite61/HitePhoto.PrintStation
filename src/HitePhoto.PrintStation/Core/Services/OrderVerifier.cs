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
    private readonly IHistoryRepository _history;
    private readonly IFilesNeededDecision _filesNeededDecision;
    private readonly DakisOrderParser _dakisParser;
    private readonly PixfizzOrderParser _pixfizzParser;
    private readonly AppSettings _settings;

    public OrderVerifier(
        IOrderRepository orders,
        IHistoryRepository history,
        IFilesNeededDecision filesNeededDecision,
        DakisOrderParser dakisParser,
        PixfizzOrderParser pixfizzParser,
        AppSettings settings)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _filesNeededDecision = filesNeededDecision ?? throw new ArgumentNullException(nameof(filesNeededDecision));
        _dakisParser = dakisParser ?? throw new ArgumentNullException(nameof(dakisParser));
        _pixfizzParser = pixfizzParser ?? throw new ArgumentNullException(nameof(pixfizzParser));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public VerifyResult VerifyRecentOrders(int days)
    {
        var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;

        var folderList = new Dictionary<string, (string Path, string Source)>(StringComparer.OrdinalIgnoreCase);
        ScanFoldersIntoList(_settings.OrderOutputPath, "pixfizz", cutoff, folderList);
        ScanFoldersIntoList(_settings.DakisWatchFolder, "dakis", cutoff, folderList);

        var dbList = _orders.GetRecentOrders(_settings.StoreId, days);

        return VerifyOrders(folderList, dbList);
    }

    public VerifyResult VerifyOrder(string externalOrderId, string folderPath,
        string sourceCode, int dbOrderId)
    {
        var folderList = new Dictionary<string, (string Path, string Source)>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(folderPath))
            folderList[externalOrderId] = (folderPath, sourceCode);

        var dbList = new Dictionary<string, (int Id, string FolderPath, string SourceCode)>(StringComparer.OrdinalIgnoreCase)
        {
            [externalOrderId] = (dbOrderId, folderPath, sourceCode)
        };

        return VerifyOrders(folderList, dbList);
    }

    public VerifyResult VerifyOrders(
        Dictionary<string, (string Path, string Source)> folderList,
        Dictionary<string, (int Id, string FolderPath, string SourceCode)> dbList)
    {
        int inserted = 0, repaired = 0, errors = 0;
        int matchCount = 0;

        // ── Reconcile: orders in BOTH lists ──
        var matched = folderList.Keys.Intersect(dbList.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var orderId in matched)
        {
            var folder = folderList[orderId];
            var db = dbList[orderId];

            OrderSource source;
            try { source = OrderSourceExtensions.FromCode(db.SourceCode); }
            catch (Exception ex)
            {
                AlertCollector.Error(AlertCategory.DataQuality,
                    $"Verify: unrecognized source code '{db.SourceCode}' for order {orderId}",
                    orderId: orderId,
                    detail: $"Attempted: parse source code '{db.SourceCode}'. " +
                            $"Expected: pixfizz, dakis, or dashboard. Found: '{db.SourceCode}'. " +
                            $"Context: order {orderId} (db id={db.Id}). " +
                            $"State: skipping order verification",
                    ex: ex);
                folderList.Remove(orderId); dbList.Remove(orderId); matchCount++; continue;
            }

            bool filesRequired = _filesNeededDecision.AreFilesRequired(source, _settings.StoreId, _settings.StoreId);

            if (filesRequired)
            {
                // Verify files on disk using OrderHelpers.VerifyFile (the ONE verification function)
                var dbItems = _orders.GetItems(db.Id);
                var itemIssues = new List<string>();
                foreach (var item in dbItems)
                {
                    if (string.IsNullOrWhiteSpace(item.ImageFilepath)) continue;
                    var error = OrderHelpers.VerifyFile(item.ImageFilepath);
                    if (error != null)
                    {
                        var filename = string.IsNullOrEmpty(item.ImageFilename)
                            ? Path.GetFileName(item.ImageFilepath)
                            : item.ImageFilename;
                        itemIssues.Add($"{filename}: {error}");
                    }
                }

                // Source-specific repair: reread source file and compare with DB
                if (source == OrderSource.Pixfizz)
                {
                    var txtPath = Path.Combine(folder.Path, "darkroom_ticket.txt");
                    if (File.Exists(txtPath))
                    {
                        var repairResult = RepairFromTxt(db.Id, folder.Path, txtPath);
                        if (repairResult > 0) repaired += repairResult;
                    }
                }
                else if (source == OrderSource.Dakis)
                {
                    var ymlPath = Path.Combine(folder.Path, "order.yml");
                    if (File.Exists(ymlPath))
                    {
                        var repairResult = RepairFromYml(db.Id, folder.Path, ymlPath);
                        if (repairResult > 0) repaired += repairResult;
                    }
                }

                if (itemIssues.Count > 0)
                {
                    var note = $"Verify: {itemIssues.Count} file issue(s) — {string.Join("; ", itemIssues.Take(5))}";
                    _history.AddNoteIfNew(db.Id, note, "system");
                    errors += itemIssues.Count;
                }
            }

            folderList.Remove(orderId);
            dbList.Remove(orderId);
            matchCount++;
        }

        // ── Leftover in folder list: on disk but not in DB → insert ──
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
                AlertCollector.Error(AlertCategory.Parsing,
                    $"Verify insert failed for {kvp.Key}",
                    orderId: kvp.Key, ex: ex);
                errors++;
            }
        }

        // ── Leftover in DB list: in DB but not on disk ──
        // Only alert if the folder_path is under a local root AND actually missing.
        // Orders outside the date cutoff may not appear in the folder scan but still exist on disk.
        foreach (var kvp in dbList)
        {
            var folderPath = kvp.Value.FolderPath;
            if (!IsLocalFolder(folderPath))
                continue; // synced from other store, folder lives on their machine

            if (Directory.Exists(folderPath))
                continue; // folder exists, just outside the scan's date cutoff

            _history.AddNoteIfNew(kvp.Value.Id, "Verify: order folder not found on disk", "system");
            AlertCollector.Error(AlertCategory.DataQuality,
                $"Order {kvp.Key} in DB but folder missing from disk",
                orderId: kvp.Key,
                detail: $"Attempted: find folder for order {kvp.Key}. Expected: folder at '{folderPath}'. " +
                        $"Found: folder missing. Context: source {kvp.Value.SourceCode}, DB id {kvp.Value.Id}. " +
                        $"State: order is in database but files are gone.");
            errors++;
        }

        AppLog.Info($"Verify complete: {matchCount} matched, {inserted} inserted, {repaired} repaired, {errors} errors");
        return new VerifyResult(matchCount, inserted, repaired, errors);
    }

    // ── TXT-vs-DB compare-and-repair (Pixfizz — TXT is source of truth) ──

    private int RepairFromTxt(int dbOrderId, string folderPath, string txtPath)
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

    private int RepairFromYml(int dbOrderId, string folderPath, string ymlPath)
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

            var parsed = _dakisParser.Parse(raw);
            return CompareAndRepair(dbOrderId, parsed.Items, "order.yml");
        }
        catch (Exception ex)
        {
            AlertCollector.Error(AlertCategory.Parsing,
                $"YML repair failed for order {dbOrderId}", ex: ex);
            return 0;
        }
    }

    // ── Shared compare-and-repair: source file is truth, replace DB items ──

    private int CompareAndRepair(int dbOrderId, List<UnifiedOrderItem> sourceItems, string sourceFileName)
    {
        var dbItems = _orders.GetItems(dbOrderId);

        // Quick check: if items already match, skip the replace
        bool needsReplace = dbItems.Count != sourceItems.Count;
        if (!needsReplace)
        {
            for (int i = 0; i < sourceItems.Count; i++)
            {
                var src = sourceItems[i];
                var db = dbItems.FirstOrDefault(d =>
                    string.Equals(d.SizeLabel, src.SizeLabel, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetFileNameWithoutExtension(d.ImageFilename),
                                  Path.GetFileNameWithoutExtension(src.ImageFilename),
                                  StringComparison.OrdinalIgnoreCase));

                if (db == null ||
                    !string.Equals(db.ImageFilename, src.ImageFilename, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(db.ImageFilepath, src.ImageFilepath, StringComparison.OrdinalIgnoreCase) ||
                    db.Quantity != src.Quantity ||
                    db.IsNoritsu != src.IsNoritsu)
                {
                    needsReplace = true;
                    break;
                }
            }
        }

        if (!needsReplace) return 0;

        // Build diff summary before replacing
        var dbSet = dbItems.Select(d => $"{d.SizeLabel}|{d.ImageFilename}|qty={d.Quantity}").OrderBy(s => s).ToList();
        var srcSet = sourceItems.Select(s => $"{s.SizeLabel}|{s.ImageFilename}|qty={s.Quantity}").OrderBy(s => s).ToList();
        var removed = dbSet.Except(srcSet).ToList();
        var added = srcSet.Except(dbSet).ToList();

        var diffParts = new List<string>();
        if (removed.Count > 0) diffParts.Add($"Removed: {string.Join(", ", removed)}");
        if (added.Count > 0) diffParts.Add($"Added: {string.Join(", ", added)}");
        var diffSummary = diffParts.Count > 0 ? string.Join("; ", diffParts) : "paths updated";

        _orders.ReplaceItems(dbOrderId, sourceItems);
        _history.AddNote(dbOrderId,
            $"Items replaced from {sourceFileName} ({dbItems.Count}→{sourceItems.Count}): {diffSummary}",
            "system");
        return sourceItems.Count;
    }

    // ── Insert from disk (order on disk but not in DB) ──

    private void InsertPixfizzFromDisk(string dir)
    {
        var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
        var txtContent = File.ReadAllText(txtPath);
        var folderName = Path.GetFileName(dir);

        var raw = new RawOrder(
            ExternalOrderId: folderName,
            SourceName: "pixfizz",
            RawData: txtContent,
            Metadata: new Dictionary<string, string> { ["folder_path"] = dir });

        var order = _pixfizzParser.Parse(raw);

        var existingId = _orders.FindOrderId(order.ExternalOrderId, _settings.StoreId);
        if (existingId != null) return;

        var orderId = _orders.InsertOrder(order, _settings.StoreId);
        _history.AddNote(orderId, $"Order received at {DateTime.Now:g} (discovered by verify)");
    }

    private void InsertDakisFromDisk(string externalOrderId, string dir)
    {
        var ymlContent = File.ReadAllText(Path.Combine(dir, "order.yml"));
        var raw = new RawOrder(externalOrderId, "dakis", ymlContent,
            new Dictionary<string, string> { ["folder_path"] = dir });
        var order = _dakisParser.Parse(raw);

        var existingId = _orders.FindOrderId(order.ExternalOrderId, _settings.StoreId);
        if (existingId != null) return;

        var orderId = _orders.InsertOrder(order, _settings.StoreId);
        _history.AddNote(orderId, $"Order received at {DateTime.Now:g} (discovered by verify)");
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
                catch (Exception ex)
                {
                    AppLog.Info($"Verify: TXT parse failed for {txtPath}, using folder name as fallback: {ex.Message}");
                }
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

    /// <summary>
    /// Returns true if the folder path is under one of our configured local roots.
    /// Orders synced from the other store have folder paths on that store's drive.
    /// </summary>
    private bool IsLocalFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;

        var normalized = Path.GetFullPath(folderPath);

        if (!string.IsNullOrWhiteSpace(_settings.OrderOutputPath))
        {
            var pixRoot = Path.GetFullPath(_settings.OrderOutputPath);
            if (normalized.StartsWith(pixRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(_settings.DakisWatchFolder))
        {
            var dakisRoot = Path.GetFullPath(_settings.DakisWatchFolder);
            if (normalized.StartsWith(dakisRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
