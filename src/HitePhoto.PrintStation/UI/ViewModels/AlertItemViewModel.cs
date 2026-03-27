using System.Windows.Media;

namespace HitePhoto.PrintStation.UI.ViewModels;

public class AlertItemViewModel
{
    public int Id { get; init; }
    public string SeverityLabel { get; init; } = "";
    public string Summary { get; init; } = "";
    public string? OrderId { get; init; }
    public string TimestampText { get; init; } = "";
    public string? TechnicalDetail { get; init; }
    public bool IsDetailExpanded { get; set; }
    public Brush SeverityBrush { get; init; } = Brushes.Gray;
    public Brush BackgroundBrush { get; init; } = Brushes.Transparent;
}
