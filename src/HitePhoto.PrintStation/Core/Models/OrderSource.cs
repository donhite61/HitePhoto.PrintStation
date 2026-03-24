namespace HitePhoto.PrintStation.Core.Models;

public enum OrderSource
{
    Pixfizz,
    Dakis,
    Dashboard
}

public static class OrderSourceExtensions
{
    public static string ToCode(this OrderSource source) => source switch
    {
        OrderSource.Pixfizz => "pixfizz",
        OrderSource.Dakis => "dakis",
        OrderSource.Dashboard => "dashboard",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    public static OrderSource FromCode(string code) => code.ToLowerInvariant() switch
    {
        "pixfizz" => OrderSource.Pixfizz,
        "dakis" => OrderSource.Dakis,
        "dashboard" => OrderSource.Dashboard,
        _ => throw new ArgumentException($"Unknown order source code: '{code}'")
    };
}
