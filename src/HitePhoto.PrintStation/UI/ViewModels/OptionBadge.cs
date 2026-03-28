namespace HitePhoto.PrintStation.UI.ViewModels;

public class OptionBadge
{
    public string DisplayText { get; }
    public string OptionKey { get; }
    public string OptionValue { get; }
    public bool IsDefault { get; }

    public OptionBadge(string key, string value, bool isDefault)
    {
        OptionKey = key;
        OptionValue = value;
        IsDefault = isDefault;
        DisplayText = string.IsNullOrEmpty(key) ? value : $"{key}: {value}";
    }
}
