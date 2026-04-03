using System.Windows;
using System.Windows.Controls;
using HitePhoto.PrintStation.Core;
using HitePhoto.PrintStation.Core.Services;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.UI;

public partial class SendForProductionWindow : Window
{
    private readonly string _orderId;
    private readonly string _externalOrderId;
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

    public SendForProductionWindow(
        string orderId,
        string externalOrderId,
        string folderPath,
        ITransferService transfer,
        IOrderRepository orders,
        AppSettings settings,
        List<string>? preSelectedItemIds = null)
    {
        InitializeComponent();

        _orderId = orderId;
        _externalOrderId = externalOrderId;
        _transfer = transfer;
        _orders = orders;
        _settings = settings;

        OrderHeader.Text = $"Order: {externalOrderId}";
        OrderDetail.Text = folderPath;

        LoadStores();
        LoadItems(preSelectedItemIds);
        LoadLocalFolders(folderPath);
        UpdateSummary();
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

    private void LoadItems(List<string>? preSelectedItemIds)
    {
        var items = _orders.GetItems(_orderId);
        if (items.Count == 0)
        {
            AlertCollector.Error(AlertCategory.Transfer,
                $"No items found for order {_externalOrderId}",
                orderId: _externalOrderId,
                detail: $"Attempted: load items for SendForProduction dialog. " +
                        $"Expected: at least 1 item. Found: 0. " +
                        $"Context: orderId={_orderId}. State: dialog will be empty.");
            return;
        }

        _totalItemCount = items.Count;
        bool selectAll = preSelectedItemIds == null;

        // Group by (SizeLabel, MediaType)
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
                Content = $"{group.Key.SizeLabel} -- {group.Key.MediaType}  ({fileCount} file{(fileCount != 1 ? "s" : "")}, {totalQty} print{(totalQty != 1 ? "s" : "")})",
                IsThreeState = true,
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

            // Individual items in a collapsible panel
            var itemsPanel = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            foreach (var item in groupItems)
            {
                bool shouldCheck = selectAll || preSelectedItemIds!.Contains(item.Id);

                var itemCb = new CheckBox
                {
                    Content = $"{item.ImageFilename}  (qty {item.Quantity})",
                    IsChecked = shouldCheck,
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
            group.GroupCheckBox.IsChecked = null; // indeterminate
        _updatingCheckboxes = false;
    }

    private void UpdateSummary()
    {
        int selectedItems = GetSelectedItemIds().Count;
        int selectedFolders = GetSelectedFolderNames().Count;
        var parts = new List<string>();
        if (selectedItems > 0) parts.Add($"{selectedItems} item{(selectedItems != 1 ? "s" : "")}");
        if (selectedFolders > 0) parts.Add($"{selectedFolders} folder{(selectedFolders != 1 ? "s" : "")}");
        SummaryText.Text = parts.Count > 0 ? $"Sending {string.Join(", ", parts)}" : "Nothing selected";
        SendBtn.IsEnabled = (selectedItems > 0 || selectedFolders > 0) && StoreCombo.SelectedItem != null;
    }

    private List<string> GetSelectedItemIds()
    {
        var ids = new List<string>();
        foreach (var group in _sizeGroups)
            foreach (var (item, cb) in group.Items)
                if (cb.IsChecked == true)
                    ids.Add(item.Id);
        return ids;
    }

    private void LoadLocalFolders(string folderPath)
    {
        var folders = _transfer.ListLocalFolders(folderPath);
        foreach (var folder in folders)
        {
            var cb = new CheckBox
            {
                Content = folder,
                IsChecked = false,
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

    private List<string> GetSelectedFolderNames()
    {
        return _folderCheckboxes
            .Where(f => f.CheckBox.IsChecked == true)
            .Select(f => f.Name)
            .ToList();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _sizeGroups)
            group.GroupCheckBox.IsChecked = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var group in _sizeGroups)
            group.GroupCheckBox.IsChecked = false;
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (StoreCombo.SelectedItem is not StoreItem target)
            return;

        var selectedIds = GetSelectedItemIds();
        if (selectedIds.Count == 0)
            return;

        bool isPartial = selectedIds.Count < _totalItemCount;
        var itemLabel = isPartial ? $"{selectedIds.Count} item(s)" : "all items";

        var result = MessageBox.Show(
            $"Send {itemLabel} to {target.Name} for production?",
            "Confirm Send",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        SendBtn.IsEnabled = false;
        SummaryText.Text = "Sending...";

        var comment = CommentBox.Text.Trim();
        var operatorName = Environment.UserName;

        List<string>? itemIds = isPartial ? selectedIds : null;
        var selectedFolders = GetSelectedFolderNames();
        List<string>? folderNames = selectedFolders.Count > 0 ? selectedFolders : null;
        bool createOrder = CreateOrderCheck.IsChecked == true;

        try
        {
            await Task.Run(() =>
                _transfer.SendForProduction(_orderId, target.Id, operatorName, comment, itemIds, folderNames, createOrder));

            SummaryText.Text = "Sent successfully";
            MessageBox.Show($"Order sent to {target.Name} for production.",
                "Send Complete", MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Send failed";
            SendBtn.IsEnabled = true;

            AlertCollector.Error(AlertCategory.Transfer,
                $"Send for production failed for order {_externalOrderId}",
                orderId: _externalOrderId,
                detail: $"Attempted: send {itemLabel} from order {_orderId} to store {target.Id} ({target.Name}). " +
                        $"Expected: child order created, files uploaded. " +
                        $"Found: {ex.GetType().Name}: {ex.Message}. " +
                        $"Context: operator={operatorName}, comment='{comment}', partial={isPartial}. " +
                        $"State: child order may exist in DB — check order_links. SFTP may be incomplete.",
                ex: ex);

            MessageBox.Show($"Send failed:\n\n{ex.Message}",
                "Send Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
