using HitePhoto.PrintStation.Data;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core;

public class MariaDbAlertSink : IAlertSink
{
    private readonly PrintStationDb _db;
    private readonly int _storeId;

    public MariaDbAlertSink(PrintStationDb db, int storeId)
    {
        _db = db;
        _storeId = storeId;
    }

    public void Persist(AlertRecord record)
    {
        // Fire-and-forget — MariaDB failure must never block the app
        _ = _db.InsertAlertAsync(_storeId, record);
    }
}
