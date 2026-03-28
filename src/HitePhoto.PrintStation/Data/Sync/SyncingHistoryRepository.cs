using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Data.Sync;

/// <summary>
/// Decorator around IHistoryRepository that fires MariaDB pushes
/// after note inserts. Read methods pass through.
/// </summary>
public class SyncingHistoryRepository : IHistoryRepository
{
    private readonly IHistoryRepository _inner;
    private readonly ISyncService _sync;

    public SyncingHistoryRepository(IHistoryRepository inner, ISyncService sync)
    {
        _inner = inner;
        _sync = sync;
    }

    public void AddNote(int orderId, string note, string createdBy = "")
    {
        _inner.AddNote(orderId, note, createdBy);
        var payload = JsonSerializer.Serialize(new { orderId, note, createdBy });
        _ = Task.Run(() => _sync.PushAsync("order_notes", orderId, "add_note", payload));
    }

    public void AddNoteIfNew(int orderId, string note, string createdBy = "")
    {
        // Get note count before to detect if a new note was inserted
        var before = _inner.GetNotes(orderId).Count;
        _inner.AddNoteIfNew(orderId, note, createdBy);
        var after = _inner.GetNotes(orderId).Count;

        if (after > before)
        {
            var payload = JsonSerializer.Serialize(new { orderId, note, createdBy });
            _ = Task.Run(() => _sync.PushAsync("order_notes", orderId, "add_note", payload));
        }
    }

    public List<HistoryEntry> GetNotes(int orderId) => _inner.GetNotes(orderId);
}
