namespace HitePhoto.PrintStation.Core.Decisions;

/// <summary>
/// Single authority for whether an order's items need image files on disk.
/// Pixfizz = always. Dakis = only if this is the production store.
/// Does not depend on services/vendors tables — only source and store.
/// </summary>
public interface IFilesNeededDecision
{
    bool AreFilesRequired(string source, int orderStoreId, int localStoreId);
}
