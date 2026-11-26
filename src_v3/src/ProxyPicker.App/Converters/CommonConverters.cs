using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ProxyPicker.App.Converters
{
    public sealed class BoolToVisConverter : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool bv && bv;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    public sealed class NonEmptyToVisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    public sealed class IntEqualsToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null) return false;
            return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                int i;
                if (int.TryParse(parameter.ToString(), out i)) return i;
            }
            return Binding.DoNothing;
        }
    }
}
