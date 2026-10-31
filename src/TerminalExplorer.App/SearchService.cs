using System.Collections.Generic;
using System.IO;
using TerminalExplorer.Core;

namespace TerminalExplorer;

/// <summary>Один результат поиска: имя и полный путь.</summary>
public sealed record SearchResult(
    string Name,
    string FullPath,
    bool IsDirectory,
    int Line = -1,
    string? Snippet = null);

/// <summary>Область поиска.</summary>
public enum SearchScope
{
    /// <summary>Текущая папка и её подпапки; не требует прав администратора.</summary>
    Folder,
    /// <summary>Все NTFS-тома через MFT; требует прав администратора.</summary>
    AllDrives,
}

/// <summary>Что ищем: имя файла/папки или содержимое файлов.</summary>
public enum SearchMode
{
    /// <summary>Подстрока в имени.</summary>
    Name,
    /// <summary>Подстрока в тексте файла (по коду).</summary>
    Content,
}

/// <summary>
/// Логика поиска. Разделена на два пути:
///   - по текущей папке — обычный рекурсивный обход (EnumerateFiles), работает без прав;
///   - по всем томам — через MftIndex из общей библиотеки, с обработкой прав администратора.
///
/// Оба пути асинхронные и отменяемые: быстрый ввод не должен держать диск занятым
/// устаревшим запросом (см. CancellationToken).
/// </summary>
public static class SearchService
{
    /// <summary>
    /// Ищет подстроку в имени файла/папки или в содержимом файлов в пределах области.
    /// Запрашивает только имя и путь, размер и время не трогает — при миллионах записей
    /// лишние обращения к метаданным стоят дорого.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, SearchScope scope, SearchMode mode, string rootFolder,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        if (mode == SearchMode.Content)
            return await SearchContentAsync(rootFolder, query, cancellation);

        return scope switch
        {
            SearchScope.AllDrives => await SearchAllDrivesAsync(query, cancellation),
            _ => await SearchFolderAsync(rootFolder, query, cancellation),
        };
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchFolderAsync(
        string root, string query, CancellationToken cancellation)
    {
        if (!Directory.Exists(root)) return [];

        var results = new List<SearchResult>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        await Task.Run(() =>
        {
            // Обходим и файлы, и папки: в файловом менеджере ищется и то и другое.
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();

                string name = Path.GetFileName(path);
                if (name.Length == 0) continue; // корневой элемент самого root
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                bool isDirectory = Directory.Exists(path);
                results.Add(new SearchResult(name, path, isDirectory));
            }
        }, cancellation);

        return results;
    }

    /// <summary>
    /// Поиск подстроки в содержимом файлов под root. Ограничен текстовыми и кодовыми
    /// расширениями (как предпросмотр), бинарники пропускаются. Возвращает файлы и строку
    /// первого совпадения, чтобы результат был полезен (поиск по коду).
    /// </summary>
    private static async Task<IReadOnlyList<SearchResult>> SearchContentAsync(
        string root, string query, CancellationToken cancellation)
    {
        if (!Directory.Exists(root)) return [];

        var results = new List<SearchResult>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        // Размер ограничиваем — читать 6-гигабайтный файл не нужно.
        const long MaxBytes = 4 * 1024 * 1024;

        await Task.Run(() =>
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();

                string ext = Path.GetExtension(path);
                if (!IsSearchableText(ext)) continue;

                try
                {
                    var info = new FileInfo(path);
                    if (info.Length > MaxBytes) continue;

                    // Binary detection: нулевой байт почти наверняка значит «не текст».
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.Contains((byte)0)) continue;

                    using var reader = new StreamReader(
                        new MemoryStream(bytes),
                        System.Text.Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true);
                    string text = reader.ReadToEnd();

                    int index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) continue;

                    int line = 1 + text.AsSpan(0, index).Count('\n');
                    results.Add(new SearchResult(
                        Path.GetFileName(path),
                        path,
                        IsDirectory: false,
                        Line: line,
                        Snippet: MakeSnippet(text, index)));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }, cancellation);

        return results;
    }

    /// <summary>Текстовые расширения, которые имеет смысл искать по содержимому.</summary>
    private static bool IsSearchableText(string extension) => extension is
        ".txt" or ".md" or ".markdown" or ".log" or ".ini" or ".cfg" or ".conf" or ".json" or ".xml"
        or ".yaml" or ".yml" or ".csv" or ".tsv" or ".html" or ".htm" or ".css" or ".js" or ".ts"
        or ".py" or ".c" or ".cpp" or ".h" or ".hpp" or ".java" or ".sh" or ".bat" or ".cmd"
        or ".ps1" or ".sql";

    /// <summary>Вырезает небольшой фрагмент вокруг совпадения для отображения в результате.</summary>
    private static string MakeSnippet(string text, int index)
    {
        const int radius = 60;
        int start = Math.Max(0, index - radius);
        int length = Math.Min(text.Length - start, radius * 2);

        string snippet = text.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ');
        return (start > 0 ? "…" : "") + snippet.Trim();
    }

    private static async Task<IReadOnlyList<SearchResult>> SearchAllDrivesAsync(
        string query, CancellationToken cancellation)
    {
        var results = new List<SearchResult>();

        await Task.Run(() =>
        {
            foreach (char drive in DriveLetters.GetReadyDriveLetters())
            {
                cancellation.ThrowIfCancellationRequested();

                MftIndex index;
                try
                {
                    index = new MftIndex(drive);
                    index.Build();
                }
                catch (InvalidOperationException)
                {
                    // Том не удалось открыть (нет прав администратора или это не NTFS).
                    // Пропускаем и продолжаем по остальным.
                    continue;
                }

                List<int> hits;
                try
                {
                    hits = index.Search(query);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                foreach (int hit in hits)
                {
                    if (results.Count >= 10000) return;

                    cancellation.ThrowIfCancellationRequested();
                    string path;
                    try
                    {
                        path = index.GetFullPath(hit);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }

                    results.Add(new SearchResult(Path.GetFileName(path), path, index.IsDirectoryAt(hit)));
                }
            }
        }, cancellation);

        return results;
    }
}

/// <summary>Вспомогательный доступ к готовым к работе дискам.</summary>
internal static class DriveLetters
{
    public static IEnumerable<char> GetReadyDriveLetters()
    {
        var letters = new List<char>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    letters.Add(drive.Name[0]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return letters;
    }
}