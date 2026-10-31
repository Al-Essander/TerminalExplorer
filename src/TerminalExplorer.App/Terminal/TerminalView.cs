using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TerminalExplorer.Terminal;

/// <summary>
/// Визуальный контрол терминала: рисует экран <see cref="TerminalScreen"/>, превращает нажатия
/// клавиш и мыши в VT-ввод для оболочки, умеет выделение, копирование, вставку и прокрутку истории.
///
/// Рисуется напрямую через DrawingContext, а не набором TextBlock: экран — это сетка
/// фиксированных ячеек, и точная посимвольная раскладка важнее гибкости разметки WPF.
/// </summary>
public sealed class TerminalView : Control
{
    private const double PaddingX = 6;
    private const double PaddingY = 3;
    private const double ScrollStripWidth = 10;

    // Палитра из 16 цветов (в духе «Campbell» из Windows Terminal).
    private static readonly Color[] Ansi =
    [
        Color.FromRgb(0x0C, 0x0C, 0x0C), Color.FromRgb(0xC5, 0x0F, 0x1F), Color.FromRgb(0x13, 0xA1, 0x0E),
        Color.FromRgb(0xC1, 0x9C, 0x00), Color.FromRgb(0x00, 0x37, 0xDA), Color.FromRgb(0x88, 0x17, 0x98),
        Color.FromRgb(0x3A, 0x96, 0xDD), Color.FromRgb(0xCC, 0xCC, 0xCC), Color.FromRgb(0x76, 0x76, 0x76),
        Color.FromRgb(0xE7, 0x48, 0x56), Color.FromRgb(0x16, 0xC6, 0x0C), Color.FromRgb(0xF9, 0xF1, 0xA5),
        Color.FromRgb(0x3B, 0x78, 0xFF), Color.FromRgb(0xB4, 0x00, 0x9E), Color.FromRgb(0x61, 0xD6, 0xD6),
        Color.FromRgb(0xF2, 0xF2, 0xF2),
    ];

