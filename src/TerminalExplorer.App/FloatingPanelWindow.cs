using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace TerminalExplorer;

/// <summary>
/// Откреплённое окно панели («поверх других»). Держит содержимое панели (терминал или сведения)
/// в отдельном перетаскиваемом окне, которое всегда сверху. Кнопка ⧉ возвращает панель в док.
///
/// Строится кодом, а не XAML, чтобы не плодить словари рядом с панелями: для стиля
/// используются те же DynamicResource-ключи темы, что и в основном окне.
/// </summary>
internal sealed class FloatingPanelWindow : Window
{
    /// <summary>Контейнер содержимого панели: вызывающий код кладёт сюда своё дерево.</summary>
    public Grid Body { get; } = new();

    /// <summary>Заголовок окна — служит для идентификации при возврате в док.</summary>
    public string PanelTitle { get; }

    private readonly Button _dock;

    /// <summary>Пользователь просит вернуть панель в док основного окна.</summary>
    public event Action? DockRequested;

    public FloatingPanelWindow(string title)
    {
        PanelTitle = title;

        // Окно всегда поверх остальных, как просит «поверх других» в паспорте фичи.
        Topmost = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        Background = (Brush)Application.Current.Resources["Background"];

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 26,
            ResizeBorderThickness = new Thickness(5),
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });

        _dock = new Button
        {
            Content = "⧉",
            Width = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["Accent"],
        };
        _dock.Click += (_, _) => DockRequested?.Invoke();

        var titleText = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["Accent"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var close = new Button
        {
            Content = "X",
            Width = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["Accent"],
        };
        close.Click += (_, _) => { DockRequested?.Invoke(); };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 2, 6, 2),
            Background = (Brush)Application.Current.Resources["Selection"],
        };
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(_dock);
        titleRow.Children.Add(close);
        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleRow, 0);
        Grid.SetRow(Body, 1);
        host.Children.Add(titleRow);
        host.Children.Add(Body);

        var border = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["Border"],
            BorderThickness = new Thickness(1),
            Margin = new Thickness(18),
            Effect = (System.Windows.Media.Effects.Effect)Application.Current.Resources["WindowShadow"],
            Child = host,
        };

        Content = border;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Если окно закрыто (Esc, Alt+F4), панель возвращается в док.
        DockRequested?.Invoke();
        base.OnClosed(e);
    }
}