using System.IO;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core.Services;

public class TransferService : ITransferService
{
    private readonly IOrderRepository _orders;
    private readonly IHistoryRepository _history;
    private readonly AppSettings _settings;

    public TransferService(IOrderRepository orders, IHistoryRepository history, AppSettings settings)
    {
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void TransferOrder(int orderId, int targetStoreId, string operatorName, string comment)
    {
        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        if (string.IsNullOrEmpty(order.FolderPath) || !Directory.Exists(order.FolderPath))
        {
            throw new InvalidOperationException(
                $"Order {orderId} folder not found: '{order.FolderPath}'");
        }

        ValidateSftpSettings();

        // Get all files in the order folder (recursively)
        var allFiles = Directory.GetFiles(order.FolderPath, "*", SearchOption.AllDirectories);
        if (allFiles.Length == 0)
            throw new InvalidOperationException($"Order {orderId} folder is empty: '{order.FolderPath}'");

        var remoteFolderBase = BuildRemoteFolderPath(order.FolderPath);
        UploadFolder(order.FolderPath, remoteFolderBase, allFiles);

        // DB updates
        _orders.SetCurrentLocation(orderId, targetStoreId);
        _orders.SetExternallyModified(orderId, true);

        // Write local transfer marker
        WriteTransferMarker(order.FolderPath, targetStoreId, operatorName, comment, itemIds: null);

        // History note
        var storeName = _orders.GetStoreName(targetStoreId);
        var note = string.IsNullOrEmpty(comment)
            ? $"Transferred to {storeName} by {operatorName}"
            : $"Transferred to {storeName} by {operatorName}: {comment}";
        _history.AddNote(orderId, note, operatorName);

        AppLog.Info($"Transfer complete: order {orderId} → {storeName} ({allFiles.Length} files)");
    }

    public void TransferItems(int orderId, List<int> itemIds, int targetStoreId, string operatorName, string comment)
    {
        var order = _orders.GetOrder(orderId);
        if (order is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        if (string.IsNullOrEmpty(order.FolderPath) || !Directory.Exists(order.FolderPath))
        {
            throw new InvalidOperationException(
                $"Order {orderId} folder not found: '{order.FolderPath}'");
        }

        ValidateSftpSettings();

        var allItems = _orders.GetItems(orderId);
        var filesToTransfer = CollectItemFiles(allItems, itemIds);

        // Also include metadata + root-level text files for plain transfers
        var metadataDir = Path.Combine(order.FolderPath, "metadata");
        if (Directory.Exists(metadataDir))
            filesToTransfer.AddRange(Directory.GetFiles(metadataDir, "*", SearchOption.AllDirectories));
        foreach (var file in Directory.GetFiles(order.FolderPath))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".txt" or ".yml" or ".yaml" or ".json" or ".xml")
                filesToTransfer.Add(file);
        }
        filesToTransfer = filesToTransfer.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (filesToTransfer.Count == 0)
            throw new InvalidOperationException($"No files found to transfer for the selected {itemIds.Count} items.");

        var remoteFolderBase = BuildRemoteFolderPath(order.FolderPath);
        UploadFiles(order.FolderPath, remoteFolderBase, filesToTransfer.ToArray());

        // Write local transfer marker
        WriteTransferMarker(order.FolderPath, targetStoreId, operatorName, comment, itemIds);

        // History note
        var storeName = _orders.GetStoreName(targetStoreId);
        var note = $"Transferred {itemIds.Count} item(s) to {storeName} by {operatorName}";
        if (!string.IsNullOrEmpty(comment))
            note += $": {comment}";
        _history.AddNote(orderId, note, operatorName);

        AppLog.Info($"Transfer complete: {itemIds.Count} items from order {orderId} → {storeName} ({filesToTransfer.Count} files)");
    }

    // ── Mismatch check ────────────────────────────────────────────────

