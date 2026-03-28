namespace HitePhoto.PrintStation.Data.Repositories;

public interface IOptionDefaultsRepository
{
    /// <summary>Get all option defaults as a HashSet for fast lookup.</summary>
    HashSet<(string Key, string Value)> GetAll();

    /// <summary>Mark a key+value pair as default (upsert).</summary>
    void SetDefault(string optionKey, string optionValue);

    /// <summary>Remove a key+value pair from defaults.</summary>
    void RemoveDefault(string optionKey, string optionValue);

    /// <summary>Check if a specific key+value is marked as default.</summary>
    bool IsDefault(string optionKey, string optionValue);
}
