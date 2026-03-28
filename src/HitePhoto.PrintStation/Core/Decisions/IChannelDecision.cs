namespace HitePhoto.PrintStation.Core.Decisions;

/// <summary>
/// Single authority for which Noritsu channel a size routes to.
/// Reads from channel_mappings table in SQLite.
/// Returns 0 if unmapped — operator must assign manually.
/// </summary>
public interface IChannelDecision
{
    ChannelResult Resolve(string sizeLabel, string mediaType);

    /// <summary>
    /// Load all channel mappings in one query. Returns dictionary keyed by routing_key.
    /// Used by tree display to resolve channels without N+1 queries.
    /// </summary>
    Dictionary<string, ChannelResult> ResolveAll();
}

public record ChannelResult(
    int ChannelNumber,
    string? LayoutName,
    string RoutingKey);
