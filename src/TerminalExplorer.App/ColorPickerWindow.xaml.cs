using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TerminalExplorer;

/// <summary>
/// Выбор цвета в модели HSV.
///
/// HSV, а не RGB, потому что человек думает о цвете именно так: «тот же оттенок, но темнее»
/// в HSV — движение по одной оси, в RGB — согласованное изменение трёх чисел.
/// </summary>
public partial class ColorPickerWindow : Window
{
    private double _hue;          // 0..360
    private double _saturation;   // 0..1
    private double _value;        // 0..1

    public Color SelectedColor { get; private set; }

    /// <summary>
    /// Срабатывает на каждое изменение — окно настроек применяет цвет немедленно,
    /// чтобы результат было видно на живом интерфейсе, а не только в маленьком образце.
    /// Отмена возвращает исходный цвет.
    /// </summary>
    public event Action<Color>? ColorChanged;

    public ColorPickerWindow(string title, Color initial)
    {
        InitializeComponent();

        TitleText.Text = "  " + title.ToUpperInvariant();
        SelectedColor = initial;

        (_hue, _saturation, _value) = ToHsv(initial);

        Loaded += (_, _) => Refresh();
    }

    // ---------- преобразования ----------

    /// <summary>RGB -> HSV. Стандартный алгоритм: тон определяется наибольшей компонентой.</summary>
    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0.00001)
        {
            if (max == r) hue = 60 * (((g - b) / delta) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        if (hue < 0) hue += 360;

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation;
        double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
        double m = value - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    // ---------- отрисовка ----------

    private void Refresh()
    {
        Color pure = FromHsv(_hue, 1, 1);
        HueLayer.Fill = new SolidColorBrush(pure);

        SelectedColor = FromHsv(_hue, _saturation, _value);
        Preview.Fill = new SolidColorBrush(SelectedColor);

        // Альфа сохраняем непрозрачной: полупрозрачные цвета интерфейса дали бы
        // просвечивающие панели, чего плоская тема не предполагает.
        HexBox.Text = "#FF" + SelectedColor.R.ToString("X2") +
                      SelectedColor.G.ToString("X2") + SelectedColor.B.ToString("X2");

        ValuesText.Text = "H " + _hue.ToString("F0") + "°   S " + (_saturation * 100).ToString("F0") +
                          "%   V " + (_value * 100).ToString("F0") + "%";

        if (SvField.ActualWidth > 0)
        {
            Canvas.SetLeft(Marker, _saturation * SvField.ActualWidth - Marker.Width / 2);
            Canvas.SetTop(Marker, (1 - _value) * SvField.ActualHeight - Marker.Height / 2);
        }

        if (HueField.ActualWidth > 0)
            Canvas.SetLeft(HueMarker, _hue / 360 * HueField.ActualWidth - HueMarker.Width / 2);

        ColorChanged?.Invoke(SelectedColor);
    }

    // ---------- ввод ----------

    private void SvField_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvField.CaptureMouse();
        UpdateFromSv(e.GetPosition(SvField));
    }

    private void SvField_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { SvField.ReleaseMouseCapture(); return; }
        UpdateFromSv(e.GetPosition(SvField));
    }

    private void UpdateFromSv(Point point)
    {
        _saturation = Math.Clamp(point.X / SvField.ActualWidth, 0, 1);
        _value = Math.Clamp(1 - point.Y / SvField.ActualHeight, 0, 1);
        Refresh();
    }

    private void HueField_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueField.CaptureMouse();
        UpdateFromHue(e.GetPosition(HueField));
    }

    private void HueField_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { HueField.ReleaseMouseCapture(); return; }
        UpdateFromHue(e.GetPosition(HueField));
    }

    private void UpdateFromHue(Point point)
    {
        _hue = Math.Clamp(point.X / HueField.ActualWidth, 0, 1) * 360;
        Refresh();
    }

    private void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        ApplyHex();
    }

    private void Hex_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        if (!ThemeManager.TryParseColor(HexBox.Text, out Color color)) return;

        (_hue, _saturation, _value) = ToHsv(color);
        Refresh();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
