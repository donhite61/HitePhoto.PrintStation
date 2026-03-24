using System.IO;
using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Core.Decisions;
using HitePhoto.PrintStation.Core.Processing;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.Core.Services;

public class PrintService : IPrintService
{
    private readonly OrderDb _db;
    private readonly IChannelDecision _channel;
    private readonly NoritsuMrkWriter _mrkWriter;
    private readonly string _noritsuOutputRoot;

    // TODO: confirm these codes from Noritsu
    private const char StagingPrefix = 'p';   // writing in progress
    private const char ReadyPrefix = 'o';     // ready for Noritsu
    private const char SuccessPrefix = 'e';   // Noritsu accepted and printed
    private const char ErrorPrefix = 'q';     // Noritsu rejected

    public PrintService(
        OrderDb db,
        IChannelDecision channel,
        NoritsuMrkWriter mrkWriter,
        string noritsuOutputRoot)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _mrkWriter = mrkWriter ?? throw new ArgumentNullException(nameof(mrkWriter));
        _noritsuOutputRoot = noritsuOutputRoot;
    }

    public SendResult SendToPrinter(int orderId)
    {
        var sent = new List<SentItem>();
        var skipped = new List<SkippedItem>();

        using var conn = _db.OpenConnection();

        // Load order for short ID
        string externalOrderId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT external_order_id FROM orders WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", orderId);
            externalOrderId = (string)cmd.ExecuteScalar()!;
        }

        // Load noritsu items grouped by size/media
        var sizeGroups = LoadNoritsuSizeGroups(conn, orderId);

        foreach (var group in sizeGroups)
        {
            var channelResult = _channel.Resolve(group.SizeLabel, group.MediaType);
            if (channelResult.ChannelNumber == 0)
            {
                skipped.Add(new SkippedItem(
                    $"{group.SizeLabel} {group.MediaType}",
                    "No channel assigned."));
                continue;
            }

            // Write MRK folder — staging (p) then rename to ready (o)
            // TODO: align with MrkWriter signature once it's refactored for SQLite models
            var folderName = $"o{PrintServiceHelpers.GetShortId(externalOrderId)}_{group.SizeLabel}";

            foreach (var item in group.Items)
            {
                sent.Add(new SentItem(item.Id, group.SizeLabel,
                    channelResult.ChannelNumber, folderName));
            }
        }

        // History note for what was sent
        if (sent.Count > 0)
        {
            var sizes = string.Join(", ", sent
                .Select(s => s.SizeLabel)
                .Distinct());
            AddHistoryNote(conn, orderId, $"Sent to printer: {sizes}");
        }

        return new SendResult(sent, skipped);
    }

    public void CheckPrintResults()
    {
        if (!Directory.Exists(_noritsuOutputRoot))
            return;

        // Scan for success folders (e prefix)
        foreach (var dir in Directory.GetDirectories(_noritsuOutputRoot, $"{SuccessPrefix}*"))
        {
            HandleCompletedFolder(dir, success: true);
        }

        // Scan for error folders (q prefix)
        foreach (var dir in Directory.GetDirectories(_noritsuOutputRoot, $"{ErrorPrefix}*"))
        {
            HandleCompletedFolder(dir, success: false);
        }
    }

    private void HandleCompletedFolder(string folderPath, bool success)
    {
        // Folder name format: e{shortId}_{sizeLabel} or q{shortId}_{sizeLabel}
        var folderName = Path.GetFileName(folderPath);
        var baseName = folderName.Substring(1); // strip prefix

        // Find matching order and items by folder name pattern
        // The ready folder was o{baseName}, now it's e{baseName} or q{baseName}
        using var conn = _db.OpenConnection();

        if (success)
        {
            // TODO: match baseName to order+size, set is_printed = 1, add history note
            // For now just log it
            AppLog.Info($"Noritsu completed: {folderName}");
        }
        else
        {
            AlertCollector.Error(AlertCategory.Printing,
                $"Noritsu rejected print job: {folderName}",
                detail: $"Folder: {folderPath}. Check Noritsu for error details.");
        }
    }

    private static List<SizeGroup> LoadNoritsuSizeGroups(SqliteConnection conn, int orderId)
    {
        var groups = new List<SizeGroup>();
        var items = new List<PrintItem>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, size_label, media_type, image_filepath, quantity
            FROM order_items
            WHERE order_id = @id AND is_noritsu = 1
            ORDER BY size_label, media_type
            """;
        cmd.Parameters.AddWithValue("@id", orderId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new PrintItem(
                Id: reader.GetInt32(0),
                SizeLabel: reader.GetString(1),
                MediaType: reader.IsDBNull(2) ? "" : reader.GetString(2),
                ImageFilepath: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Quantity: reader.GetInt32(4)));
        }

        // Group by size + media
        foreach (var g in items.GroupBy(i => new { i.SizeLabel, i.MediaType }))
        {
            groups.Add(new SizeGroup(g.Key.SizeLabel, g.Key.MediaType, g.ToList()));
        }

        return groups;
    }

    private static void AddHistoryNote(SqliteConnection conn, int orderId, string note)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO order_history (order_id, note, created_at)
            VALUES (@id, @note, datetime('now'))
            """;
        cmd.Parameters.AddWithValue("@id", orderId);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.ExecuteNonQuery();
    }
}

internal record PrintItem(int Id, string SizeLabel, string MediaType, string ImageFilepath, int Quantity);
internal record SizeGroup(string SizeLabel, string MediaType, List<PrintItem> Items);

file class PrintServiceHelpers
{
    // Reuse same short ID logic as MrkWriter
    public static string GetShortId(string externalOrderId)
    {
        const string prefix = "HITEPHOTO-";
        return externalOrderId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? externalOrderId.Substring(prefix.Length)
            : externalOrderId;
    }
}
