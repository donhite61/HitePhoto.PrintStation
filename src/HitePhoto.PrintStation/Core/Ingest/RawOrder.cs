namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Raw data from an order source before parsing.
/// API poller creates these; parser converts to UnifiedOrder.
/// </summary>
public record RawOrder(
    string ExternalOrderId,
    string SourceName,
    string RawData,
    Dictionary<string, string> Metadata);
