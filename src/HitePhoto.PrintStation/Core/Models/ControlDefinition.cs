using System.Windows.Input;
using System.Windows.Media;

namespace HitePhoto.PrintStation.Core.Models
{
    public enum SlotKind { Empty, Correction, Preset, Action }

    public class ControlDefinition
    {
        public string Id         { get; init; } = "";
        public SlotKind Kind     { get; init; }
        public string Label      { get; init; } = "";
        public string FieldName  { get; init; } = "";
        public int MinValue      { get; init; } = -10;
        public int MaxValue      { get; init; } = 10;
        public Key? Shortcut     { get; init; }

        // Pre-built frozen brushes for performance (no converter needed)
        public SolidColorBrush ValueBgBrush  { get; init; } = Brushes.Transparent;
        public SolidColorBrush ValueFgBrush  { get; init; } = Brushes.White;
        public SolidColorBrush LabelBrush    { get; init; } = Brushes.Gray;
        public SolidColorBrush ButtonBgBrush { get; init; } = Brushes.Transparent;
        public SolidColorBrush ButtonFgBrush { get; init; } = Brushes.White;
    }
}
