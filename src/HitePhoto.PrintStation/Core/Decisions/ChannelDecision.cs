using Microsoft.Data.Sqlite;
using HitePhoto.PrintStation.Data;

namespace HitePhoto.PrintStation.Core.Decisions;

public class ChannelDecision : IChannelDecision
{
    private readonly OrderDb _db;

    public ChannelDecision(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public ChannelResult Resolve(string sizeLabel, string mediaType)
    {
        var routingKey = BuildRoutingKey(sizeLabel, mediaType);

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT channel_number, layout_name FROM channel_mappings WHERE routing_key = @key";
        cmd.Parameters.AddWithValue("@key", routingKey);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var channel = reader.GetInt32(0);
            var layout = reader.IsDBNull(1) ? null : reader.GetString(1);
            return new ChannelResult(channel, layout, routingKey);
        }

        // Not mapped — return 0, operator must assign
        return new ChannelResult(0, null, routingKey);
    }

    /// <summary>
    /// Builds a deterministic routing key from size and media.
    /// Must match the key used when learning/saving mappings.
    /// </summary>
    public static string BuildRoutingKey(string sizeLabel, string mediaType)
    {
        var size = (sizeLabel ?? "").Trim().ToLowerInvariant();
        var media = (mediaType ?? "").Trim().ToLowerInvariant();
        return $"{size}|{media}";
    }
}
