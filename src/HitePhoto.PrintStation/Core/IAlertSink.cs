using HitePhoto.PrintStation.Data.Repositories;

namespace HitePhoto.PrintStation.Core;

/// <summary>
/// Interface for alert persistence backends.
/// Each sink receives every error/warning alert. Failures must not propagate.
/// </summary>
public interface IAlertSink
{
    void Persist(AlertRecord record);
}
