using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TerminalExplorer.Terminal;

namespace TerminalExplorer;

/// <summary>
/// Правая половина окна: встроенная консоль (по сеансу на вкладку), редактор поверх неё,
/// переключение вкладок по давности (Ctrl+Tab) и сохранение сессии.
/// </summary>
public partial class MainWindow
{
    private const int SavedConsoleLines = 400;

    /// <summary>Вкладки от самой свежей к самой давней. Первая — всегда активная.</summary>
    private readonly List<TabState> _recent = [];

    private bool _cycling;
    private int _cycleIndex;
    private List<TabState> _cycleOrder = [];

    private bool _terminalMaximized;
    private GridLength _savedSidebar = new(200);

    private DispatcherTimer? _saveTimer;
    private Control? _focusBeforeEditor;

    // ---------- запуск терминала на вкладке ----------

    /// <summary>Показывает консоль активной вкладки, при первом обращении создавая оболочку.</summary>
    private void ShowTerminalFor(TabState tab)
    {
        if (tab.TermView is null)
        {
            var view = new TerminalView();

            view.ZoomRequested += step => SetScale(AppSettings.Current.FontSize + step);
            view.GotKeyboardFocus += (_, _) => SyncTerminalToPanel(tab);
            view.RestartRequested += () => RestartTerminal(tab);

            tab.TermView = view;
        }

        TerminalHost.Child = tab.TermView;

        // Размер нужен до запуска оболочки: иначе первое приглашение отрисуется
        // в 80x24 и тут же перерисуется. UpdateLayout даёт реальные ActualWidth/Height.
        TerminalHost.UpdateLayout();

        if (tab.Session is null)
        {
            var session = new TerminalSession(tab.TermView.GridColumns, tab.TermView.GridRows);

            if (tab.SavedLines.Count > 0)
            {
                session.Screen.PreloadHistory(tab.SavedLines);
                session.Screen.PreloadHistory(["── предыдущий сеанс выше, процессы не сохраняются ──", ""]);
            }

            session.Exited += () => OnTerminalExited(tab);
            session.DirectoryChanged += path => { OnTerminalDirectory(tab, path); if (ReferenceEquals(tab, _active)) UpdateTerminalTitle(); };
            session.TitleChanged += () => { if (ReferenceEquals(tab, _active)) UpdateTerminalTitle(); };

            session.Start(tab.Path, AppSettings.Current.TerminalShell);
            tab.Session = session;
        }

        tab.TermView.Attach(tab.Session);
        UpdateTerminalTitle();
        UpdateSyncButton();
    }

    private void UpdateTerminalTitle()
    {
        if (_active?.Session is not { } session)
        {
            TerminalTitle.Text = LanguageManager.Tr("L_Console");
            return;
        }

        // Заголовок окна оболочки обычно — путь к её exe, это шум. Показываем текущую папку.
        string folder = session.ReportedDirectory ?? _active.Path;
        TerminalTitle.Text = " " + session.ShellName.ToUpperInvariant() + " — " + folder +
                             (session.HasExited ? "   [завершён — Enter: перезапуск]" : "");
    }

    /// <summary>
    /// Подпись шапки консоли лежит на цвете «Selection», а цвет текста поверх него в палитрах
    /// рассчитан под другое (в Классике это тёмно-коричневый «Accent» на тёмно-синем).
    /// Поэтому берём белый или чёрный по яркости фона — читается в любой теме, в том числе своей.
    /// </summary>
    private void UpdateHeaderColors()
    {
        if (TryFindResource("Selection") is not System.Windows.Media.SolidColorBrush selection) return;

        System.Windows.Media.Color c = selection.Color;
        double brightness = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

        TerminalTitle.Foreground = brightness > 140
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.White;
    }

    private void UpdateSyncButton() =>
        SyncButton.Content = AppSettings.Current.TerminalFollowsPanel
            ? LanguageManager.Tr("L_SyncFolderOn")
            : LanguageManager.Tr("L_SyncFolderOff");

    private void OnTerminalExited(TabState tab)
    {
        tab.Session?.Screen.Feed(System.Text.Encoding.UTF8.GetBytes(
            "\r\n\x1b[2m[процесс завершён — нажмите Enter, чтобы запустить оболочку заново]\x1b[0m\r\n"));

        tab.TermView?.InvalidateVisual();
        if (ReferenceEquals(tab, _active)) UpdateTerminalTitle();
    }

    private void RestartTerminal(TabState tab)
    {
        // История остаётся: новая оболочка стартует под старым выводом.
        tab.SavedLines = tab.Session?.Screen.SnapshotText(SavedConsoleLines) ?? tab.SavedLines;

        tab.Session?.Dispose();
        tab.Session = null;

        if (ReferenceEquals(tab, _active)) ShowTerminalFor(tab);
    }

