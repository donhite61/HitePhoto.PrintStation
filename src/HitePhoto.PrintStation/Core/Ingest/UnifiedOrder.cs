using HitePhoto.Shared.Parsers;

namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// The unified order format. Every parser outputs this regardless of source.
/// Record type enables immutable 'with' expressions for safe updates.
/// </summary>
public record UnifiedOrder
{
    public required string ExternalOrderId { get; init; }
    public required string ExternalSource { get; init; }
    public DateTime? OrderedAt { get; init; }
    public string? CustomerFirstName { get; init; }
    public string? CustomerLastName { get; init; }
    public string? CustomerEmail { get; init; }
    public string? CustomerPhone { get; init; }
    public decimal? OrderTotal { get; init; }
    public bool Paid { get; init; }
    public string? PaymentReference { get; init; }
    public string? Notes { get; init; }
    public string? FolderPath { get; init; }

    public string? Location { get; init; }
    public string? OrderType { get; init; }
    public string? FulfillmentType { get; init; }
    public bool IsInvoiceOnly { get; init; }
    public bool IsRush { get; init; }

    // Pixfizz-specific
    public string? PixfizzJobId { get; init; }
    public string? PixfizzBookId { get; init; }
    public string? PixfizzProductCode { get; init; }
    public string? PaymentMethod { get; init; }
    public string? FulfillmentStoreName { get; init; }

    // Dakis store identity
    public string? BillingStoreId { get; init; }
    public string? CurrentStoreId { get; init; }

    // Channel (Online/Kiosk) — from Dakis shopping cart
    public string? Channel { get; init; }

    // Download status
    public string DownloadStatus { get; init; } = "pending";
    public List<string> DownloadErrors { get; init; } = [];

    public List<UnifiedOrderItem> Items { get; init; } = [];
}

public record UnifiedOrderItem
{
    public string? ExternalLineId { get; init; }
    public string? SizeLabel { get; init; }
    public string? MediaType { get; init; }
    public string? FormatString { get; init; }
    public int Quantity { get; init; } = 1;
    public string? ImageFilename { get; init; }
    public string? ImageFilepath { get; init; }
    public string? OriginalImageFilepath { get; init; }
    public int? ChannelNumber { get; init; }

    /// <summary>
    /// True = goes to Noritsu printer. False = non-print item (metals, canvas, gifts).
    /// Replaces the old "IsGiftProduct" (which was inverted and unclear).
    /// </summary>
    public bool IsNoritsu { get; init; } = true;

    public string? FulfillmentStore { get; init; }
    public int ExpectedPrintCount { get; init; }
    public int OutlabCount { get; init; }

    public List<OrderItemOption> Options { get; init; } = [];
}
