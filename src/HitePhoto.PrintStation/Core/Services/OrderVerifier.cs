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
    private readonly AppSettings _settings;

    public OrderVerifier(
        IOrderRepository orders,
        IHistoryRepository history,
        IFilesNeededDecision filesNeededDecision,
        DakisOrderParser dakisParser,
        AppSettings settings)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _filesNeededDecision = filesNeededDecision ?? throw new ArgumentNullException(nameof(filesNeededDecision));
        _dakisParser = dakisParser ?? throw new ArgumentNullException(nameof(dakisParser));
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
            catch { folderList.Remove(orderId); dbList.Remove(orderId); matchCount++; continue; }

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
                    _history.AddNote(db.Id, note, "system");
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

        // ── Leftover in DB list: in DB but not on disk → error state ──
        foreach (var kvp in dbList)
        {
            _history.AddNote(kvp.Value.Id, "Verify: order folder not found on disk", "system");
            AlertCollector.Warn(AlertCategory.DataQuality,
                $"Order {kvp.Key} in DB but folder missing from disk",
                orderId: kvp.Key);
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

            var artworkDir = Path.Combine(folderPath, "artwork");
            var localFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(artworkDir))
                foreach (var f in Directory.GetFiles(artworkDir, "*.jpg"))
                    localFiles[Path.GetFileName(f)] = f;

            var parsed = HitePhoto.Shared.Parsers.PixfizzTxtParser.ParseContent(txtContent, ftpPath =>
            {
                var fname = Path.GetFileName(ftpPath);
                return localFiles.TryGetValue(fname, out var local) ? local : ftpPath;
            });
            if (parsed == null) return 0;

            var sourceItems = TxtItemConverter.ToUnifiedItems(parsed);
            return CompareAndRepair(dbOrderId, sourceItems, "darkroom_ticket.txt");
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

    // ── Shared compare-and-repair: source items vs DB items ──

    private int CompareAndRepair(int dbOrderId, List<UnifiedOrderItem> sourceItems, string sourceFileName)
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
                if (!string.Equals(match.MediaType, dbItem.MediaType, StringComparison.OrdinalIgnoreCase)) needsRepair = true;
                if (match.Quantity != dbItem.Quantity) needsRepair = true;
                if (!string.Equals(match.ImageFilepath, dbItem.ImageFilepath, StringComparison.OrdinalIgnoreCase)) needsRepair = true;
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
            _history.AddNote(dbOrderId,
                $"Repaired at {DateTime.Now:yyyy-MM-dd HH:mm} — {repairs} item(s) updated from {sourceFileName}",
                "system");

        return repairs;
    }

    // ── Insert from disk (order on disk but not in DB) ──

    private void InsertPixfizzFromDisk(string dir)
    {
        var txtPath = Path.Combine(dir, "darkroom_ticket.txt");
        var txtContent = File.ReadAllText(txtPath);

        var artworkDir = Path.Combine(dir, "artwork");
        var localFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(artworkDir))
            foreach (var f in Directory.GetFiles(artworkDir, "*.jpg"))
                localFiles[Path.GetFileName(f)] = f;

        var parsed = HitePhoto.Shared.Parsers.PixfizzTxtParser.ParseContent(txtContent, ftpPath =>
        {
            var fname = Path.GetFileName(ftpPath);
            return localFiles.TryGetValue(fname, out var local) ? local : ftpPath;
        });
        if (parsed == null) throw new InvalidOperationException("TXT parse returned null");

        var items = TxtItemConverter.ToUnifiedItems(parsed);

        var order = new UnifiedOrder
        {
            ExternalOrderId = parsed.OrderId ?? Path.GetFileName(dir),
            ExternalSource = "pixfizz",
            CustomerFirstName = parsed.FirstName,
            CustomerLastName = parsed.LastName,
            CustomerEmail = parsed.Email,
            OrderedAt = DateTime.TryParse(parsed.ReceivedAt, out var dt) ? dt : DateTime.Now,
            Notes = parsed.Notes,
            FolderPath = dir,
            Location = parsed.Location,
            DownloadStatus = "complete",
            Items = items
        };

        // Check if already in DB (may exist outside the verify date range)
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

        // Check after parse — parser resolves the real ID from the YML
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
