using System.ComponentModel;
using System.Windows.Media;
using HitePhoto.PrintStation.Core.Models;

namespace HitePhoto.PrintStation.UI.ViewModels
{
    public class SlotViewModel : ViewModelBase
    {
        private static readonly SolidColorBrush GoldBrush;
        private static readonly SolidColorBrush ActiveBgBrush;
        private static readonly SolidColorBrush ToggleOnBgBrush;
        private static readonly SolidColorBrush PresetSavedFgBrush;

        static SlotViewModel()
        {
            GoldBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#997700"));
            GoldBrush.Freeze();
            ActiveBgBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#C0B888"));
            ActiveBgBrush.Freeze();
            ToggleOnBgBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#78B878"));
            ToggleOnBgBrush.Freeze();
            PresetSavedFgBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#1A1A1A"));
            PresetSavedFgBrush.Freeze();
        }

        private readonly ImageCorrectionCard _card;

        public ControlDefinition Definition { get; }

        public SlotViewModel(ControlDefinition definition, ImageCorrectionCard card)
        {
            Definition = definition;
            _card = card;

            if (definition.Kind == SlotKind.Correction)
            {
                card.State.PropertyChanged += OnStatePropertyChanged;
                card.PropertyChanged += OnCardPropertyChanged;
            }
            else if (definition.Kind == SlotKind.Action && IsToggleAction)
            {
                card.State.PropertyChanged += OnToggleStateChanged;
            }
        }

        /// <summary>
        /// Unsubscribes event handlers to prevent memory leaks when the card is replaced.
        /// </summary>
        public void Detach()
        {
            if (Definition.Kind == SlotKind.Correction)
            {
                _card.State.PropertyChanged -= OnStatePropertyChanged;
                _card.PropertyChanged -= OnCardPropertyChanged;
            }
            else if (Definition.Kind == SlotKind.Action && IsToggleAction)
            {
                _card.State.PropertyChanged -= OnToggleStateChanged;
            }
        }

        public int DisplayValue => GetFieldValue();

        public bool IsActive =>
            Definition.Kind == SlotKind.Correction &&
            _card.FocusedField == Definition.FieldName;

        /// <summary>Whether this action button is a toggleable state (not RST/HLD).</summary>
        private bool IsToggleAction => Definition.Id is "AL" or "AG" or "WB" or "NM" or "BW" or "SP";

        /// <summary>Whether the toggle action is currently on.</summary>
        public bool IsToggleOn => Definition.Kind == SlotKind.Action && GetToggleState();

        public SolidColorBrush CurrentValueFgBrush =>
            IsActive ? GoldBrush : Definition.ValueFgBrush;

        public SolidColorBrush CurrentValueBgBrush =>
            IsActive ? ActiveBgBrush : Definition.ValueBgBrush;

        /// <summary>For action buttons: brighter bg when toggle is on.</summary>
        public SolidColorBrush CurrentButtonBgBrush =>
            IsToggleOn ? ToggleOnBgBrush : Definition.ButtonBgBrush;

        /// <summary>For preset buttons: brighter text when preset is saved.</summary>
        public SolidColorBrush CurrentButtonFgBrush =>
            (Definition.Kind == SlotKind.Preset && HasSavedPreset())
                ? PresetSavedFgBrush
                : Definition.ButtonFgBrush;

        private int GetFieldValue()
        {
            if (Definition.Kind != SlotKind.Correction) return 0;
            return Definition.FieldName switch
            {
                "Exposure"           => _card.State.Exposure,
                "Brightness"         => _card.State.Brightness,
                "Contrast"           => _card.State.Contrast,
                "Shadows"            => _card.State.Shadows,
                "Highlights"         => _card.State.Highlights,
                "Saturation"         => _card.State.Saturation,
                "ColorTemp"          => _card.State.ColorTemp,
                "Red"                => _card.State.Red,
                "Green"              => _card.State.Green,
                "Blue"               => _card.State.Blue,
                "SigmoidalContrast"  => _card.State.SigmoidalContrast,
                "Clahe"              => _card.State.Clahe,
                "ContrastStretch"    => _card.State.ContrastStretch,
                "Levels"             => _card.State.Levels,
                _                    => 0
            };
        }

        private bool GetToggleState() => Definition.Id switch
        {
            "AL" => _card.State.AutoLevel,
            "AG" => _card.State.AutoGamma,
            "WB" => _card.State.WhiteBalance,
            "NM" => _card.State.Normalize,
            "BW" => _card.State.Grayscale,
            "SP" => _card.State.Sepia,
            _    => false
        };

        private bool HasSavedPreset()
        {
            if (Definition.Kind != SlotKind.Preset) return false;
            return _card.Settings.Presets.ContainsKey(Definition.Id);
        }

        /// <summary>
        /// Called externally when a preset is saved, to refresh the visual indicator.
        /// </summary>
        public void RefreshPresetState()
        {
            OnPropertyChanged(nameof(CurrentButtonFgBrush));
        }

        private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == Definition.FieldName)
                OnPropertyChanged(nameof(DisplayValue));
        }

        private void OnToggleStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            var toggleProp = Definition.Id switch
            {
                "AL" => nameof(ImageCorrectionState.AutoLevel),
                "AG" => nameof(ImageCorrectionState.AutoGamma),
                "WB" => nameof(ImageCorrectionState.WhiteBalance),
                "NM" => nameof(ImageCorrectionState.Normalize),
                "BW" => nameof(ImageCorrectionState.Grayscale),
                "SP" => nameof(ImageCorrectionState.Sepia),
                _    => null
            };
            if (e.PropertyName == toggleProp)
            {
                OnPropertyChanged(nameof(IsToggleOn));
                OnPropertyChanged(nameof(CurrentButtonBgBrush));
            }
        }

        private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ImageCorrectionCard.FocusedField))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(CurrentValueFgBrush));
                OnPropertyChanged(nameof(CurrentValueBgBrush));
            }
        }
    }
}
