namespace HitePhoto.PrintStation.Core.Models;

public class ChannelInfo
{
    public int ChannelNumber { get; set; }
    public string SizeLabel { get; set; } = string.Empty;   // e.g. "4x6"
    public string MediaType { get; set; } = string.Empty;   // e.g. "luster"
    public string Description { get; set; } = string.Empty;

    // Routing key used as dictionary key in RoutingMap
    public string RoutingKey => $"size={SizeLabel}|media={MediaType}".ToLowerInvariant();

    /// <summary>Paper width in inches, parsed from SizeLabel (e.g. "4x6" → 4.0).</summary>
    public double WidthInches => ParseDimension(0);

    /// <summary>Paper height in inches, parsed from SizeLabel (e.g. "4x6" → 6.0).</summary>
    public double HeightInches => ParseDimension(1);

    private double ParseDimension(int index)
    {
        var parts = SizeLabel.ToLowerInvariant().Split('x');
        if (parts.Length < 2) return 0;

        string raw = parts[index].Trim();
        int end = 0;
        while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '.' || raw[end] == '-'))
            end++;

        if (end > 0 && double.TryParse(raw[..end],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
            return val;
        return 0;
    }
}
