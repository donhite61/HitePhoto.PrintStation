using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.Core.Decisions;

public class FilesNeededDecision : IFilesNeededDecision
{
    public bool AreFilesRequired(OrderSource source, int orderStoreId, int localStoreId)
    {
        return source switch
        {
            OrderSource.Pixfizz => true,
            OrderSource.Dakis => orderStoreId == localStoreId,
            OrderSource.Dashboard => true,
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
    }
}
