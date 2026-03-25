namespace HitePhoto.PrintStation.Core.Services;

/// <summary>No-op Pixfizz notifier for testing. Replace with real implementation later.</summary>
public class StubPixfizzNotifier : IPixfizzNotifier
{
    public Task MarkCompletedAsync(string jobId)
    {
        AppLog.Info($"[STUB] Would mark Pixfizz job {jobId} as completed");
        return Task.CompletedTask;
    }
}
