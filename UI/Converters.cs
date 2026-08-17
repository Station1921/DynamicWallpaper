using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicWallpaper.UI
{
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool invert = parameter is string s && s == "inverse";
            bool hasValue = value != null;
            if (invert) hasValue = !hasValue;
            return hasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>非空字符串 → Visible，否则 Collapsed（用于“已应用屏幕”徽标）。</summary>
    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool nonEmpty = value is string s && !string.IsNullOrEmpty(s);
            return nonEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>bool → Visibility；支持 inverse 参数。</summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool flag = value is bool b && b;
            bool invert = parameter is string s && s == "inverse";
            if (invert) flag = !flag;
            return flag ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>把 ActualWidth / ActualHeight 转成带圆角的 RectangleGeometry，用于动态裁剪卡片内容。</summary>
    public class SizeToClipGeometryConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                values[0] is double w && w > 0 &&
                values[1] is double h && h > 0)
            {
                double radius = parameter is string s && double.TryParse(s, out var r) ? r : 0;
                return new System.Windows.Media.RectangleGeometry(
                    new Rect(0, 0, w, h), radius, radius);
            }
            return DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
