using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TerminalExplorer;

/// <summary>
/// Иконка строки. Реализована конвертером, а не свойством FileEntry, намеренно:
/// конвертер вызывается только для строк, которые WPF реально отрисовывает, поэтому
/// при виртуализации иконки запрашиваются лишь для видимых 30–40 строк, а не для всех
/// ста тысяч.
/// </summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FileEntry entry ? IconProvider.Get(entry) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Иконка по пути — для боковой панели и панели сведений.</summary>
public sealed class PathIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string path ? IconProvider.GetForPath(path, isDirectory: true) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Показывает элемент, когда значение непусто. Для панели сведений.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
