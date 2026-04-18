namespace HitePhoto.PrintStation.Core.Services;

public interface IPixfizzNotifier
{
    Task<bool> MarkCompletedAsync(string jobId);
}
