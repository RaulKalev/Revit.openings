using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RedeliAvad
{
    /// <summary>true → Collapsed, false → Visible.</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool && (bool)value ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is Visibility && (Visibility)value != Visibility.Visible;
    }

    /// <summary>Non-null (and non-empty string) → Visible.</summary>
    public sealed class NotEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var visible = value != null && !(value is string && string.IsNullOrWhiteSpace((string)value));
            if (parameter as string == "invert") visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Inverts a bool.</summary>
    public sealed class NotConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool && (bool)value);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool && (bool)value);
    }
}

namespace RedeliAvad
{
    /// <summary>double minus the (invariant) parameter, never below zero – e.g. a sheet's MaxHeight from the window height.</summary>
    public sealed class SubtractConverter : IValueConverter
    {
        public static readonly SubtractConverter Instance = new SubtractConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var v = value is double ? (double)value : 0;
            double p;
            double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out p);
            return Math.Max(0, v - p);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