    public static readonly DependencyProperty ThemeForegroundProperty = DependencyProperty.Register(
        nameof(ThemeForeground), typeof(Brush), typeof(TerminalView),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThemeBackgroundProperty = DependencyProperty.Register(
        nameof(ThemeBackground), typeof(Brush), typeof(TerminalView),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThemeSelectionProperty = DependencyProperty.Register(
        nameof(ThemeSelection), typeof(Brush), typeof(TerminalView),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush ThemeForeground { get => (Brush)GetValue(ThemeForegroundProperty); set => SetValue(ThemeForegroundProperty, value); }
    public Brush ThemeBackground { get => (Brush)GetValue(ThemeBackgroundProperty); set => SetValue(ThemeBackgroundProperty, value); }
    public Brush ThemeSelection { get => (Brush)GetValue(ThemeSelectionProperty); set => SetValue(ThemeSelectionProperty, value); }

    private TerminalSession? _session;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private bool _cursorPhase = true;

    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private Typeface _typeface = new("Consolas");
    private readonly Dictionary<int, Brush> _brushCache = [];

    private int _viewOffset;
    private int _lastScrollback;

    private (int Row, int Col)? _selectionAnchor;
    private (int Row, int Col)? _selectionHead;
    private bool _selecting;
    private bool _draggingScrollbar;

    /// <summary>Пользователь крутит колесо с Ctrl: +1 — крупнее, -1 — мельче.</summary>
    public event Action<int>? ZoomRequested;

    /// <summary>Пользователь нажал клавишу — повод, например, вернуть фокус-индикацию.</summary>
    public event Action? InputSent;

    /// <summary>Оболочка завершилась, а пользователь нажал Enter — просит запустить её заново.</summary>
    public event Action? RestartRequested;

    public int GridColumns { get; private set; } = 80;
    public int GridRows { get; private set; } = 24;

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;

        // Tab и стрелки принадлежат оболочке, а не навигации WPF по элементам.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.None);
        KeyboardNavigation.SetControlTabNavigation(this, KeyboardNavigationMode.None);

        SetResourceReference(ThemeForegroundProperty, "Foreground");
        // «Background» — «бумага» темы (в Классике белая), а не серый корпус окна: на ней и читаются ANSI-цвета.
        SetResourceReference(ThemeBackgroundProperty, "Background");
        SetResourceReference(ThemeSelectionProperty, "Selection");
        SetResourceReference(FontFamilyProperty, "MonoFont");
        SetResourceReference(FontSizeProperty, "BaseFontSize");

        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _blink.Tick += (_, _) =>
        {
            _cursorPhase = !_cursorPhase;
            if (IsKeyboardFocused) InvalidateVisual();
        };

        GotKeyboardFocus += (_, _) => { _cursorPhase = true; _blink.Start(); InvalidateVisual(); };
        LostKeyboardFocus += (_, _) => { _blink.Stop(); InvalidateVisual(); };
    }

    // ---------- подключение сеанса ----------

    public TerminalSession? Session => _session;

    public void Attach(TerminalSession? session)
    {
        if (_session is not null) _session.Changed -= OnSessionChanged;

        _session = session;
        _selectionAnchor = _selectionHead = null;
        _viewOffset = 0;
        _lastScrollback = session?.Screen.ScrollbackCount ?? 0;

        if (_session is not null)
        {
            _session.Changed += OnSessionChanged;
            ApplyGridSize();
        }

        InvalidateVisual();
    }

    private void OnSessionChanged()
    {
        if (_session is null) return;

        // Если пользователь читает историю, новый вывод не должен «уезжать» из-под глаз:
        // сдвигаем смещение на столько строк, сколько ушло в историю.
        int scrollback = _session.Screen.ScrollbackCount;

        if (_viewOffset > 0)
            _viewOffset = Math.Min(scrollback, _viewOffset + Math.Max(0, scrollback - _lastScrollback));

        _lastScrollback = scrollback;
        InvalidateVisual();
    }

    // ---------- размеры ----------

    private void MeasureCell()
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        var sample = new FormattedText(new string('M', 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, FontSize, Brushes.White, null, TextFormattingMode.Display, dip);

        _cellWidth = Math.Max(1, sample.WidthIncludingTrailingWhitespace / 20);
        _cellHeight = Math.Max(1, Math.Ceiling(sample.Height));
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty)
        {
            MeasureCell();
            ApplyGridSize();
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyGridSize();
    }

    /// <summary>Сколько ячеек помещается сейчас; сообщает размер сеансу, а тот — ConPTY.</summary>
    public void ApplyGridSize()
    {
        MeasureCell();

        int columns = (int)Math.Floor((ActualWidth - PaddingX * 2 - ScrollStripWidth) / _cellWidth);
        int rows = (int)Math.Floor((ActualHeight - PaddingY * 2) / _cellHeight);

        if (columns < 4 || rows < 2) return;

        GridColumns = columns;
        GridRows = rows;
        _session?.Resize(columns, rows);
    }

    // ---------- цвета ----------

    private Color Resolve(int spec, bool foreground, bool bold)
    {
        if (spec == Cell.DefaultColor)
            return ((foreground ? ThemeForeground : ThemeBackground) as SolidColorBrush)?.Color
                   ?? (foreground ? Colors.LightGray : Colors.Black);

        if ((spec & Cell.RgbFlag) != 0)
            return Color.FromRgb((byte)(spec >> 16), (byte)(spec >> 8), (byte)spec);

        if (spec < 8 && bold) spec += 8;
        if (spec < 16) return Ansi[spec];

        if (spec < 232)
        {
            int i = spec - 16;
            return Color.FromRgb(CubeLevel(i / 36), CubeLevel(i / 6 % 6), CubeLevel(i % 6));
        }

        byte gray = (byte)(8 + (spec - 232) * 10);
        return Color.FromRgb(gray, gray, gray);

        static byte CubeLevel(int level) => (byte)(level == 0 ? 0 : 55 + level * 40);
    }

    private Brush BrushFor(Color color)
    {
        int key = color.R << 16 | color.G << 8 | color.B;

        if (_brushCache.TryGetValue(key, out Brush? brush)) return brush;

        var created = new SolidColorBrush(color);
        created.Freeze();

        if (_brushCache.Count > 512) _brushCache.Clear();
        _brushCache[key] = created;
        return created;
    }

    private const double MinimumContrast = 3.5;

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Минимальный контраст текста с фоном, как «minimumContrastRatio» в Windows Terminal:
    /// цвет сдвигается к белому на тёмном фоне и к чёрному на светлом, пока текст не станет читаемым.
    /// Оттенок сохраняется, пока это возможно.
    /// </summary>
    private static Color EnsureContrast(Color foreground, Color background)
    {
        if (ContrastRatio(foreground, background) >= MinimumContrast) return foreground;

        Color target = Luminance(background) < 0.4 ? Colors.White : Colors.Black;

        for (double step = 0.1; step <= 1.001; step += 0.1)
        {
            Color candidate = Blend(foreground, target, step);
            if (ContrastRatio(candidate, background) >= MinimumContrast) return candidate;
        }

        return target;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    // ---------- отрисовка ----------

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        dc.DrawRectangle(ThemeBackground, null, new Rect(0, 0, width, height));

        if (_session is null) return;

        TerminalScreen screen = _session.Screen;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        Color defaultBackground = Resolve(Cell.DefaultColor, false, false);
        Color defaultForeground = Resolve(Cell.DefaultColor, true, false);

        lock (screen.Sync)
        {
            int total = screen.TotalRows;
            int firstRow = Math.Max(0, screen.ScrollbackCount - _viewOffset);

            for (int r = 0; r < Math.Min(GridRows, screen.Rows); r++)
            {
                Row? row = screen.GetAbsoluteRow(firstRow + r);
                if (row is null) break;

                DrawRow(dc, row, r, firstRow + r, screen, defaultBackground, defaultForeground, dip);
            }

            DrawCursor(dc, screen, firstRow, dip);
            DrawScrollbar(dc, total, firstRow);
        }
    }

    private void DrawRow(DrawingContext dc, Row row, int visualRow, int absoluteRow, TerminalScreen screen,
        Color defaultBackground, Color defaultForeground, double dip)
    {
        double y = PaddingY + visualRow * _cellHeight;
        Cell[] cells = row.Cells;
        int columns = Math.Min(cells.Length, GridColumns);

        // Первый проход: фон. Подряд идущие ячейки одного цвета сливаются в один прямоугольник.
        int start = 0;

        while (start < columns)
        {
            Color background = CellColors(cells[start], defaultBackground, defaultForeground, out _);
            int end = start + 1;

            while (end < columns && CellColors(cells[end], defaultBackground, defaultForeground, out _) == background)
                end++;

            if (background != defaultBackground)
                dc.DrawRectangle(BrushFor(background), null,
                    new Rect(PaddingX + start * _cellWidth, y, (end - start) * _cellWidth, _cellHeight));

            start = end;
        }

        // Выделение рисуется поверх фона, но под текстом.
        DrawSelectionRow(dc, absoluteRow, columns, y);

        // Второй проход: текст. ASCII объединяется в пробеги, остальное — по одному символу,
        // чтобы широкие и нестандартные глифы не сдвигали последующие столбцы.
        var text = new StringBuilder();

        for (int c = 0; c < columns; c++)
        {
            ref Cell cell = ref cells[c];

            if ((cell.Attr & CellAttr.Continuation) != 0) continue;
            if (cell.Codepoint is <= ' ' or 0x7F && (cell.Attr & CellAttr.Underline) == 0 && (cell.Attr & CellAttr.Strike) == 0)
                continue;
            if ((cell.Attr & CellAttr.Hidden) != 0) continue;

            Color foreground = ForegroundOf(cell, defaultBackground, defaultForeground);

            bool simple = cell.Codepoint is > ' ' and < 0x7F;
            int runEnd = c + 1;

            text.Clear();
            AppendCodepoint(text, cell.Codepoint);

            if (simple)
            {
                while (runEnd < columns)
                {
                    ref Cell next = ref cells[runEnd];

                    if (next.Codepoint is not (> ' ' and < 0x7F) || next.Attr != cell.Attr || next.Fg != cell.Fg || next.Bg != cell.Bg)
                        break;

                    text.Append((char)next.Codepoint);
                    runEnd++;
                }
            }

            double x = PaddingX + c * _cellWidth;
            string value = text.ToString();

            if (value.Trim().Length > 0)
            {
                var typeface = (cell.Attr & (CellAttr.Italic)) != 0
                    ? new Typeface(FontFamily, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal)
                    : _typeface;

                var formatted = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, FontSize, BrushFor(foreground), null, TextFormattingMode.Display, dip);

                dc.DrawText(formatted, new Point(x, y));
            }

            double runWidth = (runEnd - c) * _cellWidth * ((cell.Attr & CellAttr.Wide) != 0 ? 2 : 1);

            if ((cell.Attr & CellAttr.Underline) != 0)
                dc.DrawRectangle(BrushFor(foreground), null, new Rect(x, y + _cellHeight - 2, runWidth, 1));

            if ((cell.Attr & CellAttr.Strike) != 0)
                dc.DrawRectangle(BrushFor(foreground), null, new Rect(x, y + _cellHeight / 2, runWidth, 1));

            c = runEnd - 1;
        }
    }

    /// <summary>Фон ячейки с учётом инверсии.</summary>
    private Color CellColors(Cell cell, Color defaultBackground, Color defaultForeground, out Color foreground)
    {
        bool inverse = (cell.Attr & CellAttr.Inverse) != 0;

        Color bg = cell.Bg == Cell.DefaultColor ? defaultBackground : Resolve(cell.Bg, false, false);
        Color fg = cell.Fg == Cell.DefaultColor ? defaultForeground : Resolve(cell.Fg, true, (cell.Attr & CellAttr.Bold) != 0);

        foreground = inverse ? bg : fg;
        return inverse ? fg : bg;
    }

    private Color ForegroundOf(Cell cell, Color defaultBackground, Color defaultForeground)
    {
        Color background = CellColors(cell, defaultBackground, defaultForeground, out Color foreground);

        if ((cell.Attr & CellAttr.Dim) != 0) foreground = Blend(foreground, background, 0.5);

        // Цвет темы по умолчанию дизайнер палитры уже подобрал; чужие (ANSI, 256, true color) —
        // нет: жёлтый PSReadLine на белом или синий на синем нечитаемы. Подтягиваем контраст.
        bool themeColor = cell.Fg == Cell.DefaultColor && (cell.Attr & CellAttr.Inverse) == 0;
        return themeColor ? foreground : EnsureContrast(foreground, background);
    }

    private static void AppendCodepoint(StringBuilder builder, int codepoint)
    {
        if (codepoint is > 0 and < 0x10000) builder.Append((char)codepoint);
        else if (codepoint >= 0x10000) builder.Append(char.ConvertFromUtf32(codepoint));
        else builder.Append(' ');
    }

    private void DrawCursor(DrawingContext dc, TerminalScreen screen, int firstRow, double dip)
    {
        if (!screen.CursorVisible || _viewOffset > 0) return;
        if (screen.CursorRow >= GridRows || screen.CursorCol >= GridColumns) return;

        var rect = new Rect(PaddingX + screen.CursorCol * _cellWidth, PaddingY + screen.CursorRow * _cellHeight,
            _cellWidth, _cellHeight);

        Brush accent = ThemeForeground;

        if (!IsKeyboardFocused)
        {
            dc.DrawRectangle(null, new Pen(accent, 1), new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1));
            return;
        }

        if (!_cursorPhase) return;

        dc.DrawRectangle(accent, null, rect);

        // Символ под блоком перерисовывается цветом фона, иначе он «пропал бы» внутри курсора.
        Row? row = screen.GetAbsoluteRow(firstRow + screen.CursorRow);
        if (row is null || screen.CursorCol >= row.Cells.Length) return;

        int codepoint = row.Cells[screen.CursorCol].Codepoint;
        if (codepoint <= ' ') return;

        var glyph = new FormattedText(char.ConvertFromUtf32(codepoint), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, FontSize, ThemeBackground, null, TextFormattingMode.Display, dip);

        dc.DrawText(glyph, rect.TopLeft);
    }

