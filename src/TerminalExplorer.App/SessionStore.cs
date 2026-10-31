using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalExplorer;

/// <summary>
/// Сохранение и восстановление сессии (п. 3.1 паспорта).
///
/// Формат — JSON через System.Text.Json, входящий в состав .NET: сторонних сериализаторов
/// проект не использует. Файл кладётся в %APPDATA%\TerminalExplorer\session.json.
/// </summary>
public static class SessionStore
{
    public sealed class TabRecord
    {
        public string Path { get; set; } = string.Empty;
        public SortColumn Sort { get; set; }
        public bool SortDescending { get; set; }

        /// <summary>История вкладки, чтобы «назад» работал и после перезапуска.</summary>
        public List<string> History { get; set; } = [];
        public int HistoryPosition { get; set; } = -1;

        /// <summary>Хвост текста консоли вкладки — процессы не переживают перезапуск, а вот вывод можно показать.</summary>
        public List<string> ConsoleLines { get; set; } = [];
    }

    public sealed class Session
    {
        public List<TabRecord> Tabs { get; set; } = [];
        public int ActiveIndex { get; set; }

        /// <summary>Порядок вкладок по давности использования (индексы, самая свежая первой) — для Ctrl+Tab.</summary>
        public List<int> RecentOrder { get; set; } = [];
        public bool TerminalMaximized { get; set; }
        public int ThemeIndex { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public bool Maximized { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? _resolvedDirectory;

    /// <summary>
    /// Где хранятся настройки, сессия, избранное и шаблоны.
    ///
    /// По умолчанию — папка Data рядом с исполняемым файлом: программа переносится
    /// вместе со своими данными, и не приходится искать их в скрытом AppData.
    ///
    /// Откат на %APPDATA% нужен для случая, когда программа установлена в место,
    /// куда обычному пользователю писать нельзя (Program Files): там portable-режим
    /// молча терял бы все настройки.
    /// </summary>
    public static string DirectoryPath => _resolvedDirectory ??= ResolveDirectory();

    private static string ResolveDirectory()
    {
        string portable = System.IO.Path.Combine(AppContext.BaseDirectory, "Data");

        if (IsWritable(portable)) return portable;

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TerminalExplorer");
    }

    /// <summary>Проверяет право записи делом, а не разбором прав доступа.</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string probe = System.IO.Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Переносит данные из %APPDATA% в папку рядом с программой — один раз, при первом
    /// запуске после перехода на portable-хранение. Старые файлы не удаляются:
    /// откат на предыдущую версию не должен терять настройки.
    /// </summary>
    public static void MigrateFromAppData()
    {
        string legacy = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TerminalExplorer");

        if (string.Equals(legacy, DirectoryPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(legacy) || File.Exists(FilePath)) return;

        try
        {
            Directory.CreateDirectory(DirectoryPath);

            foreach (string file in Directory.EnumerateFiles(legacy))
                File.Copy(file, System.IO.Path.Combine(DirectoryPath, System.IO.Path.GetFileName(file)), overwrite: false);

            foreach (string folder in Directory.EnumerateDirectories(legacy))
            {
                string target = System.IO.Path.Combine(DirectoryPath, System.IO.Path.GetFileName(folder));
                if (Directory.Exists(target)) continue;

                CopyDirectory(folder, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)), overwrite: false);

        foreach (string folder in Directory.EnumerateDirectories(source))
            CopyDirectory(folder, System.IO.Path.Combine(target, System.IO.Path.GetFileName(folder)));
    }

    public static string FilePath => System.IO.Path.Combine(DirectoryPath, "session.json");

    /// <summary>
    /// Запись через временный файл с последующей заменой: если процесс умрёт в момент
    /// сохранения, старая сессия останется целой, а не превратится в обрезанный JSON.
    /// </summary>
    public static void Save(Session session)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(session, Options));

            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Потеря сессии не повод мешать пользователю закрыть программу.
        }
    }

    public static Session Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Session();

            return JsonSerializer.Deserialize<Session>(File.ReadAllText(FilePath), Options)
                   ?? new Session();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый файл сессии не должен мешать запуску — начинаем с чистого листа.
            return new Session();
        }
    }
}
