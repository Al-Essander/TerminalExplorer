using System.IO;

namespace TerminalExplorer;

/// <summary>Как сортировать список.</summary>
public enum SortColumn { Name, Size, Modified, Type }

/// <summary>
/// Чтение содержимого папки.
///
/// Используется EnumerateFileSystemInfos, а не GetFiles + GetDirectories: под капотом это
/// FindFirstFile/FindNextFile, который возвращает размер, время и атрибуты прямо в структуре
/// поиска. Отдельных обращений к диску за метаданными не происходит — иначе на папке
/// в 100 000 файлов мы получили бы 100 000 лишних системных вызовов.
/// </summary>
public static class DirectoryLoader
{
    private static readonly EnumerationOptions Options = new()
    {
        // Ошибки доступа к отдельным подпапкам не должны рушить чтение всей папки.
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,          // показываем скрытые и системные: это файловый менеджер
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Читает папку в фоновом потоке. Возвращает готовый список — UI-поток не должен
    /// заниматься ни перечислением, ни сортировкой.
    /// </summary>
    public static Task<IReadOnlyList<FileEntry>> LoadAsync(
        string path, SortColumn sort, bool descending, CancellationToken cancellation = default)
    {
        return Task.Run<IReadOnlyList<FileEntry>>(() =>
        {
            var entries = new List<FileEntry>(1024);
            var directory = new DirectoryInfo(path);

            foreach (FileSystemInfo info in directory.EnumerateFileSystemInfos("*", Options))
            {
                cancellation.ThrowIfCancellationRequested();

                bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

                entries.Add(new FileEntry
                {
                    Name = info.Name,
                    FullPath = info.FullName,
                    IsDirectory = isDirectory,
                    Size = isDirectory ? 0 : ((FileInfo)info).Length,
                    Modified = info.LastWriteTime,
                    Attributes = info.Attributes,
                });
            }

            Sort(entries, sort, descending);
            return entries;
        }, cancellation);
    }

    /// <summary>
    /// Сортировка. Папки всегда идут выше файлов независимо от направления — так ведут себя
    /// все файловые менеджеры, и обратный порядок здесь воспринимался бы как ошибка.
    /// </summary>
    public static void Sort(List<FileEntry> entries, SortColumn column, bool descending)
    {
        Comparison<FileEntry> comparison = column switch
        {
            SortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            SortColumn.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
            SortColumn.Type => (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };

        entries.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            int result = comparison(a, b);
            if (result == 0 && column != SortColumn.Name)
                result = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

            return descending ? -result : result;
        });
    }

    /// <summary>Список корневых узлов для боковой панели: готовые к работе диски.</summary>
    public static IReadOnlyList<DriveInfo> GetDrives()
    {
        var drives = new List<DriveInfo>();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            // IsReady у пустого привода бросает исключение при обращении к остальным полям.
            try
            {
                if (drive.IsReady) drives.Add(drive);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return drives;
    }
}