    private void DisposeTerminal(TabState tab)
    {
        tab.Session?.Dispose();
        tab.Session = null;
        tab.TermView = null;
    }

    // ---------- синхронизация папок ----------

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Оболочка сообщила новую папку (после cd) — панель файлов идёт за ней.
    /// Обратное направление ленивое, см. <see cref="SyncTerminalToPanel"/>.
    /// </summary>
    private async void OnTerminalDirectory(TabState tab, string path)
    {
        if (!AppSettings.Current.TerminalFollowsPanel) return;

        // Отчёт о папке, которую мы сами только что отправили, — не повод двигать панель назад.
        if (tab.Session?.IsOwnReport(path) == true) return;

        if (!Directory.Exists(path) || SamePath(path, tab.Path)) return;

        if (ReferenceEquals(tab, _active))
        {
            await NavigateAsync(path);
            return;
        }

        tab.Path = path;
        tab.RecordHistory(path);
        ScheduleSessionSave();
    }

    /// <summary>
    /// Панель сменила папку, а оболочка осталась в прежней — переводим её, когда пользователь
    /// собирается в ней работать (фокус на консоли). Печатать команду при каждом клике по папке
    /// засорило бы вывод; делаем это один раз, в нужный момент, и только если оболочка свободна.
    /// </summary>
    private void SyncTerminalToPanel(TabState tab)
    {
        if (!AppSettings.Current.TerminalFollowsPanel) return;
        if (tab.Session is not { HasExited: false } session) return;
        if (SamePath(session.ReportedDirectory, tab.Path)) return;
        if (!Directory.Exists(tab.Path)) return;

        session.ChangeDirectory(tab.Path);
    }

    private DispatcherTimer? _terminalSyncTimer;

    /// <summary>
    /// Если оболочка умеет менять папку скрыто, делаем это сразу после остановки панели в папке,
    /// а не при фокусе: к моменту, когда пользователь перейдёт в консоль, всё уже сделано, и
    /// ничего не печатается. Задержка гасит пролистывание по нескольким папкам подряд.
    /// </summary>
    private void ScheduleTerminalSync()
    {
        _terminalSyncTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };

