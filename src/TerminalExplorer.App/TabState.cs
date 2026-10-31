using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace TerminalExplorer;

/// <summary>
/// Состояние одной вкладки.
///
/// История навигации у каждой вкладки своя — это принципиально: в браузере и в проводнике
/// Windows 11 «назад» действует в пределах вкладки, а не глобально, и любое другое
/// поведение воспринимается как ошибка.
///
/// INotifyPropertyChanged здесь нужен (в отличие от FileEntry): заголовок вкладки меняется
/// при навигации, и панель вкладок должна это отражать без пересоздания элементов.
/// </summary>
public sealed class TabState : INotifyPropertyChanged
{
    private string _path = string.Empty;
    private bool _isActive;

    public string Path
    {
        get => _path;
        set
        {
            if (_path == value) return;

            _path = value;
            Notify();
            Notify(nameof(Title));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive != value) { _isActive = value; Notify(); } }
    }

    /// <summary>Подпись вкладки: имя папки, а для корня диска — сама буква.</summary>
    public string Title
    {
        get
        {
            if (string.IsNullOrEmpty(_path)) return "новая";

            string name = System.IO.Path.GetFileName(_path.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? _path.TrimEnd('\\') : name;
        }
    }

    // --- встроенный терминал вкладки (создаётся при первом показе) ---
    public TerminalExplorer.Terminal.TerminalSession? Session { get; set; }
    public TerminalExplorer.Terminal.TerminalView? TermView { get; set; }

    /// <summary>Текст прошлого сеанса консоли: показывается над новым приглашением после перезапуска программы.</summary>
    public List<string> SavedLines { get; set; } = [];

    /// <summary>Папка, на которой вкладка в последний раз проиграла звук перехода. Нужна для направления «вошёл / вышел».</summary>
    public string? SoundPath { get; set; }

    public SortColumn Sort { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }

    /// <summary>История посещённых путей этой вкладки и позиция в ней.</summary>
    public List<string> History { get; init; } = [];
    public int HistoryPosition { get; set; } = -1;

    public bool CanGoBack => HistoryPosition > 0;
    public bool CanGoForward => HistoryPosition >= 0 && HistoryPosition < History.Count - 1;

    public bool CanGoUp => !string.IsNullOrEmpty(_path) && Directory.GetParent(_path) is not null;

    /// <summary>
    /// Записывает переход в историю. Переход из середины истории обрезает всё, что было
    /// впереди, — как в браузере.
    /// </summary>
    public void RecordHistory(string path)
    {
        if (HistoryPosition >= 0 && HistoryPosition < History.Count - 1)
            History.RemoveRange(HistoryPosition + 1, History.Count - HistoryPosition - 1);

        if (History.Count == 0 || !string.Equals(History[^1], path, StringComparison.OrdinalIgnoreCase))
            History.Add(path);

        HistoryPosition = History.Count - 1;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
