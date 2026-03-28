using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core;

public class SqliteAlertSink : IAlertSink
{
    private readonly IAlertRepository _repository;

    public SqliteAlertSink(IAlertRepository repository)
    {
        _repository = repository;
    }

    public void Persist(AlertRecord record)
    {
        try { _repository.Insert(record); }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to persist alert to SQLite: {ex.Message}");
        }
    }
}
