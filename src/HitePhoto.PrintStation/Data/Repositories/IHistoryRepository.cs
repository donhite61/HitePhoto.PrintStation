namespace HitePhoto.PrintStation.Data.Repositories;

public interface IHistoryRepository
{
    void AddNote(string orderId, string note, string createdBy = "");

    /// <summary>Only inserts if the most recent note by this author differs from the new note.</summary>
    void AddNoteIfNew(string orderId, string note, string createdBy = "");

    List<HistoryEntry> GetNotes(string orderId);
}

public record HistoryEntry(string Id, string OrderId, string Note, string CreatedBy, string CreatedAt);
