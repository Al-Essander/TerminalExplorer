using System.IO;
using System.Text.Json;
using System.Windows;

namespace TerminalExplorer;

/// <summary>
/// Настройки приложения (п. 3.5 паспорта).
///
/// Живут отдельно от сессии: сессия — это «что было открыто», настройки — «как выглядит
/// и ведёт себя программа». Разные файлы, чтобы сброс одного не задевал другое.
/// </summary>
public sealed class AppSettings
{
    // --- внешний вид ---
    public int ThemeIndex { get; set; }

    /// <summary>Версия нумерации тем: 2 — после добавления «MS-DOS Far». Нужна, чтобы перенести старый выбор.</summary>
    public int ThemeLayout { get; set; }
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Consolas, Courier New";

    /// <summary>Пользовательская палитра: имя ресурса -> цвет в формате #AARRGGBB.</summary>
    public Dictionary<string, string> CustomColors { get; set; } = new()
    {
        ["Background"] = "#FF1E1E1E",
        ["Surface"] = "#FF252526",
        ["Foreground"] = "#FFD4D4D4",
        ["Dim"] = "#FF808080",
        ["Selection"] = "#FF264F78",
        ["Accent"] = "#FF569CD6",
        ["Border"] = "#FF3F3F46",
        ["Hover"] = "#FF2A2D2E",
        ["BevelLight"] = "#FF4A4A50",
        ["BevelDark"] = "#FF141416",
    };

    // --- вкладки ---
    public double TabHeight { get; set; } = 26;
    public double TabMaxWidth { get; set; } = 150;
    public double TabPaddingHorizontal { get; set; } = 10;

    // --- поведение ---
    public bool OpenFoldersInNewTab { get; set; }
    public bool ShowHiddenFiles { get; set; } = true;

    // --- терминал ---
    /// <summary>Путь или имя оболочки; пусто — PowerShell 7, затем Windows PowerShell, затем cmd.</summary>
    public string TerminalShell { get; set; } = string.Empty;

    /// <summary>Терминал и панель файлов следуют за папкой друг друга.</summary>
    public bool TerminalFollowsPanel { get; set; } = true;

    // --- звук ---
    public bool SoundEnabled { get; set; } = true;

    private static string DirectoryPath => SessionStore.DirectoryPath;

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённые настройки не должны мешать запуску.
            Current = new AppSettings();
        }

        // Нумерация тем менялась: «Классика» была 2, «Пользовательская» — 3. Переносим выбор один раз.
        if (Current.ThemeLayout < 2)
        {
            Current.ThemeIndex = Current.ThemeIndex switch { 2 => 3, 3 => 4, var other => other };
            Current.ThemeLayout = 2;
        }

        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Переносит настройки в ресурсы приложения.
    ///
    /// Значения кладутся прямо в Application.Resources, а не в объединённые словари:
    /// собственные ресурсы имеют приоритет над MergedDictionaries, поэтому так они
    /// перекрывают значения темы, а DynamicResource подхватывает изменение немедленно.
    /// </summary>
    public void ApplyToResources()
    {
        ResourceDictionary resources = Application.Current.Resources;

        resources["BaseFontSize"] = FontSize;
        resources["MonoFont"] = new System.Windows.Media.FontFamily(FontFamily);
        resources["TabHeight"] = TabHeight;
        resources["TabMaxWidth"] = TabMaxWidth;
        resources["TabPadding"] = new Thickness(TabPaddingHorizontal, 0, TabPaddingHorizontal - 4, 0);
    }
}
