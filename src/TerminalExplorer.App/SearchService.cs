using System.Collections.Generic;
using System.IO;
using TerminalExplorer.Core;

namespace TerminalExplorer;

/// <summary>Один результат поиска: имя и полный путь.</summary>
public sealed record SearchResult(string Name, string FullPath, bool IsDirectory);

/// <summary>Область поиска.</summary>
public enum SearchScope
{
    /// <summary>Текущая папка и её подпапки; не требует прав администратора.</summary>
    Folder,
    /// <summary>Все NTFS-тома через MFT; требует прав администратора.</summary>
    AllDrives,
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
    /// Ищет подстроку в имени файла/папки в пределах области.
    /// Запрашивает только имя и путь, размер и время не трогает — при миллионах записей
    /// лишние обращения к метаданным стоят дорого.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, SearchScope scope, string rootFolder,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

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