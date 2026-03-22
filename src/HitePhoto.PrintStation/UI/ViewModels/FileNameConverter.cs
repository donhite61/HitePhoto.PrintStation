using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace HitePhoto.PrintStation.UI.ViewModels
{
    /// <summary>
    /// Returns just the filename portion of a full path.
    /// Used in XAML as a static singleton — no IValueConverter registration needed.
    /// </summary>
    public class FileNameConverter : IValueConverter
    {
        public static readonly FileNameConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string path ? Path.GetFileName(path) : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
