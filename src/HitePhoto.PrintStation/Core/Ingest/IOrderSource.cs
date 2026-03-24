namespace HitePhoto.PrintStation.Core.Ingest;

/// <summary>
/// Polls an external source for new orders. Each source (Pixfizz, Dakis) implements this.
/// </summary>
public interface IOrderSource
{
    string SourceName { get; }
    Task<IReadOnlyList<RawOrder>> PollAsync(CancellationToken ct);
}
