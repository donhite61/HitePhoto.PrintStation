using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;

namespace HitePhoto.PrintStation.Core.Models
{
    public static class ControlRegistry
    {
        public static readonly ControlDefinition Empty = new()
        {
            Id = "", Kind = SlotKind.Empty, Label = ""
        };

        public static readonly IReadOnlyList<ControlDefinition> All;
        public static readonly IReadOnlyDictionary<string, ControlDefinition> ById;
        public static readonly IReadOnlyDictionary<Key, ControlDefinition> ByShortcut;

        static ControlRegistry()
        {
            var list = new List<ControlDefinition>
            {
                // ── Corrections ── CMY + tone controls ──────────────────────────
                new() { Id = "Cyan",       Kind = SlotKind.Correction, Label = "C",  FieldName = "Red",
                    Shortcut = Key.C,
                    ValueBgBrush = Frozen("#88BBBB"), ValueFgBrush = Frozen("#1A6666"), LabelBrush = Frozen("#1A6666") },
                new() { Id = "Magenta",    Kind = SlotKind.Correction, Label = "M",  FieldName = "Green",
                    Shortcut = Key.M,
                    ValueBgBrush = Frozen("#BB88BB"), ValueFgBrush = Frozen("#661A66"), LabelBrush = Frozen("#661A66") },
                new() { Id = "Yellow",     Kind = SlotKind.Correction, Label = "Y",  FieldName = "Blue",
                    Shortcut = Key.Y,
                    ValueBgBrush = Frozen("#BBBB88"), ValueFgBrush = Frozen("#666618"), LabelBrush = Frozen("#666618") },
                new() { Id = "Warmth",     Kind = SlotKind.Correction, Label = "W",  FieldName = "ColorTemp",
                    Shortcut = Key.W,
                    ValueBgBrush = Frozen("#BBAA88"), ValueFgBrush = Frozen("#665518"), LabelBrush = Frozen("#665518") },
                new() { Id = "Exposure",   Kind = SlotKind.Correction, Label = "E",  FieldName = "Exposure",
                    Shortcut = Key.E,
                    ValueBgBrush = Frozen("#AA88CC"), ValueFgBrush = Frozen("#442288"), LabelBrush = Frozen("#442288") },
                new() { Id = "Contrast",   Kind = SlotKind.Correction, Label = "Cn", FieldName = "Contrast",
                    Shortcut = Key.K,
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "Saturation", Kind = SlotKind.Correction, Label = "S",  FieldName = "Saturation",
                    Shortcut = Key.S,
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "Brightness", Kind = SlotKind.Correction, Label = "Br", FieldName = "Brightness",
                    Shortcut = Key.B,
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "Shadows",    Kind = SlotKind.Correction, Label = "Sh", FieldName = "Shadows",
                    Shortcut = Key.D,
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "Highlights", Kind = SlotKind.Correction, Label = "Hi", FieldName = "Highlights",
                    Shortcut = Key.H,
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },

                // ── Advanced corrections ─────────────────────────────────────────
                new() { Id = "SigmoidalContrast", Kind = SlotKind.Correction, Label = "SC", FieldName = "SigmoidalContrast",
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "Clahe",             Kind = SlotKind.Correction, Label = "CL", FieldName = "Clahe",
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "ContrastStretch",   Kind = SlotKind.Correction, Label = "CS", FieldName = "ContrastStretch",
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },
                new() { Id = "Levels",            Kind = SlotKind.Correction, Label = "LV", FieldName = "Levels",
                    ValueBgBrush = Frozen("#B0B0B0"), ValueFgBrush = Frozen("#333333"), LabelBrush = Frozen("#555555") },

                // ── Presets ─────────────────────────────────────────────────────
                new() { Id = "D1", Kind = SlotKind.Preset, Label = "D1",
                    ButtonBgBrush = Frozen("#8AAACE"), ButtonFgBrush = Frozen("#2A4A78") },
                new() { Id = "D2", Kind = SlotKind.Preset, Label = "D2",
                    ButtonBgBrush = Frozen("#8AAACE"), ButtonFgBrush = Frozen("#2A4A78") },
                new() { Id = "L1", Kind = SlotKind.Preset, Label = "L1",
                    ButtonBgBrush = Frozen("#CCAA77"), ButtonFgBrush = Frozen("#785522") },
                new() { Id = "L2", Kind = SlotKind.Preset, Label = "L2",
                    ButtonBgBrush = Frozen("#CCAA77"), ButtonFgBrush = Frozen("#785522") },
                new() { Id = "C1", Kind = SlotKind.Preset, Label = "C1",
                    ButtonBgBrush = Frozen("#88BB88"), ButtonFgBrush = Frozen("#2A662A") },
                new() { Id = "C2", Kind = SlotKind.Preset, Label = "C2",
                    ButtonBgBrush = Frozen("#88BB88"), ButtonFgBrush = Frozen("#2A662A") },

                // ── Actions ─────────────────────────────────────────────────────
                new() { Id = "HLD", Kind = SlotKind.Action, Label = "HLD",
                    ButtonBgBrush = Frozen("#CCAA77"), ButtonFgBrush = Frozen("#886622") },
                new() { Id = "RST", Kind = SlotKind.Action, Label = "RST",
                    ButtonBgBrush = Frozen("#CC8888"), ButtonFgBrush = Frozen("#882222") },
                new() { Id = "BW",  Kind = SlotKind.Action, Label = "BW",
                    ButtonBgBrush = Frozen("#B0B0B0"), ButtonFgBrush = Frozen("#404040") },

                // ── Auto-correction toggles ────────────────────────────────────
                new() { Id = "AL",  Kind = SlotKind.Action, Label = "AL",
                    ButtonBgBrush = Frozen("#88BBBB"), ButtonFgBrush = Frozen("#226666") },
                new() { Id = "AG",  Kind = SlotKind.Action, Label = "AG",
                    ButtonBgBrush = Frozen("#88BBBB"), ButtonFgBrush = Frozen("#226666") },
                new() { Id = "WB",  Kind = SlotKind.Action, Label = "WB",
                    ButtonBgBrush = Frozen("#88BBBB"), ButtonFgBrush = Frozen("#226666") },
                new() { Id = "NM",  Kind = SlotKind.Action, Label = "NM",
                    ButtonBgBrush = Frozen("#88BBBB"), ButtonFgBrush = Frozen("#226666") },
                new() { Id = "SP",  Kind = SlotKind.Action, Label = "SP",
                    ButtonBgBrush = Frozen("#B0B0B0"), ButtonFgBrush = Frozen("#404040") },
            };

            All = list.AsReadOnly();
            ById = list.ToDictionary(c => c.Id);
            ByShortcut = list.Where(c => c.Shortcut.HasValue)
                             .ToDictionary(c => c.Shortcut!.Value);
        }

        public static List<string> DefaultSlotLayout() => new()
        {
            // Right sidebar (7 slots)
            "D1", "D2", "L1", "L2", "C1", "C2", "BW",
            // Bottom row (7 slots)
            "Yellow", "Magenta", "Cyan", "Warmth", "Exposure", "Contrast", "Saturation",
            // Left sidebar (9 slots)
            "Brightness", "Shadows", "Highlights", "HLD", "RST", "", "", "", ""
        };

        private static SolidColorBrush Frozen(string hex)
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }
}
