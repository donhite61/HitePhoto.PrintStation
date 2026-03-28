using HitePhoto.PrintStation.Core;

namespace HitePhoto.PrintStation.Data.Repositories;

public class OptionDefaultsRepository : IOptionDefaultsRepository
{
    private readonly OrderDb _db;

    public OptionDefaultsRepository(OrderDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public HashSet<(string Key, string Value)> GetAll()
    {
        var result = new HashSet<(string, string)>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT option_key, option_value FROM option_defaults";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public void SetDefault(string optionKey, string optionValue)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO option_defaults (option_key, option_value)
            VALUES (@key, @value)
            ON CONFLICT(option_key, option_value) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@key", optionKey);
        cmd.Parameters.AddWithValue("@value", optionValue);
        cmd.ExecuteNonQuery();
    }

    public void RemoveDefault(string optionKey, string optionValue)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM option_defaults WHERE option_key = @key AND option_value = @value";
        cmd.Parameters.AddWithValue("@key", optionKey);
        cmd.Parameters.AddWithValue("@value", optionValue);
        cmd.ExecuteNonQuery();
    }

    public bool IsDefault(string optionKey, string optionValue)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM option_defaults WHERE option_key = @key AND option_value = @value";
        cmd.Parameters.AddWithValue("@key", optionKey);
        cmd.Parameters.AddWithValue("@value", optionValue);
        return cmd.ExecuteScalar() != null;
    }
}
