using System.Windows;
using System.Windows.Controls;
using HitePhoto.PrintStation.Core.Models;
using HitePhoto.PrintStation.UI.ViewModels;

namespace HitePhoto.PrintStation.UI
{
    public class SlotTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? CorrectionTemplate { get; set; }
        public DataTemplate? PresetTemplate     { get; set; }
        public DataTemplate? ActionTemplate     { get; set; }
        public DataTemplate? EmptyTemplate      { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is SlotViewModel slot)
            {
                return slot.Definition.Kind switch
                {
                    SlotKind.Correction => CorrectionTemplate,
                    SlotKind.Preset     => PresetTemplate,
                    SlotKind.Action     => ActionTemplate,
                    _                   => EmptyTemplate
                };
            }
            return EmptyTemplate;
        }
    }
}