        _terminalSyncTimer.Tick -= TerminalSyncTick;
        _terminalSyncTimer.Tick += TerminalSyncTick;
        _terminalSyncTimer.Stop();
        _terminalSyncTimer.Start();
    }

    private void TerminalSyncTick(object? sender, EventArgs e)
    {
        _terminalSyncTimer?.Stop();

        if (_active?.Session is { SilentSync: true }) SyncTerminalToPanel(_active);
    }

    private void TerminalSync_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.TerminalFollowsPanel = !AppSettings.Current.TerminalFollowsPanel;
        AppSettings.Current.Save();
        UpdateSyncButton();

        if (_active is not null) SyncTerminalToPanel(_active);
    }

    private void TerminalRestart_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) RestartTerminal(_active);
    }

    private void TerminalMaximize_Click(object sender, RoutedEventArgs e) => ToggleTerminalMaximized();

    /// <summary>Консоль на всё окно — то же, что Ctrl+O в Far, когда панели убирают с экрана.</summary>
    private void ToggleTerminalMaximized(bool? force = null)
    {
        bool next = force ?? !_terminalMaximized;
        if (next == _terminalMaximized) return;

        _terminalMaximized = next;

        if (next)
        {
            _savedSidebar = SidebarColumn.Width;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitColumn.Width = new GridLength(0);
            ListColumn.MinWidth = 0;
            ListColumn.Width = new GridLength(0);
            TerminalSplitColumn.Width = new GridLength(0);
            TerminalColumn.Width = new GridLength(1, GridUnitType.Star);
            FocusTerminal();
        }
        else
        {
            SidebarColumn.Width = _savedSidebar.Value > 0 ? _savedSidebar : new GridLength(200);
            SidebarSplitColumn.Width = new GridLength(4);
            ListColumn.MinWidth = 200;
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            TerminalSplitColumn.Width = new GridLength(4);
            TerminalColumn.Width = new GridLength(1.2, GridUnitType.Star);
        }

        ScheduleSessionSave();
    }

    private void FocusTerminal()
    {
        if (_active?.TermView is { } view) Keyboard.Focus(view);
    }

    private bool TerminalHasFocus => Keyboard.FocusedElement is TerminalView;

    private bool EditorHasFocus =>
        Keyboard.FocusedElement is DependencyObject element && IsInside(element, Editor);

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? current = element; current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container)) return true;
        }

        return false;
    }

    // ---------- ввод из списка файлов в консоль ----------

    /// <summary>
    /// Как в Far: начал печатать в панели — текст уходит в командную строку. Фокус переходит
    /// в консоль, оболочка при необходимости переводится в папку панели.
    /// </summary>
    private void FileList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_active?.TermView is not { } view || _active.Session is not { HasExited: false }) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

        string text = e.Text;
        if (text.Length == 0 || text[0] < ' ' || text == " ") return;

        e.Handled = true;
        Keyboard.Focus(view);
        view.TypeText(text);
    }

    // ---------- редактор ----------

    private void MenuEdit_Click(object sender, RoutedEventArgs e) => OpenEditorForSelection();

    private void OpenEditorForSelection()
    {
        if (Selected is not { IsDirectory: false } entry)
        {
            SetStatus("F4: выберите файл");
            return;
        }

        if (Editor.IsOpen && !Editor.TryClose()) return;

        if (!Editor.Open(entry.FullPath, out string message))
        {
            ReportError(message + ": " + entry.Name);
            return;
        }

        ToggleTerminalMaximized(false);
        _focusBeforeEditor = Keyboard.FocusedElement as Control;
        SetStatus(string.Format(LanguageManager.Tr("L_StatusEditing"), entry.FullPath));
    }

    private void Editor_Closed()
    {
        if (_focusBeforeEditor is { IsVisible: true } previous) Keyboard.Focus(previous);
        else FileList.Focus();
    }

    // ---------- Ctrl+Tab: вкладки по давности ----------

    /// <summary>Отмечает вкладку как только что использованную. Во время Ctrl+Tab порядок не трогается.</summary>
    private void TouchRecent(TabState tab)
    {
        if (_cycling) return;

        _recent.Remove(tab);
        _recent.Insert(0, tab);
    }

    /// <summary>
    /// Первое нажатие Ctrl+Tab открывает предыдущую (последнюю использованную) вкладку, каждое
    /// следующее при удержанном Ctrl идёт дальше вглубь списка — как Alt+Tab. Порядок фиксируется
    /// при отпускании Ctrl, поэтому быстрое Ctrl+Tab всегда «перекидывает» между двумя вкладками.
    /// </summary>
    private async Task CycleRecentAsync(int direction)
    {
        if (_tabs.Count < 2) return;

        if (!_cycling)
        {
            // Вкладки, которых нет в списке по давности, добавляются в конец.
            foreach (TabState tab in _tabs)
                if (!_recent.Contains(tab)) _recent.Add(tab);

            _recent.RemoveAll(tab => !_tabs.Contains(tab));

            _cycleOrder = [.. _recent];
            _cycleIndex = 0;
            _cycling = true;
        }

        _cycleIndex = ((_cycleIndex + direction) % _cycleOrder.Count + _cycleOrder.Count) % _cycleOrder.Count;
        await ActivateTabAsync(_cycleOrder[_cycleIndex]);
    }

    private void EndCycle()
    {
        if (!_cycling) return;

        _cycling = false;
        if (_active is not null) TouchRecent(_active);
        ScheduleSessionSave();
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl) EndCycle();
    }

    private void Window_Deactivated(object? sender, EventArgs e) => EndCycle();

    // ---------- сохранение сессии ----------

    /// <summary>
    /// Сессия пишется не только при выходе, но и вскоре после любого изменения: если программа
    /// упадёт или систему перезагрузят, вкладки не пропадут.
    /// </summary>
    private void ScheduleSessionSave()
    {
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        _saveTimer.Tick -= SaveTimerTick;
        _saveTimer.Tick += SaveTimerTick;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        SaveSession();
    }

    private void SaveSession()
    {
        var session = new SessionStore.Session
        {
            ActiveIndex = _active is null ? 0 : Math.Max(0, _tabs.IndexOf(_active)),
            ThemeIndex = ThemeManager.Current,
            WindowWidth = WindowState == WindowState.Maximized ? RestoreBounds.Width : Width,
            WindowHeight = WindowState == WindowState.Maximized ? RestoreBounds.Height : Height,
            Maximized = WindowState == WindowState.Maximized,
            TerminalMaximized = _terminalMaximized,
            RecentOrder = [.. _recent.Select(tab => _tabs.IndexOf(tab)).Where(index => index >= 0)],
        };

        foreach (TabState tab in _tabs)
        {
            session.Tabs.Add(new SessionStore.TabRecord
            {
                Path = tab.Path,
                Sort = tab.Sort,
                SortDescending = tab.SortDescending,
                IsPinned = tab.IsPinned,
                History = tab.History,
                HistoryPosition = tab.HistoryPosition,
                ConsoleLines = tab.Session?.Screen.SnapshotText(SavedConsoleLines) ?? tab.SavedLines,
            });
        }

        SessionStore.Save(session);
    }
}
