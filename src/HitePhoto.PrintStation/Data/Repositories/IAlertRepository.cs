namespace HitePhoto.PrintStation.Data.Repositories;

public interface IAlertRepository
{
    void Insert(AlertRecord alert);
    List<AlertRecord> GetRecent(int days);
    List<AlertRecord> GetUnacknowledged();
    void Acknowledge(int alertId);
    void PurgeOlderThan(int days);
}

public record AlertRecord(
    int Id,
    string Severity,
    string Category,
    string Summary,
    string? OrderId,
    string? Detail,
    string? Exception,
    string? SourceMethod,
    string? SourceFile,
    int? SourceLine,
    string CreatedAt,
    bool Acknowledged);