    private void DrawScrollbar(DrawingContext dc, int totalRows, int firstRow)
    {
        if (totalRows <= GridRows) return;

        double track = ActualHeight - PaddingY * 2;
        double thumb = Math.Max(16, track * GridRows / totalRows);
        double top = PaddingY + (track - thumb) * firstRow / Math.Max(1, totalRows - GridRows);

        Brush brush = BrushFor(Blend(Resolve(Cell.DefaultColor, false, false), Resolve(Cell.DefaultColor, true, false), 0.35));
        dc.DrawRectangle(brush, null, new Rect(ActualWidth - 6, top, 4, thumb));
    }

    // ---------- выделение ----------

    private bool TryGetSelection(out (int Row, int Col) start, out (int Row, int Col) end)
    {
        start = end = default;
        if (_selectionAnchor is null || _selectionHead is null) return false;

        (int Row, int Col) a = _selectionAnchor.Value, b = _selectionHead.Value;
        if (a == b) return false;

        bool aFirst = a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
        start = aFirst ? a : b;
        end = aFirst ? b : a;
        return true;
    }

    public bool HasSelection => TryGetSelection(out _, out _);

    private void DrawSelectionRow(DrawingContext dc, int absoluteRow, int columns, double y)
    {
        if (!TryGetSelection(out var start, out var end)) return;
        if (absoluteRow < start.Row || absoluteRow > end.Row) return;

        int from = absoluteRow == start.Row ? start.Col : 0;
        int to = absoluteRow == end.Row ? end.Col + 1 : columns;

        if (to <= from) return;

        Color color = (ThemeSelection as SolidColorBrush)?.Color ?? Colors.SteelBlue;
        dc.DrawRectangle(BrushFor(Color.FromArgb(255, color.R, color.G, color.B)), null,
            new Rect(PaddingX + from * _cellWidth, y, (to - from) * _cellWidth, _cellHeight));
    }

