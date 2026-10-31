using System.Runtime.InteropServices;

namespace TerminalExplorer;

/// <summary>
/// Копирование, перемещение и удаление через SHFileOperation.
///
/// Своя реализация копирования потребовала бы собственного диалога прогресса, обработки
/// конфликтов имён, поддержки отмены, длинных путей и прав доступа — то есть повторения
/// того, что оболочка Windows уже делает правильно. SHFileOperation отдаёт всё это даром:
/// системный диалог с прогрессом, вопрос «заменить?», корзину при удалении.
/// </summary>
public static class FileOperations
{
    private const uint FO_MOVE = 0x0001;
    private const uint FO_COPY = 0x0002;
    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_ALLOWUNDO = 0x0040;      // удаление в Корзину, а не мимо неё
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_RENAMEONCOLLISION = 0x0008; // «имя - копия» вместо ошибки

    /// <summary>
    /// Раскладка структуры без Pack.
    ///
    /// В примерах из интернета почти всегда стоит Pack = 1 — это наследие x86, где
    /// структура действительно упакована. На x64 указатели выравниваются по 8 байт,
    /// и Pack = 1 даёт неверные смещения полей: shell32 читает мусор вместо путей,
    /// что валит процесс. Естественное выравнивание — единственный правильный вариант
    /// для 64-битной сборки.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT operation);

    /// <summary>
    /// Пути для SHFileOperation передаются одной строкой: элементы разделены нулевым
    /// символом, а весь список закрыт вторым нулём. Формат унаследован от Win16 и
    /// не имеет управляемого аналога, поэтому строим строку вручную.
    /// </summary>
    private static string PackPaths(IEnumerable<string> paths) =>
        string.Join('\0', paths) + "\0\0";

    /// <summary>
    /// Копирование. Объекты, которые копируются в собственную папку, идут отдельным вызовом
    /// с FOF_RENAMEONCOLLISION: проводник в таком случае делает «имя - копия», а без флага
    /// SHFileOperation выдаёт «Имена исходного и конечного файлов совпадают».
    /// Остальные — обычным вызовом, чтобы конфликт с чужим файлом по-прежнему спрашивал «заменить?».
    /// </summary>
    public static bool Copy(IReadOnlyList<string> sources, string destination, nint owner)
    {
        string target = destination.TrimEnd('\\');

        bool InPlace(string path) => string.Equals(
            System.IO.Path.GetDirectoryName(path.TrimEnd('\\')), target, StringComparison.OrdinalIgnoreCase);

        string[] duplicates = [.. sources.Where(InPlace)];
        string[] others = [.. sources.Where(path => !InPlace(path))];

        bool done = true;

        if (others.Length > 0) done &= Run(FO_COPY, others, destination, owner);
        if (duplicates.Length > 0) done &= Run(FO_COPY, duplicates, destination, owner, FOF_RENAMEONCOLLISION);

        return done;
    }

    public static bool Move(IReadOnlyList<string> sources, string destination, nint owner) =>
        Run(FO_MOVE, sources, destination, owner);

    /// <summary>Удаляет в Корзину: без FOF_ALLOWUNDO файлы стирались бы безвозвратно.</summary>
    public static bool DeleteToRecycleBin(IReadOnlyList<string> sources, nint owner) =>
        Run(FO_DELETE, sources, destination: null, owner);

    private static bool Run(uint function, IReadOnlyList<string> sources, string? destination, nint owner,
        ushort extraFlags = 0)
    {
        if (sources.Count == 0) return false;

        var operation = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = function,
            pFrom = PackPaths(sources),
            pTo = destination is null ? null : destination + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | extraFlags),
        };

        int result = SHFileOperationW(ref operation);

        // Ненулевой код — ошибка, отдельный флаг — отмена пользователем.
        return result == 0 && !operation.fAnyOperationsAborted;
    }
}
