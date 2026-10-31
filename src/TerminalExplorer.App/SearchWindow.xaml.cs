using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TerminalExplorer;

/// <summary>
/// Окно поиска: слева — результаты, справа — панель, показывающая, как обрабатывается запрос.
///
/// Запрос ищется с задержкой (debounce) и с отменой устаревшего поиска: быстрый ввод
/// не должен держать диск занятым запросом, который пользователь уже перепечатал.
/// </summary>
public partial class SearchWindow : Window
{
    private readonly string _rootFolder;
    private readonly ObservableCollection<SearchResult> _results = [];
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts;

    public SearchWindow(string rootFolder)
    {
        InitializeComponent();

        _rootFolder = rootFolder;

        // Стартуем с локального поиска: он не требует прав администратора и работает сразу.
        ScopeBox.SelectedIndex = 0;
        // Поиск по имени по умолчанию — по коду переключается явно.
        ModeBox.SelectedIndex = 0;

        ModeBox.SelectionChanged += ModeBox_Changed;

        ResultList.ItemsSource = _results;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += Debounce_Tick;

        QueryBox.Focus();
    }

    private SearchScope CurrentScope =>
        ScopeBox.SelectedIndex == 1 ? SearchScope.AllDrives : SearchScope.Folder;

    private SearchMode CurrentMode =>
        ModeBox.SelectedIndex == 1 ? SearchMode.Content : SearchMode.Name;

    private void QueryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Перезапускаем таймер: искомый запрос выполнится только после паузы в наборе.
        _debounce.Stop();
        _searchCts?.Cancel();

        if (string.IsNullOrWhiteSpace(QueryBox.Text))
        {
            _results.Clear();
            StatusText.Text = LanguageManager.Tr("L_TypeQuery");
            Bloom.Reset();
            return;
        }

        _debounce.Start();
    }

    private void Debounce_Tick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _ = RunSearchAsync(QueryBox.Text, CurrentScope);
    }

    private void ScopeBox_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        _debounce.Stop();
        _ = RunSearchAsync(QueryBox.Text, CurrentScope);
    }

    private void ModeBox_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        // По коду ищем только в текущей папке: MFT-индекс хранит имена, не содержимое.
        if (CurrentMode == SearchMode.Content && CurrentScope == SearchScope.AllDrives)
            ScopeBox.SelectedIndex = 0;

        _debounce.Stop();
        _ = RunSearchAsync(QueryBox.Text, CurrentScope);
    }

    private async Task RunSearchAsync(string query, SearchScope scope)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        StatusText.Text = LanguageManager.Tr(
            scope == SearchScope.AllDrives ? "L_SearchingDrives" : "L_SearchingFolder");

        try
        {
            IReadOnlyList<SearchResult> found = await SearchService.SearchAsync(
                query, scope, CurrentMode, _rootFolder, cts.Token);

            if (cts.IsCancellationRequested) return;

            _results.Clear();
            foreach (SearchResult result in found.Take(1000))
                _results.Add(result);

            StatusText.Text = LanguageManager.Tr("L_Found") + ": " + found.Count;
            Bloom.ShowForQuery(query, scope, found.Count);
        }
        catch (OperationCanceledException)
        {
            // Запрос устарел — отменён более свежим вводом. Ничего не делаем.
        }
        catch (Exception ex)
        {
            StatusText.Text = LanguageManager.Tr("L_SearchError") + ": " + ex.Message;
        }
        finally
        {
            // Таймер может быть уже перезапущен — не трогаем его.
        }
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Close();
    }

    // ---------- окно ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        base.OnClosed(e);
    }
}