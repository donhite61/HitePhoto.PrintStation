using System.IO;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class TransferWindow : Window
{
    private readonly string _orderId;
    private readonly string _externalOrderId;
    private readonly string _localFolderPath;
    private readonly ITransferService _transfer;
    private readonly IOrderRepository _orders;
    private readonly AppSettings _settings;

    private string _remoteFolderPath = "";
    private bool _connected;

    public TransferWindow(
        string orderId,
        string externalOrderId,
        string localFolderPath,
        ITransferService transfer,
        IOrderRepository orders,
        AppSettings settings)
    {
        InitializeComponent();

        _orderId = orderId;
        _externalOrderId = externalOrderId;
        _localFolderPath = localFolderPath;
        _transfer = transfer;
        _orders = orders;
        _settings = settings;

        OrderHeader.Text = $"Order: {externalOrderId}";
        OrderDetail.Text = localFolderPath;

        LoadStores();
        LoadLocalFiles();
    }

    private void LoadStores()
    {
        var stores = _orders.GetStores();
        var otherStores = stores
            .Where(s => s.Id != _settings.StoreId)
            .Select(s => new StoreItem(s.Id, s.Name))
            .ToList();

        StoreCombo.ItemsSource = otherStores;
        if (otherStores.Count > 0)
            StoreCombo.SelectedIndex = 0;
    }

    private void LoadLocalFiles()
    {
        LocalFileList.Items.Clear();

        if (string.IsNullOrEmpty(_localFolderPath) || !Directory.Exists(_localFolderPath))
        {
            LocalPath.Text = "(folder not found)";
            LocalSummary.Text = "0 files";
            return;
        }

        LocalPath.Text = _localFolderPath;

        var files = Directory.GetFiles(_localFolderPath, "*", SearchOption.AllDirectories);
        long totalSize = 0;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(_localFolderPath, file);
            var info = new FileInfo(file);
            totalSize += info.Length;
            LocalFileList.Items.Add($"{relativePath}  ({FormatSize(info.Length)})");
        }

        LocalSummary.Text = $"{files.Length} files, {FormatSize(totalSize)} total";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (StoreCombo.SelectedItem is not StoreItem target)
        {
            MessageBox.Show("Select a target store.", "Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.TransferSftpHost))
        {
            MessageBox.Show("Transfer SFTP is not configured.\nGo to Settings → Transfer to set it up.",
                "Not Configured", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ConnectionStatus.Text = "Connecting...";
        RemoteFileList.Items.Clear();

        try
        {
            // Build remote path from local folder structure
            var folderName = Path.GetFileName(_localFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var parentDir = Path.GetDirectoryName(_localFolderPath);
            if (string.IsNullOrEmpty(folderName) || string.IsNullOrEmpty(parentDir))
                throw new InvalidOperationException($"Could not parse folder path: '{_localFolderPath}'");

            var parentName = Path.GetFileName(parentDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _remoteFolderPath = $"/{parentName}/{folderName}";

            RemotePath.Text = $"{_settings.TransferSftpHost}:{_remoteFolderPath}";

            using var client = CreateSftpClient();
            client.Connect();

            long totalSize = 0;
            int fileCount = 0;

            if (client.Exists(_remoteFolderPath))
            {
                LoadRemoteFilesRecursive(client, _remoteFolderPath, _remoteFolderPath, ref fileCount, ref totalSize);
            }

            client.Disconnect();

            RemoteSummary.Text = fileCount > 0
                ? $"{fileCount} files, {FormatSize(totalSize)} (will be backed up)"
                : "No existing files (clean transfer)";

            _connected = true;
            TransferBtn.IsEnabled = true;
            ConnectionStatus.Text = $"Connected to {target.Name}";
        }
        catch (Exception ex)
        {
            _connected = false;
            TransferBtn.IsEnabled = false;
            ConnectionStatus.Text = "Connection failed";
            RemoteSummary.Text = "";

            AlertCollector.Error(AlertCategory.Transfer,
                $"Failed to connect to {_settings.TransferSftpHost} for transfer",
                orderId: _externalOrderId,
                detail: $"Attempted: SFTP connect to {_settings.TransferSftpHost}:{_settings.TransferSftpPort}. " +
                        $"Expected: successful connection. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: transfer preview for order {_externalOrderId}. " +
                        $"State: remote path would be {_remoteFolderPath}.",
                ex: ex);

            MessageBox.Show($"Could not connect:\n\n{ex.Message}",
                "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadRemoteFilesRecursive(SftpClient client, string currentPath, string basePath,
        ref int fileCount, ref long totalSize)
    {
        foreach (var entry in client.ListDirectory(currentPath))
        {
            if (entry.Name is "." or "..")
                continue;

            // Skip previous backup folders
            if (entry.Name.StartsWith("pre_transfer_"))
                continue;

            if (entry.IsDirectory)
            {
                LoadRemoteFilesRecursive(client, entry.FullName, basePath, ref fileCount, ref totalSize);
            }
            else if (entry.IsRegularFile)
            {
                var relativePath = entry.FullName.Substring(basePath.Length).TrimStart('/');
                totalSize += entry.Length;
                fileCount++;
                RemoteFileList.Items.Add($"{relativePath}  ({FormatSize(entry.Length)})");
            }
        }
    }

    private async void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (StoreCombo.SelectedItem is not StoreItem target)
            return;

        if (!_connected)
        {
            MessageBox.Show("Connect first.", "Transfer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var localCount = LocalFileList.Items.Count;
        var result = MessageBox.Show(
            $"Transfer {localCount} files to {target.Name}?\n\n" +
            $"Any existing files on the remote will be backed up to a pre_transfer folder.",
            "Confirm Transfer",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        TransferBtn.IsEnabled = false;
        TransferStatus.Text = "Transferring...";

        var comment = CommentBox.Text.Trim();
        var operatorName = Environment.UserName;

        try
        {
            await Task.Run(() =>
                _transfer.TransferOrder(_orderId, target.Id, operatorName, comment));

            TransferStatus.Text = "Transfer complete";
            MessageBox.Show($"Order transferred to {target.Name}.",
                "Transfer Complete", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            TransferStatus.Text = "Transfer failed";
            TransferBtn.IsEnabled = true;

            AlertCollector.Error(AlertCategory.Transfer,
                $"Transfer failed for order {_externalOrderId}",
                orderId: _externalOrderId,
                detail: $"Attempted: transfer order {_orderId} to store {target.Id} ({target.Name}). " +
                        $"Expected: successful SFTP upload and DB update. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: operator={operatorName}, comment='{comment}'. " +
                        $"State: check if partial upload occurred — remote files may be in inconsistent state.",
                ex: ex);

            MessageBox.Show($"Transfer failed:\n\n{ex.Message}",
                "Transfer Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private SftpClient CreateSftpClient()
    {
        var client = new SftpClient(
            _settings.TransferSftpHost,
            _settings.TransferSftpPort,
            _settings.TransferSftpUsername,
            _settings.TransferSftpPassword);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        client.OperationTimeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
