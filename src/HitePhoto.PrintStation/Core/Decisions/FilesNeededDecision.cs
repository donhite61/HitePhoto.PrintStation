namespace HitePhoto.PrintStation.Core.Decisions;

public class FilesNeededDecision : IFilesNeededDecision
{
    public bool AreFilesRequired(string source, int orderStoreId, int localStoreId)
    {
        if (source.Equals("pixfizz", StringComparison.OrdinalIgnoreCase))
            return true;

        if (source.Equals("dakis", StringComparison.OrdinalIgnoreCase))
            return orderStoreId == localStoreId;

        throw new ArgumentException(
            $"Unknown order source '{source}'. Expected 'pixfizz' or 'dakis'.");
    }
}
