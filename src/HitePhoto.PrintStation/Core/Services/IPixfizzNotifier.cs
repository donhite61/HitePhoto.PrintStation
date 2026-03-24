namespace HitePhoto.PrintStation.Core.Services;

public interface IPixfizzNotifier
{
    Task MarkCompletedAsync(string jobId);
}
