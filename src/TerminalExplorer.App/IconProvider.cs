using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TerminalExplorer;

/// <summary>
/// Иконки файлов из системного набора Windows.
///
/// Ключевой приём — SHGFI_USEFILEATTRIBUTES: с ним SHGetFileInfo не трогает диск вообще,
/// а выдаёт иконку по расширению из реестра ассоциаций. На папке в 100 000 файлов
/// обращение к диску за иконкой каждого файла стоило бы дороже, чем само перечисление.
///
/// Кэш — по расширению, а не по файлу: все .txt делят одну иконку. Исключение сделано
/// для типов, у которых иконка своя у каждого файла (.exe, .lnk, .ico): для них
/// запрашивается настоящая, с обращением к файлу.
/// </summary>
public static class IconProvider
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfoW(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    /// <summary>Расширения, у которых иконка индивидуальна для каждого файла.</summary>
    private static readonly HashSet<string> PerFileTypes =
        new(StringComparer.OrdinalIgnoreCase) { "exe", "lnk", "ico", "cur", "msi", "scr", "url" };

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Ограничение кэша: расширений на диске бывают тысячи, а каждая иконка держит
    /// растр. Без потолка кэш растёт бесконечно (раздел 10 паспорта — утечка на иконках).
    /// </summary>
    private const int CacheLimit = 512;

    public static ImageSource? Get(FileEntry entry)
    {
        string key = entry.IsDirectory
            ? "\\dir"
            : PerFileTypes.Contains(entry.Extension)
                ? entry.FullPath
                : "." + entry.Extension;

        lock (Cache)
        {
            if (Cache.TryGetValue(key, out ImageSource? cached)) return cached;
        }

        ImageSource? icon = Load(entry);

        lock (Cache)
        {
            // Простейшая стратегия вытеснения: при переполнении кэш сбрасывается целиком.
            // Для LRU нужен порядок обращений, а выигрыш здесь неощутим — промах стоит
            // один вызов SHGetFileInfo.
            if (Cache.Count >= CacheLimit) Cache.Clear();

            Cache[key] = icon;
        }

        return icon;
    }

    private static ImageSource? Load(FileEntry entry)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_SMALLICON;

        string path;
        uint attributes;

        if (entry.IsDirectory)
        {
            path = "folder";
            attributes = FILE_ATTRIBUTE_DIRECTORY;
            flags |= SHGFI_USEFILEATTRIBUTES;
        }
        else if (PerFileTypes.Contains(entry.Extension) && File.Exists(entry.FullPath))
        {
            // Настоящая иконка исполняемого файла или ярлыка — здесь обращение к диску оправдано.
            path = entry.FullPath;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }
        else
        {
            path = "file." + entry.Extension;
            attributes = FILE_ATTRIBUTE_NORMAL;
            flags |= SHGFI_USEFILEATTRIBUTES;
        }

        nint result = SHGetFileInfoW(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == 0 || info.hIcon == 0) return null;

        try
        {
            ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Freeze делает объект потокобезопасным и снимает нагрузку на UI-поток
            // при повторной отрисовке — для иконки, живущей в кэше, это обязательно.
            source.Freeze();
            return source;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            // HICON — неуправляемый ресурс: без DestroyIcon получаем утечку хендлов,
            // которая на большом дереве папок упирается в лимит GDI-объектов процесса.
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Иконка для произвольного пути — нужна боковой панели и панели сведений.</summary>
    public static ImageSource? GetForPath(string path, bool isDirectory) =>
        Get(new FileEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = isDirectory,
            Size = 0,
            Modified = default,
            Attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
        });

    public static void Clear()
    {
        lock (Cache) Cache.Clear();
    }
}
