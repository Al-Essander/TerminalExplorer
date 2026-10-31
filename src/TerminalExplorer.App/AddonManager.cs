using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalExplorer;

/// <summary>Куда аддон попадает в интерфейсе.</summary>
public enum AddonPlacement
{
    /// <summary>Только в главное меню.</summary>
    Main,

    /// <summary>Только в контекстное меню списка файлов.</summary>
    Context,

    /// <summary>И туда, и туда.</summary>
    Both,
}

/// <summary>Что аддон умеет запускать.</summary>
public enum AddonKind
{
    /// <summary>Внешняя программа или скрипт из папки аддона.</summary>
    Executable,

    /// <summary>Функция, встроенная в проводник. Опознаётся по полю Id.</summary>
    Builtin,
}

/// <summary>
/// Описание аддона — содержимое файла addon.json.
///
/// Формат намеренно плоский и без вложенности: манифест пишут руками, и чем меньше
/// в нём структуры, тем меньше поводов ошибиться.
/// </summary>
public sealed class AddonManifest
{
    /// <summary>Идентификатор, уникальный среди аддонов. Для встроенных — имя функции.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";

    public AddonKind Kind { get; set; } = AddonKind.Executable;
    public AddonPlacement Placement { get; set; } = AddonPlacement.Both;

    /// <summary>
    /// Запускаемый файл относительно папки аддона: .exe, .cmd, .bat или .ps1.
    /// Для встроенных аддонов не используется.
    /// </summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>
    /// Аргументы командной строки. Поддерживаются подстановки:
    ///   {folder}    — текущая папка проводника
    ///   {selection} — путь выделенного объекта (пусто, если ничего не выделено)
    ///   {addon}     — папка самого аддона
    /// </summary>
    public string Arguments { get; set; } = "\"{folder}\"";

    /// <summary>Требовать ли открытую папку. Почти всегда да — аддоны работают с ФС.</summary>
    public bool RequiresFolder { get; set; } = true;

    /// <summary>Запускать без окна консоли — для скриптов, которым нечего показывать.</summary>
    public bool Hidden { get; set; }

    /// <summary>Показывать ли аддон на боковой панели, а не только в меню.</summary>
    public bool Pinned { get; set; }

    /// <summary>Путь к папке аддона. Заполняется загрузчиком, в файле его нет.</summary>
    [JsonIgnore]
    public string Directory { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsBuiltin => Kind == AddonKind.Builtin;
}

/// <summary>
/// Поиск и запуск аддонов.
///
/// Каждый аддон — отдельная папка в %APPDATA%\TerminalExplorer\Addons с файлом addon.json.
/// Такая раскладка выбрана осознанно: аддон переносится копированием папки, удаляется
/// удалением папки, и ничего не регистрируется в общих списках, которые пришлось бы
/// чинить после ручного вмешательства.
/// </summary>
public static class AddonManager
{
    public const string ManifestName = "addon.json";

    /// <summary>
    /// Аддоны, поставляемые с программой. Лежат рядом с exe, входят в состав проекта
    /// и видны в репозитории.
    /// </summary>
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "Addons");

    /// <summary>
    /// Аддоны, установленные пользователем. Отдельная папка нужна затем, чтобы обновление
    /// программы не затирало чужие аддоны, а переустановка не требовала их искать заново.
    /// </summary>
    public static string UserDirectoryPath => Path.Combine(SessionStore.DirectoryPath, "Addons");

    public static List<AddonManifest> Addons { get; } = [];

    /// <summary>Закреплённые — те, что выводятся на боковую панель.</summary>
    public static IEnumerable<AddonManifest> Pinned => Addons.Where(addon => addon.Pinned);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Перечитывает обе папки аддонов. Ошибка в одном манифесте не мешает остальным:
    /// сломанный аддон пропускается молча, чтобы не блокировать работу проводника.
    /// </summary>
    public static void Reload()
    {
        Addons.Clear();

        LoadFrom(DirectoryPath);
        LoadFrom(UserDirectoryPath);
    }

    private static void LoadFrom(string root)
    {
        try
        {
            if (!System.IO.Directory.Exists(root)) return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (string folder in System.IO.Directory.EnumerateDirectories(root))
        {
            string manifestPath = Path.Combine(folder, ManifestName);
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<AddonManifest>(File.ReadAllText(manifestPath), Options);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name)) continue;

                // Аддон с уже занятым id пропускаем: пользовательский не должен
                // молча подменять поставляемый с программой.
                if (Addons.Any(existing => string.Equals(existing.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;

                manifest.Directory = folder;
                Addons.Add(manifest);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Подставляет в строку аргументов текущий контекст проводника.</summary>
    public static string ExpandArguments(AddonManifest addon, string folder, string? selection) =>
        addon.Arguments
            .Replace("{folder}", folder)
            .Replace("{selection}", selection ?? string.Empty)
            .Replace("{addon}", addon.Directory);

    /// <summary>
    /// Запускает внешний аддон. Возвращает текст ошибки или null при успехе —
    /// вызывающий сам решает, как о ней сообщить.
    /// </summary>
    public static string? Run(AddonManifest addon, string folder, string? selection)
    {
        if (addon.IsBuiltin) return LanguageManager.Tr("L_BuiltinAddon");

        string entry = Path.Combine(addon.Directory, addon.Entry);
        if (!File.Exists(entry)) return string.Format(LanguageManager.Tr("L_AddonEntryNotFound"), addon.Entry);

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = entry,
                Arguments = ExpandArguments(addon, folder, selection),

                // Рабочая папка — текущая папка проводника, а не папка аддона:
                // скрипты почти всегда работают с тем, что открыто у пользователя.
                WorkingDirectory = folder,
                UseShellExecute = true,
                WindowStyle = addon.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            };

            Process.Start(start);
            return null;
        }
        catch (Exception ex)
        {
            return string.Format(LanguageManager.Tr("L_AddonRunError"), ex.Message);
        }
    }
}
