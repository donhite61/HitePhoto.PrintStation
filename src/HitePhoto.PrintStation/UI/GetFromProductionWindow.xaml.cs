using System.Windows;
using System.Windows.Controls;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class GetFromProductionWindow : Window
{
    private readonly int _orderId;
    private readonly string _externalOrderId;
    private readonly string _remotePath;
    private readonly ITransferService _transfer;
    private readonly IOrderRepository _orders;
    private readonly AppSettings _settings;

    private readonly List<SizeGroup> _sizeGroups = new();
    private readonly List<(string Name, CheckBox CheckBox)> _folderCheckboxes = new();
    private int _totalItemCount;
    private bool _updatingCheckboxes;

    private class SizeGroup
    {
        public string SizeLabel { get; init; } = "";
        public string MediaType { get; init; } = "";
        public CheckBox GroupCheckBox { get; init; } = null!;
        public List<(OrderItemRecord Item, CheckBox CheckBox)> Items { get; } = new();
    }

    public GetFromProductionWindow(
        int orderId,
        string externalOrderId,
        string folderPath,
        ITransferService transfer,
        IOrderRepository orders,
        AppSettings settings)
    {
        InitializeComponent();

        _orderId = orderId;
        _externalOrderId = externalOrderId;
        _transfer = transfer;
        _orders = orders;
        _settings = settings;

        // Remote path is derived from the order's folder_path (stored as S:\... from the sending store)
        _remotePath = folderPath?.Replace('\\', '/').Replace("S:", "") ?? "";

        OrderHeader.Text = $"Order: {externalOrderId}";
        OrderDetail.Text = folderPath;

        LoadRemoteFolders();
        LoadItems();
        UpdateSummary();
    }

    private void LoadRemoteFolders()
    {
        if (string.IsNullOrEmpty(_remotePath))
        {
            FolderStatus.Text = "No remote path available";
            return;
        }

        try
        {
            FolderStatus.Text = "Scanning remote folders...";
            var folders = _transfer.ListRemoteFolders(_remotePath);

            if (folders.Count == 0)
            {
                FolderStatus.Text = "No folders found on remote";
                return;
            }

            FolderStatus.Text = "";
            foreach (var folder in folders)
            {
                var cb = new CheckBox
                {
                    Content = folder,
                    IsChecked = true,
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
                };
                cb.Checked += (_, _) => UpdateSummary();
                cb.Unchecked += (_, _) => UpdateSummary();
                _folderCheckboxes.Add((folder, cb));
                FolderPanel.Children.Add(cb);
            }
        }
        catch (Exception ex)
        {
            FolderStatus.Text = $"Failed to scan: {ex.Message}";
            AppLog.Info($"GetFromProduction: SFTP folder scan failed for {_remotePath}: {ex.Message}");
        }
    }

    private void LoadItems()
    {
        var items = _orders.GetItems(_orderId);
        _totalItemCount = items.Count;

        if (items.Count == 0)
        {
            ItemPanel.Children.Add(new TextBlock
            {
                Text = "No items on this order (folders only)",
                FontStyle = FontStyles.Italic,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                FontSize = 12
            });
            return;
        }

        var grouped = items
            .GroupBy(i => (i.SizeLabel, i.MediaType))
            .OrderBy(g => g.Key.SizeLabel)
            .ThenBy(g => g.Key.MediaType);

        foreach (var group in grouped)
        {
            var groupItems = group.ToList();
            var fileCount = groupItems.Count;
            var totalQty = groupItems.Sum(i => i.Quantity);

            var groupCb = new CheckBox
            {
                Content = $"{group.Key.SizeLabel}  ({fileCount} file{(fileCount != 1 ? "s" : "")}, {totalQty} print{(totalQty != 1 ? "s" : "")})",
                IsThreeState = true,
                IsChecked = true,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            };

            var sizeGroup = new SizeGroup
            {
                SizeLabel = group.Key.SizeLabel,
                MediaType = group.Key.MediaType,
                GroupCheckBox = groupCb
            };

            groupCb.Checked += (_, _) => { if (!_updatingCheckboxes) SetGroupItems(sizeGroup, true); };
            groupCb.Unchecked += (_, _) => { if (!_updatingCheckboxes) SetGroupItems(sizeGroup, false); };

            var itemsPanel = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            foreach (var item in groupItems)
            {
                var itemCb = new CheckBox
                {
                    Content = $"{item.ImageFilename}  (qty {item.Quantity})",
                    IsChecked = true,
                    Tag = item.Id,
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary")
                };
                itemCb.Checked += (_, _) => { UpdateGroupCheckBox(sizeGroup); UpdateSummary(); };
                itemCb.Unchecked += (_, _) => { UpdateGroupCheckBox(sizeGroup); UpdateSummary(); };

                sizeGroup.Items.Add((item, itemCb));
                itemsPanel.Children.Add(itemCb);
            }

            var expander = new Expander
            {
                Header = groupCb,
                Content = itemsPanel,
                IsExpanded = false,
                Margin = new Thickness(0, 4, 0, 0)
            };

            ItemPanel.Children.Add(expander);
            _sizeGroups.Add(sizeGroup);
            UpdateGroupCheckBox(sizeGroup);
        }
    }

    private void SetGroupItems(SizeGroup group, bool isChecked)
    {
        foreach (var (_, cb) in group.Items)
            cb.IsChecked = isChecked;
    }

    private void UpdateGroupCheckBox(SizeGroup group)
    {
        int checkedCount = group.Items.Count(i => i.CheckBox.IsChecked == true);
        _updatingCheckboxes = true;
        if (checkedCount == group.Items.Count)
            group.GroupCheckBox.IsChecked = true;
        else if (checkedCount == 0)
            group.GroupCheckBox.IsChecked = false;
        else
            group.GroupCheckBox.IsChecked = null;
        _updatingCheckboxes = false;
    }

    private void UpdateSummary()
    {
        int selectedItems = GetSelectedItemIds().Count;
        int selectedFolders = GetSelectedFolderNames().Count;
        var parts = new List<string>();
        if (selectedItems > 0) parts.Add($"{selectedItems} item{(selectedItems != 1 ? "s" : "")}");
        if (selectedFolders > 0) parts.Add($"{selectedFolders} folder{(selectedFolders != 1 ? "s" : "")}");
        SummaryText.Text = parts.Count > 0 ? $"Getting {string.Join(", ", parts)}" : "Nothing selected";
        GetBtn.IsEnabled = selectedItems > 0 || selectedFolders > 0;
    }

    private List<int> GetSelectedItemIds()
    {
        var ids = new List<int>();
        foreach (var group in _sizeGroups)
            foreach (var (item, cb) in group.Items)
                if (cb.IsChecked == true)
                    ids.Add(item.Id);
        return ids;
    }

    private List<string> GetSelectedFolderNames()
    {
        return _folderCheckboxes
            .Where(f => f.CheckBox.IsChecked == true)
            .Select(f => f.Name)
            .ToList();
    }

    private async void Get_Click(object sender, RoutedEventArgs e)
    {
        var selectedIds = GetSelectedItemIds();
        var selectedFolders = GetSelectedFolderNames();

        if (selectedIds.Count == 0 && selectedFolders.Count == 0)
            return;

        bool createOrder = CreateOrderCheck.IsChecked == true;

        var confirm = MessageBox.Show(
            $"Download files from remote and{(createOrder ? " create receive order?" : " save locally?")}",
            "Confirm Get",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        GetBtn.IsEnabled = false;
        SummaryText.Text = "Downloading...";

        var comment = CommentBox.Text.Trim();
        var operatorName = Environment.UserName;
        List<int>? itemIds = selectedIds.Count > 0 && selectedIds.Count < _totalItemCount ? selectedIds : null;
        List<string>? folderNames = selectedFolders.Count > 0 ? selectedFolders : null;

        try
        {
            await Task.Run(() =>
                _transfer.GetFromProduction(_orderId, createOrder, operatorName, comment, itemIds, folderNames));

            SummaryText.Text = "Download complete";
            MessageBox.Show("Files downloaded successfully.",
                "Get Complete", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Download failed";
            GetBtn.IsEnabled = true;

            AlertCollector.Error(AlertCategory.Transfer,
                $"Get from production failed for order {_externalOrderId}",
                orderId: _externalOrderId,
                detail: $"Attempted: download from {_remotePath}. " +
                        $"Expected: files downloaded, child order created. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: operator={operatorName}, createOrder={createOrder}. " +
                        $"State: partial download may exist on disk.",
                ex: ex);

            MessageBox.Show($"Download failed:\n\n{ex.Message}",
                "Get Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
