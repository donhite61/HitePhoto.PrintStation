namespace HitePhoto.PrintStation.Data.Repositories;

public interface IHistoryRepository
{
    void AddNote(int orderId, string note, string createdBy = "");

    /// <summary>Only inserts if the most recent note by this author differs from the new note.</summary>
    void AddNoteIfNew(int orderId, string note, string createdBy = "");

    List<HistoryEntry> GetNotes(int orderId);
}

public record HistoryEntry(int Id, int OrderId, string Note, string CreatedBy, string CreatedAt);
