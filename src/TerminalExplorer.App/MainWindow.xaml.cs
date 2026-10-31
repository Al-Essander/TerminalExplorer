using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace TerminalExplorer;

public partial class MainWindow : Window
{
    /// <summary>Элемент боковой панели: диск с псевдографическим индикатором занятости.</summary>
    public sealed record DriveItem(string Header, string Bar, string Path);

    private readonly ObservableCollection<TabState> _tabs = [];
    private TabState? _active;

    /// <summary>
    /// Отменяет предыдущую загрузку. Без этого быстрое переключение вкладок приводит
    /// к гонке: медленная папка дочитывается позже быстрой и затирает её содержимое.
    /// </summary>
    private CancellationTokenSource? _loading;

    private List<FileEntry> _entries = [];

    /// <summary>
    /// Следит за открытой папкой. Нужен затем, что проводник Windows и другие программы
    /// меняют файлы независимо от нас, и без наблюдения список быстро расходится
    /// с действительностью — отсюда и сообщения вида «файл перемещён или изменён».
    /// </summary>
    private FileSystemWatcher? _folderWatcher;

    /// <summary>
    /// Перезагрузка откладывается: одна операция копирования порождает десятки событий,
    /// и перечитывать папку на каждое означало бы держать диск занятым без нужды.
    /// </summary>
    private DispatcherTimer? _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        TabBar.ItemsSource = _tabs;
        Editor.Closed += Editor_Closed;
        ThemeManager.Changed += UpdateHeaderColors;

        FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));

        // Меню закрыто не левым кликом (Esc, клик вне окна) — снимаем флаг, чтобы
        // следующий левый клик снова мог начать перетаскивание.
        if (FileList.ContextMenu is { } fileMenu)
            fileMenu.Closed += (_, _) => _menuOpen = false;

        SourceInitialized += (_, _) =>
        {
            // Хук ставится, как только у окна появляется дескриптор: раньше HwndSource нет.
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WindowProcedureHook);
        };

        Loaded += async (_, _) =>
        {
            // Перенос данных из %APPDATA% выполняется до первого чтения настроек.
            SessionStore.MigrateFromAppData();

            AppSettings.Load().ApplyToResources();
            // Язык по умолчанию — из настроек; применяется до построения строк-заголовков.
            LanguageManager.Apply((UiLanguage)AppSettings.Current.Language);
            Favorites.Load();
            FavoriteList.ItemsSource = Favorites.Items;

            LoadDrives();
            RefreshAddons();
            await RestoreSessionAsync();
            HandleCommandLine();
        };
    }

    /// <summary>
    /// Разбор аргументов запуска.
    ///
    /// Поддерживается «--folder-tree [папка]» — открыть генератор структуры сразу.
    /// Нужно для двух вещей: запускать генератор ярлыком, минуя меню, и проверять его,
    /// не прокликивая интерфейс.
    /// </summary>
    private void HandleCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 1; i < args.Length; i++)
        {
            // --theme N — применить тему сразу (нужно для проверки внешнего вида без кликов).
            if (string.Equals(args[i], "--theme", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                int.TryParse(args[i + 1], out int theme))
            {
                ThemeManager.Apply(theme);
                i++;
                continue;
            }

            if (string.Equals(args[i], "--details", StringComparison.OrdinalIgnoreCase))
            {
                ToggleDetails();
                continue;
            }

            // --select "путь" открывает папку файла и выделяет его — то же, что делает
            // «Показать в проводнике», только для нас самих.
            if (string.Equals(args[i], "--select", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                _ = SelectPathAsync(args[i + 1]);
                i++;
                continue;
            }

            if (!string.Equals(args[i], "--folder-tree", StringComparison.OrdinalIgnoreCase)) continue;

            string folder = i + 1 < args.Length && Directory.Exists(args[i + 1])
                ? args[i + 1]
                : _active?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            RunBuiltinAddon(new AddonManifest { Id = "folder-tree" }, folder);
            return;
        }
    }

    /// <summary>Переходит в папку объекта и выделяет его в списке.</summary>
    private async Task SelectPathAsync(string path)
    {
        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder)) return;

        await NavigateAsync(folder);

        FileEntry? target = _entries.FirstOrDefault(
            entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase));

        if (target is null) return;

        FileList.SelectedItem = target;
        FileList.ScrollIntoView(target);
        UpdateDetails();
    }

    // ---------- избранное ----------

    private void AddFavorite_Click(object sender, RoutedEventArgs e) => AddCurrentToFavorites();

    private void AddCurrentToFavorites()
    {
        if (_active is null) return;

        SetStatus(Favorites.Add(_active.Path)
            ? string.Format(LanguageManager.Tr("L_StatusFavorite"), _active.Path)
            : string.Format(LanguageManager.Tr("L_StatusAlreadyFavorite"), _active.Path));
    }

    private async void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FavoriteItem item }) await OpenFavoriteAsync(item);
    }

    private async Task OpenFavoriteAsync(FavoriteItem item, bool newTab = false)
    {
        if (!Directory.Exists(item.Path))
        {
            // Папку могли удалить или это съёмный диск — сообщаем, но из списка не выбрасываем.
            ReportError(string.Format(LanguageManager.Tr("L_FolderUnavailable"), item.Path));
            return;
        }

        if (newTab) await NewTabAsync(item.Path);
        else await NavigateAsync(item.Path);
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FavoriteItem item }) Favorites.Remove(item);
    }

    /// <summary>Достаёт объект, на котором открыли контекстное меню.</summary>
    private static T? MenuTarget<T>(object sender) where T : class =>
        sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { Tag: T target } } }
            ? target
            : null;

    private async void FavoriteMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<FavoriteItem>(sender) is { } item) await OpenFavoriteAsync(item);
    }

    private async void FavoriteMenuOpenInTab_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<FavoriteItem>(sender) is { } item) await OpenFavoriteAsync(item, newTab: true);
    }

    private void FavoriteMenuUp_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<FavoriteItem>(sender) is { } item) Favorites.Move(item, -1);
    }

    private void FavoriteMenuDown_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<FavoriteItem>(sender) is { } item) Favorites.Move(item, 1);
    }

    private void FavoriteMenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<FavoriteItem>(sender) is { } item) CopyPathToClipboard(item.Path);
    }

    private void FavoriteMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<FavoriteItem>(sender) is { } item) Favorites.Remove(item);
    }

    // ---------- меню дисков ----------

    private async void DriveMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<string>(sender) is { } path) await NavigateAsync(path);
    }

    private async void DriveMenuOpenInTab_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<string>(sender) is { } path) await NewTabAsync(path);
    }

    private void DriveMenuFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<string>(sender) is not { } path) return;

        SetStatus(Favorites.Add(path)
            ? string.Format(LanguageManager.Tr("L_StatusFavorite"), path)
            : string.Format(LanguageManager.Tr("L_StatusAlreadyFavorite"), path));
    }

    private void DriveMenuTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<string>(sender) is { } path) ShellActions.OpenTerminal(path);
    }

    private void DriveMenuProperties_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTarget<string>(sender) is { } path)
            ShellActions.ShowProperties(path, new WindowInteropHelper(this).Handle);
    }

    private void MenuAddFavorite_Click(object sender, RoutedEventArgs e)
    {
        string? path = Selected is { IsDirectory: true } entry ? entry.FullPath : _active?.Path;
        if (path is null) return;

        SetStatus(Favorites.Add(path)
            ? string.Format(LanguageManager.Tr("L_StatusFavorite"), path)
            : string.Format(LanguageManager.Tr("L_StatusAlreadyFavorite"), path));
    }

    // ---------- меню оболочки ----------

    /// <summary>Точка последнего ПКМ в экранных координатах — туда же встанет меню Windows из пункта «Ещё».</summary>
    private Point _menuScreenPoint;

    /// <summary>
    /// ПКМ выделяет строку под курсором (в WPF этого по умолчанию нет), а клик по пустому
    /// месту снимает выделение — тогда меню относится к самой папке.
    /// </summary>
    private void FileList_RightDown(object sender, MouseButtonEventArgs e)
    {
        _menuScreenPoint = PointToScreen(e.GetPosition(this));
        // Собственное меню открыто; убираем его следующим левым кликом.
        _menuOpen = true;

        if (ItemUnderMouse(e) is { } clicked)
        {
            if (!FileList.SelectedItems.Contains(clicked)) FileList.SelectedItem = clicked;
        }
        else
        {
            FileList.UnselectAll();
        }
    }

    /// <summary>
    /// Обычный ПКМ открывает собственное меню — оно появляется мгновенно. Меню Windows строится
    /// через IContextMenu и ждёт каждое расширение оболочки (архиваторы, Git, редакторы),
    /// отсюда и задержка в секунду-две; поэтому оно вынесено на Shift+ПКМ и в пункт «Ещё».
    /// </summary>
    private async void FileList_ShellMenu(object sender, MouseButtonEventArgs e)
    {
        if (_active is null) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        e.Handled = true;
        await ShowShellMenuAsync();
    }

    private async void MenuShell_Click(object sender, RoutedEventArgs e) => await ShowShellMenuAsync();

    private async Task ShowShellMenuAsync()
    {
        if (_active is null) return;

        var paths = new List<string>();

        foreach (object item in FileList.SelectedItems)
            if (item is FileEntry entry) paths.Add(entry.FullPath);

        bool shown = ShellContextMenu.Show(
            paths, _active.Path, new WindowInteropHelper(this).Handle,
            (int)_menuScreenPoint.X, (int)_menuScreenPoint.Y);

        if (!shown)
        {
            SetStatus(LanguageManager.Tr("L_ShellMenuUnavailable"));
            return;
        }

        // Команда могла создать, удалить или переименовать объект — перечитываем папку.
        await LoadCurrentAsync(recordHistory: false);
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard(cut: false);

    private void MenuCut_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard(cut: true);

    private async void MenuPaste_Click(object sender, RoutedEventArgs e) => await PasteFromClipboardAsync();

    private async void MenuDelete_Click(object sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    /// <summary>Находит строку списка под курсором, поднимаясь по визуальному дереву.</summary>
    private FileEntry? ItemUnderMouse(MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return null;

        while (source is not null and not ListViewItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        return (source as ListViewItem)?.DataContext as FileEntry;
    }

    /// <summary>
    /// Хук оконной процедуры. Нужен исключительно меню оболочки: подменю сторонних
    /// расширений наполняются в ответ на оконные сообщения, и без переадресации
    /// открываются пустыми.
    /// </summary>
    private nint WindowProcedureHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (ShellContextMenu.HandleMenuMessage(message, wParam, lParam, out nint result))
        {
            handled = true;
            return result;
        }

        return 0;
    }

    // ---------- аддоны ----------

    private void RefreshAddons()
    {
        AddonManager.Reload();

        AddonList.ItemsSource = null;
        AddonList.ItemsSource = AddonManager.Pinned.ToList();
    }

    private void Addon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AddonManifest addon }) RunAddon(addon);
    }

    private void AddonsMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = (Button)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_RefreshList"), () =>
        {
            RefreshAddons();
            SetStatus(LanguageManager.Tr("L_AddonsFound") + ": " + AddonManager.Addons.Count);
        }));

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_ProgramAddons"), () =>
            ShellActions.OpenFolder(AddonManager.DirectoryPath)));

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_UserAddons"), () =>
        {
            Directory.CreateDirectory(AddonManager.UserDirectoryPath);
            ShellActions.OpenFolder(AddonManager.UserDirectoryPath);
        }));

        menu.IsOpen = true;
    }

    // ---------- настройки ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowMainMenu((Button)sender);

    /// <summary>
    /// Главное меню. Строится заново при каждом открытии: список аддонов и избранного
    /// меняется в рантайме, а меню, собранное один раз в разметке, показывало бы устаревшее.
    /// </summary>
    private void ShowMainMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_NewTab") + "\tCtrl+T", async () =>
            await NewTabAsync(_active?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_MenuFavorite") + "\tCtrl+D", AddCurrentToFavorites));
        menu.Items.Add(new Separator());

        // --- аддоны ---
        AddonManager.Reload();

        var addons = new MenuItem { Header = LanguageManager.Tr("L_AddonsSubmenu") };

        foreach (AddonManifest addon in AddonManager.Addons)
        {
            if (addon.Placement == AddonPlacement.Context) continue;

            AddonManifest captured = addon;
            addons.Items.Add(MenuEntry(addon.Name, () => RunAddon(captured)));
        }

        addons.Items.Add(new Separator());
        addons.Items.Add(MenuEntry(LanguageManager.Tr("L_ProgramAddons"), () =>
            ShellActions.OpenFolder(AddonManager.DirectoryPath)));

        addons.Items.Add(MenuEntry(LanguageManager.Tr("L_UserAddons"), () =>
        {
            Directory.CreateDirectory(AddonManager.UserDirectoryPath);
            ShellActions.OpenFolder(AddonManager.UserDirectoryPath);
        }));

        menu.Items.Add(addons);
        menu.Items.Add(new Separator());

        // --- места ---
        var places = new MenuItem { Header = LanguageManager.Tr("L_QuickAccess") };

        foreach (FavoriteItem favorite in Favorites.Items)
        {
            FavoriteItem captured = favorite;
            places.Items.Add(MenuEntry(favorite.Display, async () => await OpenFavoriteAsync(captured)));
        }

        if (Favorites.Items.Count == 0)
            places.Items.Add(new MenuItem { Header = LanguageManager.Tr("L_FavoritesEmpty"), IsEnabled = false });

        menu.Items.Add(places);
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_OpenTerminalHere"), () =>
        {
            if (_active is not null) ShellActions.OpenTerminal(_active.Path);
        }));

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_DataFolder"), () =>
        {
            Directory.CreateDirectory(SessionStore.DirectoryPath);
            ShellActions.OpenFolder(SessionStore.DirectoryPath);
        }));

        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_Settings") + "\tF10", OpenSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(LanguageManager.Tr("L_Exit"), Close));

        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();

        return item;
    }

    /// <summary>Запускает аддон: встроенный открывает своё окно, внешний — свой файл.</summary>
    private void RunAddon(AddonManifest addon)
    {
        string? folder = _active?.Path;

        if (addon.RequiresFolder && folder is null)
        {
            ReportError(LanguageManager.Tr("L_AddonNeedsFolder"));
            return;
        }

        if (addon.IsBuiltin)
        {
            RunBuiltinAddon(addon, folder!);
            return;
        }

        string? error = AddonManager.Run(addon, folder!, Selected?.FullPath);

        if (error is null) SetStatus(string.Format(LanguageManager.Tr("L_AddonStarted"), addon.Name));
        else ReportError(addon.Name + ": " + error);
    }

    private void RunBuiltinAddon(AddonManifest addon, string folder)
    {
        switch (addon.Id)
        {
            case "folder-tree":
                var window = new FolderTreeWindow(folder) { Owner = this };
                window.ShowDialog();

                // Аддон мог создать папки — показываем результат сразу.
                _ = LoadCurrentAsync(recordHistory: false);
                break;

            default:
                ReportError(string.Format(LanguageManager.Tr("L_UnknownBuiltinAddon"), addon.Id));
                break;
        }
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow { Owner = this };
        window.ShowDialog();

        // Настройки могли изменить поведение и палитру — обновляем то, что не на DynamicResource.
        LoadDrives();
    }

    /// <summary>Открывает окно поиска (Ctrl+F или кнопка «?»).</summary>
    private void Search_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        // Поиск может быть долгим, поэтому окно модельное: владелец не должен дёргать
        // общий индекс, пока там идёт скан. Сам поиск при этом не блокирует UI.
        var window = new SearchWindow(_active?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    // ---------- сессия ----------

    private async Task RestoreSessionAsync()
    {
        SessionStore.Session session = SessionStore.Load();

        // Тема живёт в настройках, а не в сессии: она относится к «как выглядит программа».
        ThemeManager.Apply(AppSettings.Current.ThemeIndex);

        if (session.WindowWidth > 200 && session.WindowHeight > 200)
        {
            Width = session.WindowWidth;
            Height = session.WindowHeight;
        }

        if (session.Maximized) WindowState = WindowState.Maximized;

        foreach (SessionStore.TabRecord record in session.Tabs)
        {
            // Папка могла исчезнуть между запусками — такие вкладки просто не восстанавливаем.
            if (!Directory.Exists(record.Path)) continue;

            _tabs.Add(new TabState
            {
                Path = record.Path,
                Sort = record.Sort,
                SortDescending = record.SortDescending,
                IsPinned = record.IsPinned,
                SavedLines = record.ConsoleLines,
                History = record.History.Count > 0 ? record.History : [record.Path],
                HistoryPosition = record.HistoryPosition >= 0
                    ? Math.Min(record.HistoryPosition, Math.Max(0, record.History.Count - 1))
                    : 0,
            });
        }

        if (_tabs.Count == 0)
            _tabs.Add(new TabState { Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });

        // Порядок «по давности» для Ctrl+Tab; недостающие вкладки добавляются в конец.
        // Индексы RecentOrder относятся к порядку из сессии, поэтому работаем по restoredOrder,
        // а не по _tabs после переноса закреплённых вкладок в начало.
        List<TabState> restoredOrder = [.. _tabs];

        foreach (int recent in session.RecentOrder)
            if (recent >= 0 && recent < restoredOrder.Count && !_recent.Contains(restoredOrder[recent]))
                _recent.Add(restoredOrder[recent]);

        foreach (TabState tab in _tabs)
            if (!_recent.Contains(tab)) _recent.Add(tab);

        // Закреплённые вкладки держим в начале полосы, как при перетаскивании.
        StabilizePinned();

        int index = Math.Clamp(session.ActiveIndex, 0, _tabs.Count - 1);
        await ActivateTabAsync(_tabs[index]);

        if (session.TerminalMaximized) ToggleTerminalMaximized(true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Editor.IsOpen && !Editor.TryClose())
        {
            // Несохранённая правка: не закрываем окно молча.
            e.Cancel = true;
            return;
        }

        SaveSession();

        foreach (TabState tab in _tabs) tab.Session?.Dispose();

        base.OnClosing(e);
    }
    // ---------- вкладки ----------

    private async Task ActivateTabAsync(TabState tab)
    {
        bool switching = _active is not null && !ReferenceEquals(_active, tab);

        foreach (TabState other in _tabs) other.IsActive = ReferenceEquals(other, tab);

        _active = tab;
        if (switching) Sounds.Play(Sounds.TabSwitch);

        TouchRecent(tab);
        ShowTerminalFor(tab);
        ScheduleSessionSave();

        await LoadCurrentAsync(recordHistory: false);
    }

    private async Task NewTabAsync(string path)
    {
        var tab = new TabState { Path = path };
        tab.RecordHistory(path);

        _tabs.Add(tab);
        await ActivateTabAsync(tab);
    }

    private async Task CloseTabAsync(TabState tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;

        // Закрытие последней вкладки закрывает окно — как в браузере.
        if (_tabs.Count == 1) { Close(); return; }

        _tabs.RemoveAt(index);
        _recent.Remove(tab);
        DisposeTerminal(tab);
        Sounds.Play(Sounds.TabClose);

        if (ReferenceEquals(tab, _active))
            await ActivateTabAsync(_recent.FirstOrDefault(_tabs.Contains) ?? _tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) =>
        await NewTabAsync(_active?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private async void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabState tab }) await CloseTabAsync(tab);
    }

    private async void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabState tab }) return;

        switch (e.ChangedButton)
        {
            case MouseButton.Middle:
                e.Handled = true;
                await CloseTabAsync(tab);
                break;

            case MouseButton.Left:
                e.Handled = true;
                if (!ReferenceEquals(tab, _active)) await ActivateTabAsync(tab);
                break;
        }
    }

    private Point? _dragStart;
    private TabState? _dragTab;

    private void Tab_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragTab = null; _dragStart = null; return; }

        if (sender is not FrameworkElement { Tag: TabState tab }) return;

        Point position = e.GetPosition(TabBar);
        _dragTab ??= tab;
        _dragStart ??= position;

        // Начинаем перетаскивание только после сдвига на порог, чтобы клик не стал драгом.
        if (_dragStart is { } start && (Math.Abs(position.X - start.X) > SystemParameters.MinimumHorizontalDragDistance))
            DragDrop.DoDragDrop(TabBar, tab, DragDropEffects.Move);
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(TabState)) is TabState ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabState tab }) return;
        if (e.Data.GetData(typeof(TabState)) is not TabState dragged || ReferenceEquals(dragged, tab)) return;

        MoveTab(dragged, tab);
        e.Handled = true;
    }

    /// <summary>Меняет порядок вкладок, сохраняя закреплённые в начале полосы.</summary>
    private void MoveTab(TabState dragged, TabState target)
    {
        int from = _tabs.IndexOf(dragged);
        int to = _tabs.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        _tabs.Move(from, to);

        StabilizePinned();

        ScheduleSessionSave();
    }

    /// <summary>Переставляет закреплённые вкладки в начало полосы, сохраняя их взаимный порядок.</summary>
    private void StabilizePinned()
    {
        var pinned = _tabs.Where(x => x.IsPinned).ToList();
        var unpinned = _tabs.Where(x => !x.IsPinned).ToList();

        if (pinned.Count == _tabs.Count || pinned.Count == 0) return;

        _tabs.Clear();
        foreach (TabState tab in pinned) _tabs.Add(tab);
        foreach (TabState tab in unpinned) _tabs.Add(tab);
    }

    private async void TabPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabState tab }) return;

        tab.IsPinned = !tab.IsPinned;
        StabilizePinned();

        ScheduleSessionSave();
        await Task.CompletedTask;
    }

    // ---------- навигация ----------

    private async Task NavigateAsync(string path, bool recordHistory = true)
    {
        if (_active is null || string.IsNullOrWhiteSpace(path)) return;

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ReportError(string.Format(LanguageManager.Tr("L_InvalidPath"), path));
            return;
        }

        if (!Directory.Exists(path))
        {
            ReportError(File.Exists(path)
                ? string.Format(LanguageManager.Tr("L_NotAFolder"), path)
                : string.Format(LanguageManager.Tr("L_FolderNotFound"), path));
            return;
        }

        ClearError();
        _active.Path = path;
        await LoadCurrentAsync(recordHistory);
    }

    /// <summary>
    /// Звук перехода по направлению: вглубь — «вход», наверх — «выход», в сторону — обычный.
    /// Сравнивается с папкой, на которой этот звук играл в прошлый раз, поэтому автообновление,
    /// переключение вкладок и «назад/вперёд» ведут себя одинаково: звук есть, когда папка сменилась.
    /// </summary>
    private static void PlayFolderSound(TabState tab)
    {
        string? previous = tab.SoundPath;
        tab.SoundPath = tab.Path;

        if (previous is null || SamePath(previous, tab.Path)) return;

        string from = previous.TrimEnd('\\') + "\\";
        string to = tab.Path.TrimEnd('\\') + "\\";

        if (to.StartsWith(from, StringComparison.OrdinalIgnoreCase)) Sounds.Play(Sounds.FolderIn);
        else if (from.StartsWith(to, StringComparison.OrdinalIgnoreCase)) Sounds.Play(Sounds.FolderOut);
        else Sounds.Play(Sounds.Navigate);
    }

    private async Task LoadCurrentAsync(bool recordHistory)
    {
        if (_active is null) return;

        TabState tab = _active;

        _loading?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loading = cancellation;

        PathBox.Text = tab.Path;
        ScheduleSessionSave();
        ScheduleTerminalSync();
        PromptText.Text = " " + (Path.GetPathRoot(tab.Path) ?? "C:\\").TrimEnd('\\') + "\\> ";
        SetStatus(LanguageManager.Tr("L_Reading"));

        try
        {
            IReadOnlyList<FileEntry> loaded =
                await DirectoryLoader.LoadAsync(tab.Path, tab.Sort, tab.SortDescending, cancellation.Token);

            // Пока читали, пользователь мог переключить вкладку — результат уже не нужен.
            if (cancellation.IsCancellationRequested || !ReferenceEquals(tab, _active)) return;

            // Выделение сохраняется по путям: при автообновлении объекты списка создаются
            // заново, и без этого любое изменение в активной папке сбрасывало бы выбор.
            var selected = new HashSet<string>(
                FileList.SelectedItems.OfType<FileEntry>().Select(entry => entry.FullPath),
                StringComparer.OrdinalIgnoreCase);

            _entries = [.. loaded];
            FileList.ItemsSource = _entries;

            if (selected.Count > 0) RestoreSelection(selected);

            if (recordHistory) tab.RecordHistory(tab.Path);

            PlayFolderSound(tab);

            UpdateStatus();
            UpdateDiskInfo(tab.Path);
            UpdateNavigationButtons();
            WatchCurrentFolder(tab.Path);

            if (_entries.Count > 0) FileList.ScrollIntoView(_entries[0]);
        }
        catch (OperationCanceledException)
        {
            // Загрузку перебила следующая — штатный ход, не ошибка.
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus(string.Format(LanguageManager.Tr("L_AccessDenied"), tab.Path));
        }
        catch (IOException ex)
        {
            SetStatus(string.Format(LanguageManager.Tr("L_ReadError"), ex.Message));
        }
    }

    /// <summary>
    /// Ставит наблюдение за открытой папкой и перечитывает её при изменениях извне.
    /// </summary>
    private void WatchCurrentFolder(string path)
    {
        _folderWatcher?.Dispose();
        _folderWatcher = null;

        _refreshTimer ??= CreateRefreshTimer();

        try
        {
            _folderWatcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _folderWatcher.Changed += OnFolderChanged;
            _folderWatcher.Created += OnFolderChanged;
            _folderWatcher.Deleted += OnFolderChanged;
            _folderWatcher.Renamed += OnFolderChanged;

            // Переполнение внутреннего буфера означает, что часть событий потеряна —
            // единственный верный ответ на это полная перезагрузка списка.
            _folderWatcher.Error += (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Сетевые и съёмные диски наблюдение могут не поддерживать — работаем без него.
        }
    }

    private DispatcherTimer CreateRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await LoadCurrentAsync(recordHistory: false);
        };

        return timer;
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(ScheduleRefresh);

    /// <summary>Сбрасывает таймер: перезагрузка случится через паузу после последнего события.</summary>
    private void ScheduleRefresh()
    {
        if (_refreshTimer is null) return;

        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Возвращает выделение после перезагрузки списка.</summary>
    private void RestoreSelection(HashSet<string> paths)
    {
        FileEntry? first = null;

        foreach (FileEntry entry in _entries)
        {
            if (!paths.Contains(entry.FullPath)) continue;

            FileList.SelectedItems.Add(entry);
            first ??= entry;
        }

        if (first is not null) FileList.ScrollIntoView(first);
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _active?.CanGoBack ?? false;
        ForwardButton.IsEnabled = _active?.CanGoForward ?? false;
        UpButton.IsEnabled = _active?.CanGoUp ?? false;
    }

    private async Task GoBackAsync()
    {
        if (_active is not { CanGoBack: true } tab) return;

        tab.HistoryPosition--;
        tab.Path = tab.History[tab.HistoryPosition];
        await LoadCurrentAsync(recordHistory: false);
    }

    private async Task GoForwardAsync()
    {
        if (_active is not { CanGoForward: true } tab) return;

        tab.HistoryPosition++;
        tab.Path = tab.History[tab.HistoryPosition];
        await LoadCurrentAsync(recordHistory: false);
    }

    private async Task GoUpAsync()
    {
        if (_active is null) return;

        DirectoryInfo? parent = Directory.GetParent(_active.Path);
        if (parent is not null) await NavigateAsync(parent.FullName);
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await GoBackAsync();
    private async void Forward_Click(object sender, RoutedEventArgs e) => await GoForwardAsync();
    private async void Up_Click(object sender, RoutedEventArgs e) => await GoUpAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await NavigateAsync(PathBox.Text.Trim());
        FileList.Focus();
    }

    private async void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) await NavigateAsync(path);
    }

    // ---------- список ----------

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        await OpenSelectedAsync();

    private async void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await OpenSelectedAsync();
                break;

            case Key.Back:
                e.Handled = true;
                await GoUpAsync();
                break;

            case Key.F5:
                e.Handled = true;
                await LoadCurrentAsync(recordHistory: false);
                break;

            case Key.Delete:
                e.Handled = true;
                await DeleteSelectedAsync();
                break;
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (FileList.SelectedItem is not FileEntry entry) return;

        if (entry.IsDirectory)
        {
            if (AppSettings.Current.OpenFoldersInNewTab) await NewTabAsync(entry.FullPath);
            else await NavigateAsync(entry.FullPath);
            return;
        }

        // Текстовые файлы и PDF открываются во встроенном предпросмотре одним нажатием,
        // а не в стороннем приложении (внешнее остаётся доступным из меню «Открыть с помощью»).
        if (PreviewWindow.CanPreview(entry.FullPath))
        {
            var preview = new PreviewWindow(entry.FullPath) { Owner = this };
            preview.Show();
            return;
        }

        try
        {
            ShellActions.Open(entry.FullPath);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(LanguageManager.Tr("L_OpenError"), ex.Message));
            Sounds.Play(Sounds.Error);
        }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        UpdateDetails();
    }

    // ---------- панель сведений ----------

    private bool _detailsVisible;

    private void ToggleDetails()
    {
        _detailsVisible = !_detailsVisible;

        Details.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = _detailsVisible ? new GridLength(360) : new GridLength(0);

        if (_detailsVisible) UpdateDetails();
        else Details.Release();
    }

    /// <summary>
    /// Обновляет панель под выделение. Анализ папки запускается только когда панель
    /// открыта: обход дерева стоит дорого, и делать его вслепую незачем.
    /// </summary>
    private void UpdateDetails()
    {
        if (!_detailsVisible) return;

        if (Selected is { } entry)
        {
            if (entry.IsDirectory) Details.ShowFolder(entry.FullPath, entry.Name);
            else Details.ShowFile(entry);

            return;
        }

        if (_active is not null) Details.ShowFolder(_active.Path, _active.Title);
        else Details.ShowNothing();
    }

    // ---------- перетаскивание ----------

    /// <summary>Точка нажатия — от неё отсчитывается порог начала перетаскивания.</summary>
    private Point _dragOrigin;

    /// <summary>
    /// ПКМ открыл собственное меню. Следующий левый клик его закрывает; на этом клике
    /// нельзя начинать перетаскивание, иначе лёгкое дрожание руки превратило бы «закрыть меню»
    /// в drag и скопировало бы файл в ту же папку («имя - копия»).
    /// </summary>
    private bool _menuOpen;

    /// <summary>Давление сброшено для текущей сессии «нажал-повёл-отпустил» (см. <see cref="_menuOpen"/>).</summary>
    private bool _suppressDrag;

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);

        // Кнопка, закрывающая контекстное меню, не начинает перетаскивание.
        _suppressDrag = _menuOpen;
        _menuOpen = false;
    }

    /// <summary>
    /// Начинает перетаскивание файлов наружу. Порог из SystemParameters обязателен:
    /// без него любой клик с дрожанием руки превращается в drag, и выделять мышью
    /// становится невозможно.
    /// </summary>
    private void FileList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // Клик, закрывающий меню, не должен начинать перетаскивание.
        // Флаг снимается по отпусканию кнопки (см. FileList_MouseLeftButtonUp).
        if (_suppressDrag) return;

        Vector shift = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(shift.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(shift.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var paths = new System.Collections.Specialized.StringCollection();
        foreach (object item in FileList.SelectedItems)
            if (item is FileEntry entry) paths.Add(entry.FullPath);

        if (paths.Count == 0) return;

        var data = new DataObject();
        data.SetFileDropList(paths);

        DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _active is null)
        {
            e.Effects = DragDropEffects.None;
        }
        else
        {
            // Shift — перемещение, иначе копирование: то же соглашение, что в проводнике.
            e.Effects = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }

        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (_active is null || e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

        bool move = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
        string destination = _active.Path;

        string[] sources = dropped.Where(path => IsDroppable(path, destination, move)).ToArray();

        if (sources.Length == 0)
        {
            SetStatus(LanguageManager.Tr("L_NothingToMove"));
            return;
        }

        bool done = move
            ? FileOperations.Move(sources, destination, new WindowInteropHelper(this).Handle)
            : FileOperations.Copy(sources, destination, new WindowInteropHelper(this).Handle);

        SetStatus(done
            ? string.Format(LanguageManager.Tr(move ? "L_Moved" : "L_Copied"), sources.Length)
            : LanguageManager.Tr("L_OperationCancelled"));

        await LoadCurrentAsync(recordHistory: false);
    }

    /// <summary>
    /// Отсеивает то, что нельзя бросить в эту папку.
    ///
    /// Три случая: сама папка назначения, перемещение объекта туда, где он уже лежит,
    /// и попытка вложить папку внутрь самой себя. Последнее особенно важно — SHFileOperation
    /// на такой операции ведёт себя непредсказуемо.
    /// </summary>
    private static bool IsDroppable(string source, string destination, bool move)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return false;

        string? parent = Path.GetDirectoryName(source);
        if (move && string.Equals(parent, destination, StringComparison.OrdinalIgnoreCase)) return false;

        // Папку нельзя переносить внутрь собственного поддерева.
        string prefix = source.TrimEnd('\\') + "\\";
        if (destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    // ---------- переименование и буфер обмена ----------

    /// <summary>
    /// Переименование по F2. Диалог свой, а не системный: у оболочки нет готового
    /// окна переименования, которое можно вызвать снаружи — проводник рисует поле
    /// прямо в списке, а это переписывание шаблона строки ради одной операции.
    /// </summary>
    private async Task RenameSelectedAsync()
    {
        if (Selected is not { } entry) { SetStatus(LanguageManager.Tr("L_SelectObject")); return; }

        var dialog = new RenameWindow(entry.Name, entry.IsDirectory) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        string target = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, dialog.NewName);

        if (string.Equals(target, entry.FullPath, StringComparison.Ordinal)) return;

        try
        {
            if (File.Exists(target) || Directory.Exists(target))
            {
                ReportError(string.Format(LanguageManager.Tr("L_NameTaken"), dialog.NewName));
                return;
            }

            if (entry.IsDirectory) Directory.Move(entry.FullPath, target);
            else File.Move(entry.FullPath, target);

            SetStatus(string.Format(LanguageManager.Tr("L_Renamed"), dialog.NewName));
            await LoadCurrentAsync(recordHistory: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError(string.Format(LanguageManager.Tr("L_RenameError"), ex.Message));
        }
    }

    /// <summary>
    /// Копирование в буфер в формате оболочки: список файлов плюс «предпочтительный
    /// эффект». Именно по этому эффекту принимающая сторона отличает копирование
    /// от вырезания — отдельного формата для вырезанных файлов в Windows нет.
    /// </summary>
    private void CopySelectionToClipboard(bool cut)
    {
        var paths = new System.Collections.Specialized.StringCollection();
        foreach (object item in FileList.SelectedItems)
            if (item is FileEntry entry) paths.Add(entry.FullPath);

        if (paths.Count == 0) return;

        try
        {
            var data = new DataObject();
            data.SetFileDropList(paths);

            // DROPEFFECT_COPY = 5, DROPEFFECT_MOVE = 2.
            var effect = new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5));
            data.SetData("Preferred DropEffect", effect);

            Clipboard.SetDataObject(data, copy: true);
            SetStatus(string.Format(LanguageManager.Tr(cut ? "L_Cut" : "L_Copied"), paths.Count));
        }
        catch (Exception ex)
        {
            ReportError(string.Format(LanguageManager.Tr("L_ClipboardBusy"), ex.Message));
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        if (_active is null) return;

        try
        {
            if (!Clipboard.ContainsFileDropList()) { SetStatus(LanguageManager.Tr("L_ClipboardEmpty")); return; }

            string[] sources = [.. Clipboard.GetFileDropList().Cast<string>().Where(path => path is not null)];
            if (sources.Length == 0) return;

            bool cut = ReadPreferredDropEffect() == 2;
            string destination = _active.Path;

            string[] filtered = sources.Where(path => IsDroppable(path, destination, cut)).ToArray();
            if (filtered.Length == 0) { SetStatus(LanguageManager.Tr("L_NothingToPaste")); return; }

            nint owner = new WindowInteropHelper(this).Handle;

            bool done = cut
                ? FileOperations.Move(filtered, destination, owner)
                : FileOperations.Copy(filtered, destination, owner);

            SetStatus(done
            ? string.Format(LanguageManager.Tr(cut ? "L_Moved" : "L_Copied"), filtered.Length)
            : LanguageManager.Tr("L_OperationCancelled"));

            // После вырезания буфер очищается: повторная вставка перенесла бы то,
            // чего на прежнем месте уже нет.
            if (done && cut) Clipboard.Clear();

            await LoadCurrentAsync(recordHistory: false);
        }
        catch (Exception ex) when (ex is ExternalException or IOException)
        {
            ReportError(string.Format(LanguageManager.Tr("L_PasteError"), ex.Message));
        }
    }

    private static int ReadPreferredDropEffect()
    {
        if (Clipboard.GetData("Preferred DropEffect") is not MemoryStream stream) return 5;

        byte[] buffer = new byte[4];
        return stream.Read(buffer, 0, 4) == 4 ? BitConverter.ToInt32(buffer) : 5;
    }

    /// <summary>Удаление выделенного в Корзину.</summary>
    private async Task DeleteSelectedAsync()
    {
        var paths = new List<string>();
        foreach (object item in FileList.SelectedItems)
            if (item is FileEntry entry) paths.Add(entry.FullPath);

        if (paths.Count == 0) return;

        FileOperations.DeleteToRecycleBin(paths, new WindowInteropHelper(this).Handle);
        await LoadCurrentAsync(recordHistory: false);
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (_active is null) return;
        if (e.OriginalSource is not GridViewColumnHeader { Content: string header }) return;

        SortColumn column = header switch
        {
            "РАЗМЕР" => SortColumn.Size,
            "ИЗМЕНЁН" => SortColumn.Modified,
            "ТИП" => SortColumn.Type,
            _ => SortColumn.Name,
        };

        // Повторный клик по той же колонке переключает направление.
        if (column == _active.Sort) _active.SortDescending = !_active.SortDescending;
        else { _active.Sort = column; _active.SortDescending = false; }

        DirectoryLoader.Sort(_entries, _active.Sort, _active.SortDescending);

        // Список — тот же объект, поэтому привязку надо перевесить принудительно.
        FileList.ItemsSource = null;
        FileList.ItemsSource = _entries;
    }

    // ---------- контекстное меню ----------

    private FileEntry? Selected => FileList.SelectedItem as FileEntry;

    private async void MenuOpen_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();

    private void MenuPreview_Click(object sender, RoutedEventArgs e) => OpenPreviewForSelection();

    private void OpenPreviewForSelection()
    {
        if (Selected is not { IsDirectory: false } entry) { SetStatus(LanguageManager.Tr("L_SelectObject")); return; }

        if (!PreviewWindow.CanPreview(entry.FullPath))
        {
            SetStatus(LanguageManager.Tr("L_PreviewUnsupported") + ": " + entry.Extension);
            return;
        }

        var preview = new PreviewWindow(entry.FullPath) { Owner = this };
        preview.Show();
    }

    private async void MenuRename_Click(object sender, RoutedEventArgs e) => await RenameSelectedAsync();

    private async void MenuOpenInTab_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { IsDirectory: true } entry) await NewTabAsync(entry.FullPath);
        else if (_active is not null) await NewTabAsync(_active.Path);
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e) =>
        CopyPathToClipboard(Selected?.FullPath ?? _active?.Path);

    private void CopyPathToClipboard(string? path)
    {
        if (path is null) return;

        try
        {
            // В кавычках — так путь сразу пригоден для вставки в командную строку.
            Clipboard.SetText("\"" + path + "\"");
            SetStatus(string.Format(LanguageManager.Tr("L_CopyPath"), path));
        }
        catch (Exception ex)
        {
            // Буфер обмена может быть занят другим процессом — это штатная ситуация Windows.
            ReportError(string.Format(LanguageManager.Tr("L_ClipboardBusy"), ex.Message));
        }
    }

    private void MenuReveal_Click(object sender, RoutedEventArgs e)
    {
        string? path = Selected?.FullPath ?? _active?.Path;
        if (path is not null) ShellActions.RevealInExplorer(path);
    }

    private void MenuTerminal_Click(object sender, RoutedEventArgs e)
    {
        string? directory = Selected is { IsDirectory: true } entry ? entry.FullPath : _active?.Path;
        if (directory is not null) ShellActions.OpenTerminal(directory);
    }

    private void MenuProperties_Click(object sender, RoutedEventArgs e)
    {
        string? path = Selected?.FullPath ?? _active?.Path;
        if (path is null) return;

        nint handle = new WindowInteropHelper(this).Handle;
        if (!ShellActions.ShowProperties(path, handle))
            SetStatus(LanguageManager.Tr("L_PropertiesError"));
    }

    // ---------- статус ----------

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
    }

    /// <summary>
    /// Сообщает об ошибке заметно: текст в статусной строке легко пропустить, поэтому
    /// добавляются подсветка адресной строки и звук.
    /// </summary>
    private void ReportError(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = ErrorBrush;

        PathBox.BorderBrush = ErrorBrush;
        PathBox.Background = ErrorBackground;

        Sounds.Play(Sounds.Error);

        // Подсветка снимается при следующей успешной навигации, а не по таймеру:
        // так пользователь видит проблему ровно до тех пор, пока её не исправил.
    }

    private void ClearError()
    {
        PathBox.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
        PathBox.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
    }

    private static readonly System.Windows.Media.Brush ErrorBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x5A, 0x4A));

    private static readonly System.Windows.Media.Brush ErrorBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x1E, 0x1A));

    private void UpdateStatus()
    {
        int directories = 0, files = 0;
        long bytes = 0;

        foreach (FileEntry entry in _entries)
        {
            if (entry.IsDirectory) directories++;
            else { files++; bytes += entry.Size; }
        }

        // Без склонений: "1 папок" резало бы глаз, а согласование ради статусной строки
        // тянет за собой правила для трёх форм.
        string text = "папок: " + directories + "   файлов: " + files + "   " + FormatSize(bytes);

        if (FileList.SelectedItems.Count > 1)
            text += "   |   выделено: " + FileList.SelectedItems.Count;
        else if (Selected is { IsDirectory: false } selected)
            text += "   |   " + selected.Name + "  " + FormatSize(selected.Size);

        SetStatus(text);
    }

    private void UpdateDiskInfo(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path) ?? "C:\\");
            if (!drive.IsReady) { DiskText.Text = string.Empty; return; }

            double used = 1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize;

            DiskText.Text = drive.Name.TrimEnd('\\') + "  " +
                            FormatSize(drive.AvailableFreeSpace) + " свободно  " +
                            RenderBar(used) + " " + (used * 100).ToString("F0") + "%";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiskText.Text = string.Empty;
        }
    }

    /// <summary>Индикатор занятости псевдографикой — в духе текстового режима.</summary>
    private static string RenderBar(double fraction, int width = 20)
    {
        int filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024L * 1024 => (bytes / 1024.0).ToString("N0") + " K",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("N1") + " M",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("N2") + " G",
    };

    private void LoadDrives()
    {
        var items = new List<DriveItem>();

        foreach (DriveInfo drive in DirectoryLoader.GetDrives())
        {
            try
            {
                double used = drive.TotalSize > 0
                    ? 1.0 - (double)drive.AvailableFreeSpace / drive.TotalSize
                    : 0;

                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.DriveType.ToString()
                    : drive.VolumeLabel;

                // Полоса в 10 символов — предел, при котором строка целиком влезает
                // в боковую панель моноширинным шрифтом и не обрезается многоточием.
                items.Add(new DriveItem(
                    Header: drive.Name.TrimEnd('\\') + "  " + label,
                    Bar: RenderBar(used, 10) + " " + FormatSize(drive.AvailableFreeSpace),
                    Path: drive.RootDirectory.FullName));
            }
            catch (IOException) { }
        }

        DriveList.ItemsSource = items;
    }

    // ---------- ввод ----------

    /// <summary>
    /// Боковые кнопки мыши. WPF отдаёт их как XButton1 (назад) и XButton2 (вперёд) —
    /// нумерация соответствует физическому порядку кнопок на мышах с пятью кнопками.
    /// </summary>
    protected override async void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        switch (e.ChangedButton)
        {
            case MouseButton.XButton1:
                e.Handled = true;
                await GoBackAsync();
                break;

            case MouseButton.XButton2:
                e.Handled = true;
                await GoForwardAsync();
                break;
        }
    }

    /// <summary>
    /// Горячие клавиши уровня окна. PreviewKeyDown, а не KeyDown: иначе список
    /// перехватит нажатие раньше и часть сочетаний сработает только вне него.
    /// </summary>
    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ctrl+Tab работает везде — и в списке, и в консоли, и в редакторе.
        if (key == Key.Tab && control && !alt)
        {
            e.Handled = true;
            await CycleRecentAsync(shift ? -1 : 1);
            return;
        }

        // Переключение фокуса между половинами: Ctrl+` (как в VS Code) и Alt+1 / Alt+2.
        if (key == Key.Oem3 && control || key == Key.D1 && alt && !control || key == Key.D2 && alt && !control)
        {
            e.Handled = true;

            if (key == Key.D1 || (key == Key.Oem3 && TerminalHasFocus)) FileList.Focus();
            else if (Editor.IsOpen) Editor.FocusEditor();
            else FocusTerminal();

            return;
        }

        if (key == Key.F11)
        {
            e.Handled = true;
            ToggleTerminalMaximized();
            return;
        }

        // В консоли и редакторе клавиши принадлежат им: Ctrl+C там — прерывание или копирование
        // текста, а не копирование файлов. Окну остаются только сочетания, не мешающие оболочке.
        if (TerminalHasFocus || EditorHasFocus)
        {
            switch (key)
            {
                case Key.T when control && shift:
                    e.Handled = true;
                    await NewTabAsync(_active?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                    break;

                case Key.W when control && shift:
                    e.Handled = true;
                    if (_active is not null) await CloseTabAsync(_active);
                    break;

                case Key.F9:
                    e.Handled = true;
                    ThemeManager.Next();
                    AppSettings.Current.ThemeIndex = ThemeManager.Current;
                    AppSettings.Current.Save();
                    break;

                case Key.F10:
                    e.Handled = true;
                    OpenSettings();
                    break;
            }

            return;
        }

        // Поле адреса — обычное текстовое поле: файловые Ctrl+C/X/V/A там не нужны.
        if (Keyboard.FocusedElement is TextBox && key is Key.C or Key.X or Key.V or Key.A && control) return;

        switch (e.Key)
        {
            case Key.F3:
                e.Handled = true;
                OpenPreviewForSelection();
                break;

            case Key.F4:
                e.Handled = true;
                OpenEditorForSelection();
                break;

            case Key.F9:
                e.Handled = true;
                ThemeManager.Next();
                AppSettings.Current.ThemeIndex = ThemeManager.Current;
                AppSettings.Current.Save();
                SetStatus(string.Format(LanguageManager.Tr("L_ThemeStatus"), ThemeManager.CurrentName));
                break;

            case Key.F10:
                e.Handled = true;
                OpenSettings();
                break;

            case Key.F12:
                e.Handled = true;
                ToggleDetails();
                break;

            case Key.F2:
                e.Handled = true;
                await RenameSelectedAsync();
                break;

            case Key.F when control:
                e.Handled = true;
                OpenSearch();
                break;

            case Key.C when control:
                e.Handled = true;
                CopySelectionToClipboard(cut: false);
                break;

            case Key.X when control:
                e.Handled = true;
                CopySelectionToClipboard(cut: true);
                break;

            case Key.V when control:
                e.Handled = true;
                await PasteFromClipboardAsync();
                break;

            case Key.A when control:
                e.Handled = true;
                FileList.SelectAll();
                break;

            // Масштаб: те же сочетания, что в браузерах. OemPlus/OemMinus — основная
            // клавиатура, Add/Subtract — цифровой блок.
            case Key.OemPlus or Key.Add when control:
                e.Handled = true;
                ChangeScale(+1);
                break;

            case Key.OemMinus or Key.Subtract when control:
                e.Handled = true;
                ChangeScale(-1);
                break;

            case Key.D0 or Key.NumPad0 when control:
                e.Handled = true;
                SetScale(14);
                break;

            case Key.D when control:
                e.Handled = true;
                AddCurrentToFavorites();
                break;

            case Key.G when control:
                e.Handled = true;
                if (_active is not null) RunBuiltinAddon(
                    new AddonManifest { Id = "folder-tree" }, _active.Path);
                break;

            case Key.T when control:
                e.Handled = true;
                await NewTabAsync(_active?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                break;

            case Key.W when control:
                e.Handled = true;
                if (_active is not null) await CloseTabAsync(_active);
                break;


            case Key.L when control:
                e.Handled = true;
                PathBox.Focus();
                PathBox.SelectAll();
                break;

            case Key.Left when Keyboard.Modifiers == ModifierKeys.Alt:
                e.Handled = true;
                await GoBackAsync();
                break;

            case Key.Right when Keyboard.Modifiers == ModifierKeys.Alt:
                e.Handled = true;
                await GoForwardAsync();
                break;
        }
    }

    // ---------- масштаб ----------

    /// <summary>
    /// Масштаб меняется размером шрифта: вся вёрстка построена на нём и на моноширинных
    /// метриках, поэтому LayoutTransform здесь был бы хуже — он размывает текст,
    /// увеличивая уже отрисованные пиксели вместо перерисовки в нужном кегле.
    /// </summary>
    private void ChangeScale(int step) => SetScale(AppSettings.Current.FontSize + step);

    private void SetScale(double size)
    {
        size = Math.Clamp(size, 6, 40);

        AppSettings.Current.FontSize = size;
        AppSettings.Current.ApplyToResources();
        AppSettings.Current.Save();

        SetStatus(string.Format(LanguageManager.Tr("L_ScaleStatus"), size.ToString("F0")));
    }

    /// <summary>Ctrl + колесо — тот же масштаб, привычный по браузерам и редакторам.</summary>
    private void FileList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        e.Handled = true;
        ChangeScale(e.Delta > 0 ? +1 : -1);
    }

    private async Task CycleTabAsync(int direction)
    {
        if (_active is null || _tabs.Count < 2) return;

        int index = _tabs.IndexOf(_active);
        int next = ((index + direction) % _tabs.Count + _tabs.Count) % _tabs.Count;

        await ActivateTabAsync(_tabs[next]);
    }

    // ---------- окно ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
