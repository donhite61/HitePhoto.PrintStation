using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HitePhoto.PrintStation.Core;
using HitePhoto.Shared.Models;

namespace HitePhoto.PrintStation.UI.ViewModels;

/// <summary>
/// Represents a size group (size_label + media_type) in the tree.
/// Groups order_items by size/media for display under an order node.
/// </summary>
public class SizeTreeItem : INotifyPropertyChanged
{
    private string _sizeLabel = "";
    private string _mediaType = "";
    private int _imageCount;
    private int _printedCount;
    private int _missingFileCount;
    private int? _channelNumber;
    private string _channelName = "";
    private string? _layoutName;
    private string _displayOptions = "";
    private bool _isSelected;

    public OrderTreeItem? ParentOrder { get; set; }

    public string SizeLabel
    {
        get => _sizeLabel;
        set { _sizeLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    /// <summary>Options key — used for routing. NOT displayed directly.</summary>
    public string MediaType
    {
        get => _mediaType;
        set { _mediaType = value; OnPropertyChanged(); }
    }

    /// <summary>Non-default options for display only. Set during tree build.</summary>
    public string DisplayOptions
    {
        get => _displayOptions;
        set { _displayOptions = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int ImageCount
    {
        get => _imageCount;
        set { _imageCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CountLabel)); }
    }

    public int PrintedCount
    {
        get => _printedCount;
        set { _printedCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(PrintedLabel)); OnPropertyChanged(nameof(IsPrinted)); }
    }

    public int MissingFileCount
    {
        get => _missingFileCount;
        set { _missingFileCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMissing)); OnPropertyChanged(nameof(MissingLabel)); }
    }

    public int? ChannelNumber
    {
        get => _channelNumber;
        set { _channelNumber = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChannelLabel)); OnPropertyChanged(nameof(IsUnmapped)); }
    }

    public string ChannelName
    {
        get => _channelName;
        set { _channelName = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChannelLabel)); }
    }

    public string? LayoutName
    {
        get => _layoutName;
        set { _layoutName = value; OnPropertyChanged(); OnPropertyChanged(nameof(ChannelLabel)); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    // ── Computed display properties ──

    public string DisplayLabel =>
        string.IsNullOrEmpty(DisplayOptions) ? SizeLabel : $"{SizeLabel}  {DisplayOptions}";

    public string CountLabel => $"{Items.Count} file{(Items.Count != 1 ? "s" : "")}, {ImageCount} print{(ImageCount != 1 ? "s" : "")}";

    public string PrintedLabel =>
        PrintedCount > 0 ? $"Printed {PrintedCount}/{ImageCount}" : "";

    public bool IsPrinted => PrintedCount > 0 && PrintedCount >= ImageCount;

    public string ChannelLabel =>
        !ChannelNumber.HasValue || ChannelNumber == 0 ? "(unmapped)"
        : ChannelNumber == -1 ? "(skip)"
        : !string.IsNullOrEmpty(LayoutName) ? $"[Layout] {LayoutName} \u2192 Ch {ChannelNumber:D3}"
        : !string.IsNullOrEmpty(ChannelName) ? $"Ch {ChannelNumber:D3} \u2014 {ChannelName}"
        : $"Ch {ChannelNumber:D3}";

    public bool IsUnmapped => !ChannelNumber.HasValue || ChannelNumber == 0;

    public bool HasMissing => MissingFileCount > 0;

    public string MissingLabel => HasMissing ? $"{MissingFileCount} missing" : "";

    /// <summary>The underlying OrderItem records for this size group.</summary>
    public List<OrderItem> Items { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Represents an order node in the tree. Children are SizeTreeItem groups.
/// </summary>
public class OrderTreeItem : INotifyPropertyChanged
{
    private int _dbId;
    private string _externalOrderId = "";
    private string _customerName = "";
    private string _customerPhone = "";
    private string _customerEmail = "";
    private string _sourceCode = "";
    private string _statusCode = "";
    private string _storeName = "";
    private DateTime? _orderedAt;
    private bool _isHeld;
    private bool _isTransfer;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _hasUnmapped;
    private bool _hasMissingFiles;
    private int _totalImages;
    private string _folderPath = "";

    public int DbId
    {
        get => _dbId;
        set { _dbId = value; OnPropertyChanged(); }
    }

    public string ExternalOrderId
    {
        get => _externalOrderId;
        set { _externalOrderId = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShortId)); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string CustomerName
    {
        get => _customerName;
        set { _customerName = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string CustomerPhone
    {
        get => _customerPhone;
        set { _customerPhone = value; OnPropertyChanged(); }
    }

    public string CustomerEmail
    {
        get => _customerEmail;
        set { _customerEmail = value; OnPropertyChanged(); }
    }

    public string SourceCode
    {
        get => _sourceCode;
        set { _sourceCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(SourceLabel)); }
    }

    public string StatusCode
    {
        get => _statusCode;
        set { _statusCode = value; OnPropertyChanged(); }
    }

    public string StoreName
    {
        get => _storeName;
        set { _storeName = value; OnPropertyChanged(); }
    }

    public DateTime? OrderedAt
    {
        get => _orderedAt;
        set { _orderedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(DateLabel)); }
    }

    public bool IsHeld
    {
        get => _isHeld;
        set { _isHeld = value; OnPropertyChanged(); }
    }

    public bool IsTransfer
    {
        get => _isTransfer;
        set { _isTransfer = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool HasUnmapped
    {
        get => _hasUnmapped;
        set { _hasUnmapped = value; OnPropertyChanged(); }
    }

    public bool HasMissingFiles
    {
        get => _hasMissingFiles;
        set { _hasMissingFiles = value; OnPropertyChanged(); }
    }

    public int TotalImages
    {
        get => _totalImages;
        set { _totalImages = value; OnPropertyChanged(); }
    }

    public string FolderPath
    {
        get => _folderPath;
        set { _folderPath = value; OnPropertyChanged(); }
    }

    // ── Computed display properties ──

    public string ShortId => OrderHelpers.GetShortId(ExternalOrderId);

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(CustomerName)
            ? ShortId
            : $"{ShortId}  ({CustomerName})";

    public string SourceLabel => SourceCode?.ToUpperInvariant() ?? "";

    public string DateLabel => OrderedAt?.ToString("MM/dd HH:mm") ?? "";


    public ObservableCollection<SizeTreeItem> Sizes { get; } = new();

    /// <summary>Reference to the Shared Order model.</summary>
    public Order? Order { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
