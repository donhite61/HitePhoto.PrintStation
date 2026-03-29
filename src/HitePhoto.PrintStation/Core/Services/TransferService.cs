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

        // Get the specific item records to find their files
        var allItems = _orders.GetItems(orderId);
        var selectedItems = allItems.Where(i => itemIds.Contains(i.Id)).ToList();

        if (selectedItems.Count == 0)
            throw new InvalidOperationException($"None of the {itemIds.Count} item IDs found on order {orderId}.");

        // Collect files to transfer: each item's image + any metadata files
        var filesToTransfer = new List<string>();
        foreach (var item in selectedItems)
        {
            if (!string.IsNullOrEmpty(item.ImageFilepath) && File.Exists(item.ImageFilepath))
                filesToTransfer.Add(item.ImageFilepath);
        }

        // Always include metadata files (darkroom_ticket.txt, order.yml, etc.)
        var metadataDir = Path.Combine(order.FolderPath, "metadata");
        if (Directory.Exists(metadataDir))
        {
            filesToTransfer.AddRange(Directory.GetFiles(metadataDir, "*", SearchOption.AllDirectories));
        }

        // Include any root-level non-image files (TXT, YML, JSON)
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

    // ── SFTP operations ─────────────────────────────────────────────────

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

    private static void UploadSingleFile(SftpClient client, string localPath, string remotePath)
    {
        using var fs = File.OpenRead(localPath);
        client.UploadFile(fs, remotePath, canOverride: true);
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
        string operatorName, string comment, List<int>? itemIds)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return;

        var metadataDir = Path.Combine(folderPath, "metadata");
        Directory.CreateDirectory(metadataDir);

        var storeName = _orders.GetStoreName(targetStoreId);
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
}