    public string? GetSelectedText()
    {
        if (_session is null || !TryGetSelection(out var start, out var end)) return null;
        return _session.Screen.GetText(start, end);
    }

    public void ClearSelection()
    {
        _selectionAnchor = _selectionHead = null;
        InvalidateVisual();
    }

    public void CopySelection()
    {
        string? text = GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;

        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* буфер занят другой программой */ }

        ClearSelection();
    }

    public void Paste()
    {
        if (_session is null) return;

        string? text;

        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        if (string.IsNullOrEmpty(text)) return;

        // Оболочки ждут \r как «Enter»; переводы строк из буфера приводим к нему.
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');

        if (_session.Screen.BracketedPaste) text = "\x1b[200~" + text + "\x1b[201~";

        SendInput(text);
    }

    // ---------- ввод с клавиатуры ----------

    private void SendInput(string text)
    {
        if (_session is null) return;

        SnapToBottom();
        _cursorPhase = true;
        _session.Send(text);
        InputSent?.Invoke();
    }

    /// <summary>Отправляет текст в оболочку так, будто его напечатали (перенос ввода из панели файлов).</summary>
    public void TypeText(string text) => SendInput(text);

    public void SnapToBottom()
    {
        if (_viewOffset == 0) return;

        _viewOffset = 0;
        InvalidateVisual();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);

        // Управляющие символы (Enter, Tab, Backspace) обработаны в OnKeyDown; сюда приходит только печатный текст.
        string text = new(e.Text.Where(ch => ch >= ' ' && ch != 0x7F).ToArray());
        if (text.Length == 0) return;

        ClearSelection();
        SendInput(text);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;

        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        ModifierKeys mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control);
        bool alt = mods.HasFlag(ModifierKeys.Alt);
        bool shift = mods.HasFlag(ModifierKeys.Shift);

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.CapsLock or Key.NumLock or Key.None)
            return;

        if (_session.HasExited && key == Key.Enter)
        {
            e.Handled = true;
            RestartRequested?.Invoke();
            return;
        }

        // --- действия самого терминала ---
        if (ctrl && shift && key == Key.C || ctrl && !shift && !alt && key == Key.Insert)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (ctrl && !alt && key == Key.C && HasSelection)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && key == Key.V || ctrl && !shift && !alt && key == Key.V || shift && !ctrl && key == Key.Insert)
        {
            Paste();
            e.Handled = true;
            return;
        }

        // История: Shift+PgUp/PgDn/Home/End, как в большинстве терминалов.
        if (shift && !ctrl && !alt && !_session.Screen.UsingAlternateScreen)
        {
            switch (key)
            {
                case Key.PageUp: ScrollBy(GridRows - 1); e.Handled = true; return;
                case Key.PageDown: ScrollBy(-(GridRows - 1)); e.Handled = true; return;
                case Key.Home: ScrollBy(int.MaxValue / 2); e.Handled = true; return;
                case Key.End: SnapToBottom(); e.Handled = true; return;
            }
        }

        string? sequence = Encode(key, ctrl, alt, shift, _session.Screen.ApplicationCursorKeys);
        if (sequence is null) return;

        ClearSelection();
        SendInput(sequence);
        e.Handled = true;
    }

    /// <summary>Нажатие → VT-последовательность. null — клавиша не обрабатывается здесь (текст придёт через TextInput).</summary>
    private static string? Encode(Key key, bool ctrl, bool alt, bool shift, bool applicationCursor)
    {
        int modifier = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        bool modified = modifier > 1;

        string Cursor(char final) => modified
            ? $"\x1b[1;{modifier}{final}"
            : (applicationCursor ? $"\x1bO{final}" : $"\x1b[{final}");

        string Tilde(int code) => modified ? $"\x1b[{code};{modifier}~" : $"\x1b[{code}~";
        string Function(char final) => modified ? $"\x1b[1;{modifier}{final}" : $"\x1bO{final}";

        switch (key)
        {
            case Key.Up: return Cursor('A');
            case Key.Down: return Cursor('B');
            case Key.Right: return Cursor('C');
            case Key.Left: return Cursor('D');
            case Key.Home: return Cursor('H');
            case Key.End: return Cursor('F');
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.F1: return Function('P');
            case Key.F2: return Function('Q');
            case Key.F3: return Function('R');
            case Key.F4: return Function('S');
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);

            case Key.Enter: return alt ? "\x1b\r" : "\r";
            case Key.Escape: return "\x1b";
            case Key.Tab: return shift ? "\x1b[Z" : (alt ? "\x1b\t" : "\t");
            case Key.Back: return ctrl ? "\x08" : (alt ? "\x1b\x7f" : "\x7f");
            case Key.Space when ctrl && !alt: return "\0";
        }

        // Ctrl+буква → управляющий символ (Ctrl+C = 0x03 и так далее). AltGr (Ctrl+Alt) сюда не попадает.
        if (ctrl && !alt)
        {
            if (key is >= Key.A and <= Key.Z) return ((char)(key - Key.A + 1)).ToString();

            switch (key)
            {
                case Key.OemOpenBrackets: return "\x1b";
                case Key.OemBackslash or Key.OemPipe: return "\x1c";
                case Key.OemCloseBrackets: return "\x1d";
                case Key.OemMinus: return "\x1f";
                case Key.D6: return "\x1e";
            }

            return null;
        }

        // Alt+символ → ESC и сам символ (так readline понимает Alt+B, Alt+F, Alt+D).
        if (alt && !ctrl)
        {
            if (key is >= Key.A and <= Key.Z)
                return "\x1b" + (char)((shift ? 'A' : 'a') + (key - Key.A));

            if (key is >= Key.D0 and <= Key.D9 && !shift)
                return "\x1b" + (char)('0' + (key - Key.D0));
        }

        return null;
    }

    // ---------- мышь ----------

    private (int Row, int Col) CellAt(Point point)
    {
        int col = (int)Math.Floor((point.X - PaddingX) / _cellWidth);
        int row = (int)Math.Floor((point.Y - PaddingY) / _cellHeight);

        return (Math.Clamp(row, 0, GridRows - 1), Math.Clamp(col, 0, GridColumns - 1));
    }

    private (int Row, int Col) ToAbsolute((int Row, int Col) visual)
    {
        int first = Math.Max(0, (_session?.Screen.ScrollbackCount ?? 0) - _viewOffset);
        return (first + visual.Row, visual.Col);
    }

    private bool MouseReporting(MouseEventArgs e) =>
        _session is { Screen.MouseTracking: > 0 } && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    private void ReportMouse(int button, (int Row, int Col) cell, bool release, bool motion = false)
    {
        if (_session is null) return;

        ModifierKeys mods = Keyboard.Modifiers;
        int code = button + (motion ? 32 : 0)
                          + (mods.HasFlag(ModifierKeys.Shift) ? 4 : 0)
                          + (mods.HasFlag(ModifierKeys.Alt) ? 8 : 0)
                          + (mods.HasFlag(ModifierKeys.Control) ? 16 : 0);

        string report = _session.Screen.MouseSgr
            ? $"\x1b[<{code};{cell.Col + 1};{cell.Row + 1}{(release ? 'm' : 'M')}"
            : $"\x1b[M{(char)(32 + (release ? 3 : code))}{(char)(32 + Math.Min(cell.Col + 1, 222))}{(char)(32 + Math.Min(cell.Row + 1, 222))}";

        _session.Send(report);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (_session is null) return;

        Point point = e.GetPosition(this);

        // Полоса прокрутки у правого края.
        if (e.ChangedButton == MouseButton.Left && point.X >= ActualWidth - ScrollStripWidth - 2 &&
            _session.Screen.TotalRows > GridRows)
        {
            _draggingScrollbar = true;
            CaptureMouse();
            ScrollToPoint(point.Y);
            e.Handled = true;
            return;
        }

        (int Row, int Col) cell = CellAt(point);

        if (MouseReporting(e))
        {
            int button = e.ChangedButton switch { MouseButton.Left => 0, MouseButton.Middle => 1, _ => 2 };
            ReportMouse(button, cell, release: false);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        switch (e.ChangedButton)
        {
            case MouseButton.Left:
                (int Row, int Col) absolute = ToAbsolute(cell);

                if (e.ClickCount == 2) SelectWord(absolute);
                else if (e.ClickCount >= 3) SelectLine(absolute);
                else
                {
                    _selectionAnchor = absolute;
                    _selectionHead = absolute;
                    _selecting = true;
                    CaptureMouse();
                }

                InvalidateVisual();
                e.Handled = true;
                break;

            case MouseButton.Right:
                // Как в Windows Terminal: есть выделение — копируем, нет — вставляем.
                if (HasSelection) CopySelection();
                else Paste();
                e.Handled = true;
                break;

            case MouseButton.Middle:
                Paste();
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session is null) return;

        Point point = e.GetPosition(this);

        if (_draggingScrollbar)
        {
            ScrollToPoint(point.Y);
            return;
        }

        if (MouseReporting(e))
        {
            int tracking = _session.Screen.MouseTracking;
            bool pressed = e.LeftButton == MouseButtonState.Pressed;

            if (tracking == 1003 || (tracking == 1002 && pressed))
                ReportMouse(pressed ? 0 : 3, CellAt(point), release: false, motion: true);

            return;
        }

        if (!_selecting) return;

        // Выход за край при выделении прокручивает историю — иначе длинный фрагмент не охватить.
        if (point.Y < 0) ScrollBy(1);
        else if (point.Y > ActualHeight) ScrollBy(-1);

        _selectionHead = ToAbsolute(CellAt(point));
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_draggingScrollbar)
        {
            _draggingScrollbar = false;
            ReleaseMouseCapture();
            return;
        }

        if (_session is not null && MouseReporting(e) && IsMouseCaptured)
        {
            int button = e.ChangedButton switch { MouseButton.Left => 0, MouseButton.Middle => 1, _ => 2 };
            ReportMouse(button, CellAt(e.GetPosition(this)), release: true);
            ReleaseMouseCapture();
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_session is null) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomRequested?.Invoke(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
            return;
        }

        int lines = Math.Max(1, Math.Abs(e.Delta) / 40);

        if (MouseReporting(e))
        {
            for (int i = 0; i < lines; i++)
                ReportMouse(e.Delta > 0 ? 64 : 65, CellAt(e.GetPosition(this)), release: false);

            e.Handled = true;
            return;
        }

        // На альтернативном экране (less, vim без мыши) истории нет — колесо превращается в стрелки.
        if (_session.Screen.UsingAlternateScreen)
        {
            string arrow = _session.Screen.ApplicationCursorKeys
                ? (e.Delta > 0 ? "\x1bOA" : "\x1bOB")
                : (e.Delta > 0 ? "\x1b[A" : "\x1b[B");

            _session.Send(string.Concat(Enumerable.Repeat(arrow, lines)));
            e.Handled = true;
            return;
        }

        ScrollBy(e.Delta > 0 ? lines : -lines);
        e.Handled = true;
    }

    // ---------- прокрутка и слова ----------

    public void ScrollBy(int lines)
    {
        if (_session is null) return;

        int maximum = _session.Screen.ScrollbackCount;
        int next = (int)Math.Clamp((long)_viewOffset + lines, 0, maximum);

        if (next == _viewOffset) return;

        _viewOffset = next;
        InvalidateVisual();
    }

    private void ScrollToPoint(double y)
    {
        if (_session is null) return;

        int maximum = _session.Screen.ScrollbackCount;
        double track = Math.Max(1, ActualHeight - PaddingY * 2);
        double fraction = Math.Clamp((y - PaddingY) / track, 0, 1);

        // fraction 0 — самая старая история, 1 — низ.
        _viewOffset = (int)Math.Round((1 - fraction) * maximum);
        InvalidateVisual();
    }

    private void SelectWord((int Row, int Col) at)
    {
        Row? row = _session?.Screen.GetAbsoluteRow(at.Row);
        if (row is null) return;

        static bool IsWordChar(int codepoint) => codepoint > ' ' ;

        int start = at.Col, end = at.Col;

        if (!IsWordChar(row.Cells[at.Col].Codepoint)) { start = end = at.Col; }
        else
        {
            while (start > 0 && IsWordChar(row.Cells[start - 1].Codepoint)) start--;
            while (end < row.Cells.Length - 1 && IsWordChar(row.Cells[end + 1].Codepoint)) end++;
        }

        _selectionAnchor = (at.Row, start);
        _selectionHead = (at.Row, end);
    }

    private void SelectLine((int Row, int Col) at)
    {
        _selectionAnchor = (at.Row, 0);
        _selectionHead = (at.Row, GridColumns - 1);
    }
}
