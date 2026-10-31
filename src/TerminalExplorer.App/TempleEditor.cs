using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TerminalExplorer;

/// <summary>
/// Встроенный просмотр и правка текстовых файлов в духе TempleOS: палитра VGA из 16 цветов,
/// синий экран, жёлтый текст, окно с цветной рамкой и заголовком в углу.
///
/// Цвета заданы жёстко, а не берутся из темы, — это сознательно: вид TempleOS и есть
/// содержание затеи, и тема «Классика» или «MS-DOS» его бы испортила.
///
/// Редактор намеренно простой: поле TextBox из WPF уже даёт выделение, отмену, буфер обмена
/// и IME; своё полотно ради того же результата потребовало бы недель. Горячие клавиши:
/// Ctrl+S — сохранить, Ctrl+F / F3 — найти, Esc — закрыть.
/// </summary>
public sealed class TempleEditor : Border
{
    // Палитра CGA/VGA, как в TempleOS.
    private static readonly Color Blue = Color.FromRgb(0x00, 0x00, 0xAA);
    private static readonly Color LightBlue = Color.FromRgb(0x55, 0x55, 0xFF);
    private static readonly Color Cyan = Color.FromRgb(0x00, 0xAA, 0xAA);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xFF, 0x55);
    private static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color Red = Color.FromRgb(0xAA, 0x00, 0x00);
    private static readonly Color Green = Color.FromRgb(0x55, 0xFF, 0x55);

    private const long MaxBytes = 6 * 1024 * 1024;

    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly TextBox _editor = new();
    private readonly TextBox _find = new();
    private readonly Border _findBar = new();

    // Подсветка синтаксиса: текст дублируется в неактивный TextBlock под прозрачным TextBox.
    private readonly TextBlock _highlight = new();

    private string? _path;
    private bool _dirty;
    private bool _loading;
    private Encoding _encoding = new UTF8Encoding(false);
    private bool _unixLineEndings;
    private bool _readOnly;

    /// <summary>Окно закрыто пользователем.</summary>
    public event Action? Closed;

    public bool IsOpen => Visibility == Visibility.Visible;
    public bool IsDirty => _dirty;
    public string? FilePath => _path;

    public TempleEditor()
    {
        Visibility = Visibility.Collapsed;
        Background = Brush(Blue);
        BorderBrush = Brush(Yellow);
        BorderThickness = new Thickness(3);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Заголовок: бирюзовая полоса, слева «[X]», как рамка окна TempleOS.
        var closeButton = new TextBlock
        {
            Text = "[X] ",
            Foreground = Brush(Red),
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand,
            ToolTip = LanguageManager.Tr("L_EditClose"),
        };

        closeButton.MouseLeftButtonDown += (_, e) => { TryClose(); e.Handled = true; };

        _title.Foreground = Brush(Blue);
        _title.FontWeight = FontWeights.Bold;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 1, 6, 1) };
        titleRow.Children.Add(closeButton);
        titleRow.Children.Add(_title);

        var titleBar = new Border { Background = Brush(Cyan), Child = titleRow };
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        // Панель поиска.
        var findLabel = new TextBlock
        {
            Text = LanguageManager.Tr("L_FindLabel"), Foreground = Brush(Yellow), VerticalAlignment = VerticalAlignment.Center,
        };

        StyleBox(_find, Cyan, Blue);
        _find.MinWidth = 260;
        _find.KeyDown += FindKeyDown;

        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        findRow.Children.Add(findLabel);
        findRow.Children.Add(_find);
        findRow.Children.Add(new TextBlock
        {
            Text = LanguageManager.Tr("L_FindHint"), Foreground = Brush(LightBlue), VerticalAlignment = VerticalAlignment.Center,
        });

        _findBar.Child = findRow;
        _findBar.Visibility = Visibility.Collapsed;
        _findBar.BorderBrush = Brush(Yellow);
        _findBar.BorderThickness = new Thickness(0, 0, 0, 1);
        Grid.SetRow(_findBar, 1);
        grid.Children.Add(_findBar);

        // Само поле правки. Текст дублируется в неактивный TextBlock (_highlight) под
        // прозрачным TextBox: так подсветка синтаксиса не требует переписывать редактор.
        StyleBox(_editor, Blue, Yellow);
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = true;
        _editor.IsInactiveSelectionHighlightEnabled = true;
        _editor.TextWrapping = TextWrapping.NoWrap;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.FontSize = 16;
        _editor.Padding = new Thickness(8, 4, 8, 4);
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            MarkDirty();
            HighlightText();
        };
        _editor.SelectionChanged += (_, _) => UpdateStatus();
        _editor.PreviewKeyDown += EditorKeyDown;

        // Нижний слой с цветными фрагментами кода.
        _highlight.FontFamily = new FontFamily("Consolas, Courier New");
        _highlight.FontSize = 16;
        _highlight.TextWrapping = TextWrapping.NoWrap;
        _highlight.Padding = new Thickness(8, 4, 8, 4);
        _highlight.Margin = new Thickness(0);

        var highlightScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsHitTestVisible = false,
            Content = _highlight,
        };

        var editorHost = new Grid { Background = Brush(Blue) };
        editorHost.Children.Add(highlightScroll);
        editorHost.Children.Add(_editor);
        // Прозрачный фон и текст редактора — чтобы просвечивал _highlight с цветными
        // фрагментами. Каретка (White) и выделение (LightBlue) рисуются отдельно и видны.
        _editor.Background = Brushes.Transparent;
        _editor.Foreground = Brushes.Transparent;

        // Синхронизация прокрутки: внутренний ScrollViewer редактора и слой подсветки
        // должны двигаться вместе, иначе цвета «уедут» от текста.
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ScrollViewer sv)
            {
                highlightScroll.ScrollToHorizontalOffset(sv.HorizontalOffset);
                highlightScroll.ScrollToVerticalOffset(sv.VerticalOffset);
            }
        }));

        Grid.SetRow(editorHost, 2);
        grid.Children.Add(editorHost);

        // Статусная строка.
        _status.Foreground = Brush(Blue);
        var statusBar = new Border
        {
            Background = Brush(Cyan), Padding = new Thickness(6, 1, 6, 1), Child = _status,
        };

        Grid.SetRow(statusBar, 3);
        grid.Children.Add(statusBar);

        Child = grid;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Поле без системного шаблона: стандартный темы приложения рисует фаску и центрирует
    /// содержимое по вертикали, для многострочного редактора это не годится.
    /// </summary>
    private static void StyleBox(TextBox box, Color background, Color foreground)
    {
        box.Background = Brush(background);
        box.Foreground = Brush(foreground);
        box.CaretBrush = Brush(White);
        box.SelectionBrush = Brush(LightBlue);
        box.SelectionOpacity = 0.85;
        box.BorderThickness = new Thickness(0);
        box.FontFamily = new FontFamily("Consolas, Courier New");
        box.Cursor = Cursors.IBeam;

        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));

        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(host);

        template.VisualTree = border;
        box.Template = template;
    }

    // ---------- открытие и сохранение ----------

    /// <summary>Открывает файл. Возвращает false (и пишет причину в status), если файл не годится для правки.</summary>
    public bool Open(string path, out string message)
    {
        message = string.Empty;

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists) { message = LanguageManager.Tr("L_FileNotFound"); return false; }
            if (info.Length > MaxBytes) { message = LanguageManager.Tr("L_FileTooBig"); return false; }

            byte[] bytes = File.ReadAllBytes(path);

            // Двоичные файлы не открываем: нулевой байт почти всегда значит «не текст».
            // Сканируем не только начало — бинарник с «текстовыми» первыми байтами (как .blend)
            // иначе попадает в редактор и становится несохранённой правкой, из которой нет выхода.
            // Исключение одно: BOM UTF-16 (FF FE) — там ноль легален.
            bool isUtf16Bom = bytes.Length >= 2 && (bytes[0] is 0xFF or 0xFE);
            foreach (byte b in bytes)
            {
                if (b == 0 && !isUtf16Bom)
                {
                    message = LanguageManager.Tr("L_BinaryFile");
                    return false;
                }
            }

            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd();

            // Запоминаем кодировку и перевод строк, чтобы сохранение не меняло файл целиком.
            _encoding = reader.CurrentEncoding is UTF8Encoding && bytes.Length >= 3 && bytes[0] == 0xEF
                ? new UTF8Encoding(true)
                : reader.CurrentEncoding;

            _unixLineEndings = text.Contains('\n') && !text.Contains("\r\n");

            _path = path;
            _readOnly = info.IsReadOnly;
            _loading = true;
            _editor.Text = text;
            _editor.IsReadOnly = _readOnly;
            _editor.CaretIndex = 0;
            _editor.ScrollToHome();
            HighlightText();
            _loading = false;
            _dirty = false;

            Visibility = Visibility.Visible;
            UpdateTitle();
            UpdateStatus();
            _editor.Focus();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = ex.Message;
            return false;
        }
    }

    public bool Save()
    {
        if (_path is null) return false;

        if (_readOnly)
        {
            _status.Text = LanguageManager.Tr("L_ReadOnly");
            return false;
        }

        try
        {
            string text = _editor.Text;
            if (_unixLineEndings) text = text.Replace("\r\n", "\n");

            // Через временный файл: обрыв записи не должен оставить обрезанный исходник.
            string temporary = _path + ".te-tmp";
            File.WriteAllText(temporary, text, _encoding);
            File.Move(temporary, _path, overwrite: true);

            _dirty = false;
            UpdateTitle();
            _status.Text = LanguageManager.Tr("L_Saved");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = string.Format(LanguageManager.Tr("L_SaveError"), ex.Message);
            return false;
        }
    }

    /// <summary>Закрывает окно, если нет несохранённого. Иначе подсказывает, что делать.</summary>
    public bool TryClose(bool discard = false)
    {
        if (_dirty && !discard)
        {
            _status.Text = LanguageManager.Tr("L_UnsavedWarning");
            return false;
        }

        Visibility = Visibility.Collapsed;
        _path = null;
        _dirty = false;
        _loading = true;
        _editor.Clear();
        _highlight.Inlines.Clear();
        _loading = false;

        Closed?.Invoke();
        return true;
    }

    public void FocusEditor() => _editor.Focus();

    /// <summary>Перерисовывает подсветку синтаксиса для текущего текста и расширения.</summary>
    private void HighlightText()
    {
        string extension = System.IO.Path.GetExtension(_path ?? string.Empty);
        if (SyntaxHighlighter.IsCode(extension) && _editor.Text.Length > 0)
            SyntaxHighlighter.Highlight(_editor.Text, _highlight);
        else
            _highlight.Inlines.Clear();
    }

    // ---------- интерфейс ----------

    private void MarkDirty()
    {
        if (_dirty) return;

        _dirty = true;
        UpdateTitle();
    }

    private void UpdateTitle() =>
        _title.Text = (_dirty ? "* " : "") + (_path is null ? "" : _path) + (_readOnly ? LanguageManager.Tr("L_ReadOnlySuffix") : "");

    private void UpdateStatus()
    {
        int index = _editor.CaretIndex;
        int line = _editor.GetLineIndexFromCharacterIndex(index);
        int column = index - Math.Max(0, _editor.GetCharacterIndexFromLineIndex(Math.Max(0, line)));

        _status.Text = string.Format(LanguageManager.Tr("L_EditorStatus"), line + 1, column + 1);
    }

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.S when control:
                Save();
                e.Handled = true;
                break;

            case Key.F when control:
                ShowFind();
                e.Handled = true;
                break;

            case Key.F3:
                FindNext(back: shift);
                e.Handled = true;
                break;

            case Key.Escape:
                TryClose(discard: shift);
                e.Handled = true;
                break;
        }
    }

    // ---------- поиск ----------

    private void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;

        if (_editor.SelectionLength > 0 && !_editor.SelectedText.Contains('\n'))
            _find.Text = _editor.SelectedText;

        _find.Focus();
        _find.SelectAll();
    }

    private void FindKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindNext(back: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                e.Handled = true;
                break;

            case Key.Escape:
                _findBar.Visibility = Visibility.Collapsed;
                _editor.Focus();
                e.Handled = true;
                break;
        }
    }

    private void FindNext(bool back)
    {
        string needle = _find.Text;

        if (needle.Length == 0)
        {
            ShowFind();
            return;
        }

        string text = _editor.Text;
        StringComparison comparison = StringComparison.CurrentCultureIgnoreCase;

        int from = back ? _editor.SelectionStart : _editor.SelectionStart + _editor.SelectionLength;
        int found;

        if (back)
        {
            found = from > 0 ? text.LastIndexOf(needle, from - 1, comparison) : -1;
            if (found < 0) found = text.LastIndexOf(needle, comparison);
        }
        else
        {
            found = from <= text.Length ? text.IndexOf(needle, from, comparison) : -1;
            if (found < 0) found = text.IndexOf(needle, comparison);
        }

        if (found < 0)
        {
            _status.Text = string.Format(LanguageManager.Tr("L_NotFound"), needle);
            return;
        }

        _editor.Select(found, needle.Length);
        _editor.ScrollToLine(_editor.GetLineIndexFromCharacterIndex(found));
        if (!_find.IsKeyboardFocused) _editor.Focus();
    }
}