    public List<TransferMismatch> CheckTransferMismatches(int orderId)
    {
        var mismatches = new List<TransferMismatch>();

        var order = _orders.GetOrder(orderId);
        if (order is null || !order.IsExternallyModified)
            return mismatches;

        if (string.IsNullOrEmpty(order.FolderPath) || !Directory.Exists(order.FolderPath))
        {
            mismatches.Add(new TransferMismatch("(order folder)", $"Folder not found: {order.FolderPath}"));
            return mismatches;
        }

        var items = _orders.GetItems(orderId);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ImageFilepath))
                continue;

            if (!File.Exists(item.ImageFilepath))
            {
                mismatches.Add(new TransferMismatch(
                    $"{item.SizeLabel} — {item.ImageFilename}",
                    "File missing on disk"));
            }
        }

        return mismatches;
    }

    public int SendForProduction(int orderId, int targetStoreId, string operatorName, string comment,
        List<int>? itemIds = null, List<string>? folderNames = null, bool createOrder = true)
    {
        var fullOrder = _orders.GetFullOrder(orderId);
        if (fullOrder is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        if (string.IsNullOrEmpty(fullOrder.FolderPath) || !Directory.Exists(fullOrder.FolderPath))
            throw new InvalidOperationException($"Order {orderId} folder not found: '{fullOrder.FolderPath}'");

        ValidateSftpSettings();

        bool isPartial = itemIds is { Count: > 0 };
        var remoteFolderBase = BuildProductionRemotePath(fullOrder);
        var allItems = _orders.GetItems(orderId);
        var targetStoreName = _orders.GetStoreName(targetStoreId);
        var fromStoreName = _orders.GetStoreName(_settings.StoreId);

        // All SFTP operations use one connection
        var filesToTransfer = CollectItemFiles(allItems, itemIds);
        using var client = CreateSftpClient();
        client.Connect();
        try
        {
            // Upload item files
            if (filesToTransfer.Count > 0)
            {
                foreach (var localFile in filesToTransfer)
                {
                    var rel = Path.GetRelativePath(fullOrder.FolderPath, localFile);
                    var remotePath = remoteFolderBase + "/" + rel.Replace('\\', '/');
                    EnsureRemoteDirectory(client, Path.GetDirectoryName(remotePath)!.Replace('\\', '/'));
                    UploadSingleFile(client, localFile, remotePath);
                }
            }

            // Upload selected folders
            if (folderNames is { Count: > 0 })
            {
                foreach (var folder in folderNames)
                {
                    var localSub = Path.Combine(fullOrder.FolderPath, folder);
                    if (!Directory.Exists(localSub)) continue;
                    var files = Directory.GetFiles(localSub, "*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        var rel = Path.GetRelativePath(fullOrder.FolderPath, file);
                        var remotePath = remoteFolderBase + "/" + rel.Replace('\\', '/');
                        EnsureRemoteDirectory(client, Path.GetDirectoryName(remotePath)!.Replace('\\', '/'));
                        UploadSingleFile(client, file, remotePath);
                    }
                }
            }

            // Upload root metadata files
            foreach (var file in Directory.GetFiles(fullOrder.FolderPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".txt" or ".yml" or ".yaml" or ".json" or ".xml")
                {
                    var remotePath = remoteFolderBase + "/" + Path.GetFileName(file);
                    EnsureRemoteDirectory(client, remoteFolderBase);
                    UploadSingleFile(client, file, remotePath);
                }
            }

            // Upload invoice if creating order
            if (createOrder)
            {
                var invoiceItems = isPartial
                    ? allItems.Where(i => itemIds!.Contains(i.Id)).ToList()
                    : allItems;
                var invoiceText = BuildTextInvoice(fullOrder, invoiceItems, fromStoreName, comment);
                EnsureRemoteDirectory(client, remoteFolderBase);
                var invoicePath = remoteFolderBase + "/invoice.txt";
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(invoiceText));
                client.UploadFile(stream, invoicePath, canOverride: true);
            }
        }
        finally { client.Disconnect(); }

        if (!createOrder)
        {
            AppLog.Info($"SendForProduction (no order): uploaded files from {orderId} → {targetStoreName}");
            return -1;
        }

        var childFolderPath = _settings.TransferNasPrefix + remoteFolderBase.Replace('/', '\\');
        var childId = _orders.CreateAlteration(orderId, "split", comment, operatorName,
            newPickupStoreId: targetStoreId, newFolderPath: childFolderPath, itemIds: itemIds);

        if (filesToTransfer.Count == 0)
            _orders.InsertServiceItem(childId, "Transfer", childFolderPath);

        if (isPartial)
            _orders.SetItemsPrinted(itemIds!);

        WriteTransferMarker(fullOrder.FolderPath, targetStoreId, operatorName, comment, itemIds, targetStoreName);

        var itemCountNote = isPartial ? $" ({itemIds!.Count} item(s))" : "";
        var parentNote = string.IsNullOrEmpty(comment)
            ? $"Sent to {targetStoreName} for production by {operatorName}{itemCountNote}"
            : $"Sent to {targetStoreName} for production by {operatorName}{itemCountNote}: {comment}";
        _history.AddNote(orderId, parentNote, operatorName);
        _history.AddNote(childId, $"Received from {fromStoreName} for production", operatorName);

        AppLog.Info($"SendForProduction: order {orderId} → {targetStoreName}, child order {childId}");
        return childId;
    }

    public int? GetFromProduction(int orderId, bool createOrder, string operatorName, string comment,
        List<int>? itemIds = null, List<string>? folderNames = null)
    {
        var fullOrder = _orders.GetFullOrder(orderId);
        if (fullOrder is null)
            throw new InvalidOperationException($"Order {orderId} not found.");

        ValidateSftpSettings();

        if (string.IsNullOrWhiteSpace(_settings.OrderOutputPath))
            throw new InvalidOperationException("OrderOutputPath is not configured. Set it in Settings.");

        // Build remote path from the order's folder_path (stored as S:\... from the sending machine)
        var remotePath = fullOrder.FolderPath?.Replace('\\', '/').Replace(_settings.TransferNasPrefix, "") ?? "";
        if (string.IsNullOrEmpty(remotePath))
            throw new InvalidOperationException($"Order {orderId} has no folder_path — cannot locate remote files.");

        // Build local destination with date-time subfolder
        var now = DateTime.Now;
        var monthFolder = now.ToString("MM-yy MMM").ToUpper();
        var dateTimeFolder = now.ToString("MM-dd-yy HHmm");
        var lastName = fullOrder.CustomerLastName?.Trim() ?? "";
        var firstName = fullOrder.CustomerFirstName?.Trim() ?? "";
        var phone = FormatPhone(fullOrder.CustomerPhone?.Trim() ?? "");
        var customerFolder = string.IsNullOrEmpty(phone)
            ? $"{lastName} {firstName}".Trim()
            : $"{lastName} {firstName} {phone}".Trim();
        if (string.IsNullOrEmpty(customerFolder)) customerFolder = fullOrder.ExternalOrderId;

        var localBase = Path.Combine(_settings.OrderOutputPath, monthFolder, customerFolder, dateTimeFolder);
        Directory.CreateDirectory(localBase);

        // Download files
        using var client = CreateSftpClient();
        client.Connect();
        try
        {
            if (folderNames is { Count: > 0 })
            {
                DownloadFolders(client, remotePath, localBase, folderNames);
                // Partial download — also grab root-level files (metadata, invoice, etc.)
                if (RemoteDirectoryExists(client, remotePath))
                {
                    foreach (var entry in client.ListDirectory(remotePath))
                    {
                        if (entry.IsRegularFile)
                        {
                            var localFile = Path.Combine(localBase, entry.Name);
                            using var fs = File.Create(localFile);
                            client.DownloadFile(entry.FullName, fs);
                        }
                    }
                }
            }
            else
            {
                // Full download — DownloadFolder already gets root files
                DownloadFolder(client, remotePath, localBase);
            }
        }
        finally { client.Disconnect(); }

        AppLog.Info($"GetFromProduction: downloaded order {orderId} to {localBase}");

        if (!createOrder) return null;

        // Create receive alteration
        var childId = _orders.CreateAlteration(orderId, "receive", comment, operatorName,
            newPickupStoreId: _settings.StoreId, newFolderPath: localBase, itemIds: itemIds);

        // If no real items were selected, insert a Transfer service item
        var allItems = _orders.GetItems(orderId);
        var selectedItems = itemIds is { Count: > 0 }
            ? allItems.Where(i => itemIds.Contains(i.Id)).ToList()
            : allItems;

        if (selectedItems.Count == 0 || selectedItems.All(i => !i.IsNoritsu))
        {
            _orders.InsertServiceItem(childId, "Transfer", localBase);
        }

        // Print invoice
        var fromStoreName = fullOrder.StoreName ?? "Other Store";
        var invoiceItems = selectedItems.Count > 0 ? selectedItems : allItems;
        var invoiceText = BuildTextInvoice(fullOrder, invoiceItems, fromStoreName, comment);
        var invoicePath = Path.Combine(localBase, "invoice.txt");
        File.WriteAllText(invoicePath, invoiceText);

        _history.AddNote(orderId, $"Received by {operatorName}" +
            (string.IsNullOrEmpty(comment) ? "" : $": {comment}"), operatorName);
        _history.AddNote(childId, $"Received from {fromStoreName}", operatorName);

        AppLog.Info($"GetFromProduction: order {orderId} → child {childId}, local folder {localBase}");
        return childId;
    }

    // ── SFTP operations ─────────────────────────────────────────────────

    private void UploadInvoice(string remoteFolder, string invoiceText)
    {
        using var client = CreateSftpClient();
        client.Connect();
        try
        {
            EnsureRemoteDirectory(client, remoteFolder);
            var remotePath = remoteFolder + "/invoice.txt";
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(invoiceText));
            client.UploadFile(stream, remotePath, canOverride: true);
        }
        finally
        {
            client.Disconnect();
        }
    }

    private void UploadFolder(string localFolder, string remoteFolder, string[] allFiles)
    {
        using var client = CreateSftpClient();
        client.Connect();

        try
        {
            // Backup any existing files on remote before overwriting
            BackupRemoteFolder(client, remoteFolder);

            // Ensure remote directory structure exists and upload all files
            foreach (var localFile in allFiles)
            {
                var relativePath = Path.GetRelativePath(localFolder, localFile);
                var remotePath = remoteFolder + "/" + relativePath.Replace('\\', '/');

                EnsureRemoteDirectory(client, Path.GetDirectoryName(remotePath)!.Replace('\\', '/'));
                UploadSingleFile(client, localFile, remotePath);
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

    private void UploadFiles(string localFolder, string remoteFolder, string[] files)
    {
        using var client = CreateSftpClient();
        client.Connect();

        try
        {
            // Backup any existing files on remote that we're about to overwrite
            BackupRemoteFiles(client, remoteFolder, localFolder, files);

            foreach (var localFile in files)
            {
                var relativePath = Path.GetRelativePath(localFolder, localFile);
                var remotePath = remoteFolder + "/" + relativePath.Replace('\\', '/');

                EnsureRemoteDirectory(client, Path.GetDirectoryName(remotePath)!.Replace('\\', '/'));
                UploadSingleFile(client, localFile, remotePath);
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

    private void BackupRemoteFolder(SftpClient client, string remoteFolder)
    {
        if (!RemoteDirectoryExists(client, remoteFolder))
            return;

        var backupFolder = remoteFolder + $"/pre_transfer_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        EnsureRemoteDirectory(client, backupFolder);

        var entries = client.ListDirectory(remoteFolder);
        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..")
                continue;

            // Don't back up previous backup folders
            if (entry.Name.StartsWith("pre_transfer_"))
                continue;

            if (entry.IsRegularFile)
            {
                var backupPath = backupFolder + "/" + entry.Name;
                // Move = rename on same filesystem
                client.RenameFile(entry.FullName, backupPath);
            }
        }

        AppLog.Info($"Transfer: backed up existing remote files to {backupFolder}");
    }

    private void BackupRemoteFiles(SftpClient client, string remoteFolder, string localFolder, string[] localFiles)
    {
        if (!RemoteDirectoryExists(client, remoteFolder))
            return;

        var backupFolder = remoteFolder + $"/pre_transfer_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        bool backupCreated = false;

        foreach (var localFile in localFiles)
        {
            var relativePath = Path.GetRelativePath(localFolder, localFile);
            var remotePath = remoteFolder + "/" + relativePath.Replace('\\', '/');

            if (RemoteFileExists(client, remotePath))
            {
                if (!backupCreated)
                {
                    EnsureRemoteDirectory(client, backupFolder);
                    backupCreated = true;
                }

                var backupPath = backupFolder + "/" + relativePath.Replace('\\', '/');
                EnsureRemoteDirectory(client, Path.GetDirectoryName(backupPath)!.Replace('\\', '/'));
                client.RenameFile(remotePath, backupPath);
            }
        }

        if (backupCreated)
            AppLog.Info($"Transfer: backed up overwritten remote files to {backupFolder}");
    }

    public List<string> ListLocalFolders(string localPath)
    {
        var folders = new List<string>();
        if (string.IsNullOrEmpty(localPath) || !Directory.Exists(localPath)) return folders;
        foreach (var dir in Directory.GetDirectories(localPath))
        {
            var name = Path.GetFileName(dir);
            if (name is "metadata" or "prints") continue;
            folders.Add(name);
        }
        return folders;
    }

    private static void UploadSingleFile(SftpClient client, string localPath, string remotePath)
    {
        using var fs = File.OpenRead(localPath);
        client.UploadFile(fs, remotePath, canOverride: true);
    }

    /// <summary>Download all files from a remote folder recursively to a local folder.</summary>
    private void DownloadFolder(SftpClient client, string remotePath, string localPath)
    {
        if (!RemoteDirectoryExists(client, remotePath)) return;

        Directory.CreateDirectory(localPath);

        foreach (var entry in client.ListDirectory(remotePath))
        {
            if (entry.Name is "." or "..") continue;

            if (entry.IsDirectory)
            {
                DownloadFolder(client, entry.FullName, Path.Combine(localPath, entry.Name));
            }
            else if (entry.IsRegularFile)
            {
                var localFile = Path.Combine(localPath, entry.Name);
                using var fs = File.Create(localFile);
                client.DownloadFile(entry.FullName, fs);
            }
        }
    }

    /// <summary>Download specific subfolders from a remote order folder.</summary>
    private void DownloadFolders(SftpClient client, string remoteBase, string localBase, List<string> folderNames)
    {
        foreach (var folder in folderNames)
        {
            var remoteSub = remoteBase + "/" + folder;
            var localSub = Path.Combine(localBase, folder);
            DownloadFolder(client, remoteSub, localSub);
        }
    }

    /// <summary>List subfolder names in a remote directory (non-recursive).</summary>
    public List<string> ListRemoteFolders(string remotePath)
    {
        var folders = new List<string>();
        using var client = CreateSftpClient();
        client.Connect();
        try
        {
            if (!RemoteDirectoryExists(client, remotePath)) return folders;
            foreach (var entry in client.ListDirectory(remotePath))
            {
                if (entry.Name is "." or ".." or "metadata") continue;
                if (entry.IsDirectory)
                    folders.Add(entry.Name);
            }
        }
        finally { client.Disconnect(); }
        return folders;
    }

    private static void EnsureRemoteDirectory(SftpClient client, string remotePath)
    {
        // Build up path segments and create each one that doesn't exist
        var segments = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var segment in segments)
        {
            current += "/" + segment;
            if (!RemoteDirectoryExists(client, current))
                client.CreateDirectory(current);
        }
    }

    private static bool RemoteDirectoryExists(SftpClient client, string path)
    {
        try { return client.GetAttributes(path).IsDirectory; }
        catch { return false; }
    }

    private static bool RemoteFileExists(SftpClient client, string path)
    {
        try { return client.GetAttributes(path).IsRegularFile; }
        catch { return false; }
    }

    private SftpClient CreateSftpClient()
    {
        var client = new SftpClient(
            _settings.TransferSftpHost,
            _settings.TransferSftpPort,
            _settings.TransferSftpUsername,
            _settings.TransferSftpPassword);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.OperationTimeout = TimeSpan.FromMinutes(5); // Large files may take time
        return client;
    }

    private static string BuildRemoteFolderPath(string localFolderPath)
    {
        var folderName = Path.GetFileName(localFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var parentDir = Path.GetDirectoryName(localFolderPath);
        if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(parentDir))
            throw new InvalidOperationException($"Could not determine path components from '{localFolderPath}'");

        var parentRelative = Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $"/{parentRelative}/{folderName}";
    }

    private void ValidateSftpSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.TransferSftpHost))
            throw new InvalidOperationException("Transfer SFTP host is not configured. Set it in Settings.");
        if (string.IsNullOrWhiteSpace(_settings.TransferSftpUsername))
            throw new InvalidOperationException("Transfer SFTP username is not configured. Set it in Settings.");
        if (string.IsNullOrWhiteSpace(_settings.TransferSftpPassword))
            throw new InvalidOperationException("Transfer SFTP password is not configured. Set it in Settings.");
    }

    // ── Local marker ────────────────────────────────────────────────────

    private void WriteTransferMarker(string folderPath, int targetStoreId,
        string operatorName, string comment, List<int>? itemIds, string? storeName = null)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        var metadataDir = Path.Combine(folderPath, "metadata");
        Directory.CreateDirectory(metadataDir);

        storeName ??= _orders.GetStoreName(targetStoreId);
        var lines = new List<string>
        {
            $"transferred_at: {DateTime.UtcNow:O}",
            $"transferred_to: {storeName}",
            $"transferred_by: {operatorName}"
        };

        if (!string.IsNullOrEmpty(comment))
            lines.Add($"comment: {comment}");

        if (itemIds is not null)
            lines.Add($"items: {itemIds.Count} specific items");
        else
            lines.Add("items: all");

        File.WriteAllLines(Path.Combine(metadataDir, "transfer.txt"), lines);
    }

    /// <summary>
    /// Collect image files from items. When itemIds is provided, only those items; otherwise all.
    /// </summary>
    private static List<string> CollectItemFiles(List<OrderItemRecord> allItems, List<int>? itemIds = null)
    {
        var items = itemIds is { Count: > 0 }
            ? allItems.Where(i => itemIds.Contains(i.Id)).ToList()
            : allItems;

        var files = new List<string>();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.ImageFilepath) && File.Exists(item.ImageFilepath))
                files.Add(item.ImageFilepath);
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Build a plain text invoice for the production send.
    /// </summary>
    private string BuildTextInvoice(HitePhoto.Shared.Models.Order order, List<OrderItemRecord> items,
        string fromStoreName, string comment)
    {
        var lines = new List<string>();

        var name = $"{order.CustomerFirstName} {order.CustomerLastName}".Trim();
        if (!string.IsNullOrEmpty(name)) lines.Add(name);
        if (!string.IsNullOrEmpty(order.CustomerPhone)) lines.Add(order.CustomerPhone);
        if (!string.IsNullOrEmpty(order.CustomerEmail)) lines.Add(order.CustomerEmail);
        lines.Add("");
        lines.Add("TRANSFER ORDER INVOICE");
        lines.Add($"From {fromStoreName}  —  {DateTime.Now:MMM d, yyyy  h:mm tt}");
        if (!string.IsNullOrEmpty(order.StoreName))
            lines.Add($"Pickup: {order.StoreName}");
        lines.Add($"Order #: {order.ExternalOrderId}");
        if (order.OrderedAt.HasValue)
            lines.Add($"Date: {order.OrderedAt.Value:MMM d, yyyy  h:mm tt}");
        lines.Add("─────────────────────────────────────────");
        lines.Add("ITEMS");

        int totalPrints = 0;
        int totalFiles = 0;
        var groups = items.GroupBy(i => i.SizeLabel ?? "")
            .OrderBy(g => g.Key);
        foreach (var group in groups)
        {
            var qty = group.Sum(i => i.Quantity);
            var files = group.Count();
            totalPrints += qty;
            totalFiles += files;
            lines.Add($"  {group.Key,-24}  {qty} print{(qty != 1 ? "s" : "")}  ({files} file{(files != 1 ? "s" : "")})");
        }

        lines.Add("─────────────────────────────────────────");
        lines.Add($"Total: {totalPrints} prints  ({totalFiles} files)");

        if (!string.IsNullOrEmpty(comment))
        {
            lines.Add("");
            lines.Add($"Notes: {comment}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Build remote path for production sends:
    /// /AAPhoto/{MM-yy MMM}/{LastName} {FirstName} {Phone}/{MM-dd-yy HHmm}/
    /// </summary>
    private string BuildProductionRemotePath(HitePhoto.Shared.Models.Order order)
    {
        var now = DateTime.Now;
        var monthFolder = now.ToString("MM-yy MMM").ToUpper();
        var dateTimeFolder = now.ToString("MM-dd-yy HHmm");

        var lastName = order.CustomerLastName?.Trim() ?? "";
        var firstName = order.CustomerFirstName?.Trim() ?? "";
        var phone = FormatPhone(order.CustomerPhone?.Trim() ?? "");

        if (string.IsNullOrEmpty(lastName) && string.IsNullOrEmpty(firstName))
            throw new InvalidOperationException(
                $"Order {order.ExternalOrderId} has no customer name — cannot build production path.");

        var customerFolder = string.IsNullOrEmpty(phone)
            ? $"{lastName} {firstName}".Trim()
            : $"{lastName} {firstName} {phone}".Trim();
        return $"{_settings.TransferRemoteRoot}/{monthFolder}/{customerFolder}/{dateTimeFolder}";
    }

    /// <summary>Format phone as 248-390-2515. Handles raw digits or already-formatted.</summary>
    private static string FormatPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone)) return "";
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1')
            digits = digits[1..];
        if (digits.Length == 10)
            return $"{digits[..3]}-{digits[3..6]}-{digits[6..]}";
        return phone; // return as-is if not a standard 10-digit number
    }
}
