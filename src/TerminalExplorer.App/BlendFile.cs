using System.IO;
using System.Text;

namespace TerminalExplorer;

/// <summary>
/// Чтение метаданных файла .blend.
///
/// Формат разобран по реальным файлам, а не по документации, поэтому здесь описано,
/// что именно наблюдалось.
///
/// Заголовок бывает двух видов:
///   старый (Blender до 3.x):  BLENDER + указатель('_'=4, '-'=8) + порядок('v','V') + версия(3 цифры)
///   новый  (Blender 4.x+):    BLENDER + длина заголовка(2 цифры) + указатель + формат(2 цифры)
///                             + порядок + версия(4 цифры), например BLENDER17-01v0502 => 5.02
///
/// Дальше идут блоки: код из 4 символов, размер, старый адрес, индекс структуры в DNA
/// и число элементов. Двухбуквенные коды (OB, ME, MA) — это датаблоки Blender, по ним
/// и считается содержимое сцены. Четырёхбуквенные (REND, GLOB, DNA1, ENDB, TEST) —
/// служебные.
///
/// Сжатые файлы (zstd, а раньше gzip) целиком не читаются: zstd в .NET не входит,
/// а тянуть ради этого стороннюю библиотеку — против принципа «ноль зависимостей».
/// Но версия достаётся и у них: заголовок попадает в несжатый литеральный блок
/// в начале потока, поэтому сигнатуру BLENDER видно первыми же байтами.
/// </summary>
public sealed class BlendFile
{
    public enum Compression { None, Zstd, Gzip }

    public bool IsValid { get; private set; }
    public Compression Packing { get; private set; }

    /// <summary>Версия в человеческом виде, например «5.2» или «2.93».</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>Разрядность указателей: 4 или 8 байт.</summary>
    public int PointerSize { get; private set; }

    public bool LittleEndian { get; private set; } = true;

    /// <summary>Есть ли встроенное превью (блок TEST).</summary>
    public bool HasThumbnail { get; private set; }

    public int ThumbnailWidth { get; private set; }
    public int ThumbnailHeight { get; private set; }

    /// <summary>Сырые пиксели превью в формате RGBA, если оно есть.</summary>
    public byte[]? ThumbnailPixels { get; private set; }

    /// <summary>Сколько датаблоков какого кода нашлось: OB, ME, MA и так далее.</summary>
    public Dictionary<string, int> Datablocks { get; } = new(StringComparer.Ordinal);

    /// <summary>Прочитаны ли блоки. У сжатых файлов доступен только заголовок.</summary>
    public bool BlocksParsed { get; private set; }

    /// <summary>Понятные названия кодов датаблоков — показываются пользователю.</summary>
    public static readonly Dictionary<string, string> DatablockNames = new(StringComparer.Ordinal)
    {
        ["OB"] = "Объекты",
        ["ME"] = "Меши",
        ["MA"] = "Материалы",
        ["TE"] = "Текстуры",
        ["IM"] = "Изображения",
        ["SC"] = "Сцены",
        ["CA"] = "Камеры",
        ["LA"] = "Источники света",
        ["AC"] = "Действия",
        ["AR"] = "Арматуры",
        ["CU"] = "Кривые",
        ["GR"] = "Коллекции",
        ["WO"] = "Мир",
        ["NT"] = "Ноды",
        ["BR"] = "Кисти",
        ["TX"] = "Тексты",
        ["SO"] = "Звуки",
        ["PA"] = "Системы частиц",
        ["KE"] = "Ключи формы",
        ["LS"] = "Стили линий",
        ["MB"] = "Меташары",
        ["LT"] = "Решётки",
    };

    private const int MaxHeaderProbe = 64;

    public static BlendFile? Read(string path)
    {
        var result = new BlendFile();

        try
        {
            using FileStream stream = File.OpenRead(path);

            Span<byte> probe = stackalloc byte[MaxHeaderProbe];
            int read = stream.Read(probe);
            if (read < 20) return null;

            result.Packing = DetectPacking(probe);

            int headerOffset = FindHeader(probe[..read]);
            if (headerOffset < 0) return result.Packing == Compression.None ? null : result;

            if (!result.ParseHeader(probe[headerOffset..read])) return result;

            result.IsValid = true;

            // Блоки читаются только у несжатых файлов: за заголовком у сжатых идёт
            // уже сжатый поток, который без распаковщика не разобрать.
            if (result.Packing == Compression.None)
            {
                stream.Position = headerOffset + result.HeaderLength;
                result.ParseBlocks(stream);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private int HeaderLength { get; set; }

    private static Compression DetectPacking(ReadOnlySpan<byte> probe)
    {
        if (probe.Length >= 4 && probe[0] == 0x28 && probe[1] == 0xB5 && probe[2] == 0x2F && probe[3] == 0xFD)
            return Compression.Zstd;

        if (probe.Length >= 2 && probe[0] == 0x1F && probe[1] == 0x8B)
            return Compression.Gzip;

        return Compression.None;
    }

    /// <summary>Ищет сигнатуру BLENDER — у сжатых файлов она лежит не в начале.</summary>
    private static int FindHeader(ReadOnlySpan<byte> probe)
    {
        ReadOnlySpan<byte> signature = "BLENDER"u8;

        for (int i = 0; i + signature.Length <= probe.Length; i++)
            if (probe.Slice(i, signature.Length).SequenceEqual(signature)) return i;

        return -1;
    }

    private bool ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12) return false;

        // После BLENDER цифра означает новый формат: две цифры — длина заголовка.
        if (char.IsAsciiDigit((char)header[7]))
        {
            if (header.Length < 17) return false;

            HeaderLength = (header[7] - '0') * 10 + (header[8] - '0');

            PointerSize = header[9] == (byte)'_' ? 4 : 8;
            LittleEndian = header[12] == (byte)'v';

            // Четыре цифры версии: 0502 -> 5.2
            int version = ParseDigits(header.Slice(13, 4));
            Version = (version / 100) + "." + (version % 100);
        }
        else
        {
            HeaderLength = 12;

            PointerSize = header[7] == (byte)'_' ? 4 : 8;
            LittleEndian = header[8] == (byte)'v';

            // Три цифры: 293 -> 2.93
            int version = ParseDigits(header.Slice(9, 3));
            Version = (version / 100) + "." + (version % 100).ToString("00");
        }

        return PointerSize is 4 or 8;
    }

    private static int ParseDigits(ReadOnlySpan<byte> digits)
    {
        int value = 0;
        foreach (byte digit in digits)
        {
            if (!char.IsAsciiDigit((char)digit)) return value;
            value = value * 10 + (digit - '0');
        }

        return value;
    }

    /// <summary>
    /// Проходит по цепочке блоков до ENDB.
    ///
    /// Раскладка шапки установлена перебором на реальных файлах, потому что в новом
    /// формате она изменилась и в старых описаниях её нет:
    ///
    ///   старый (BLENDER-v293):  код(4) размер(4) адрес(PointerSize) SDNA(4) число(4)
    ///   новый  (BLENDER17-01):  код(4) выравнивание(4) адрес(8) размер(8) SDNA(4) число(4)
    ///
    /// То есть в новом формате шапка 32 байта, а размер стал 64-битным и переехал
    /// на смещение 16 — это плата за поддержку файлов больше 4 ГБ.
    /// </summary>
    private void ParseBlocks(FileStream stream)
    {
        bool newFormat = HeaderLength > 12;

        int headerSize = newFormat ? 32 : 16 + PointerSize;
        int sizeOffset = newFormat ? 16 : 4;

        byte[] header = new byte[headerSize];

        // Ограничение страхует от зацикливания на повреждённом файле.
        for (int guard = 0; guard < 2_000_000; guard++)
        {
            if (stream.Read(header, 0, headerSize) != headerSize) return;

            string code = Encoding.ASCII.GetString(header, 0, 4).TrimEnd('\0');

            long size = newFormat
                ? BitConverter.ToInt64(header, sizeOffset)
                : BitConverter.ToInt32(header, sizeOffset);

            if (code == "ENDB") { BlocksParsed = true; return; }
            if (size < 0 || size > stream.Length) return;

            if (code == "TEST")
            {
                ReadThumbnail(stream, size);
                continue;
            }

            // Двухбуквенные коды — датаблоки сцены; по ним и считается содержимое.
            if (code.Length == 2) Datablocks[code] = Datablocks.GetValueOrDefault(code) + 1;

            stream.Position += size;
        }
    }

    /// <summary>
    /// Блок TEST — встроенное превью: две ширины/высоты типа int, затем сырые пиксели RGBA.
    /// Это не PNG: сжатия нет, поэтому картинка читается напрямую.
    /// </summary>
    private void ReadThumbnail(FileStream stream, long size)
    {
        if (size < 8) { stream.Position += size; return; }

        byte[] dimensions = new byte[8];
        if (stream.Read(dimensions, 0, 8) != 8) return;

        int width = BitConverter.ToInt32(dimensions, 0);
        int height = BitConverter.ToInt32(dimensions, 4);

        long expected = (long)width * height * 4;

        // Здравые границы: превью Blender не бывает больше пары сотен пикселей.
        if (width is <= 0 or > 1024 || height is <= 0 or > 1024 || expected != size - 8)
        {
            stream.Position += size - 8;
            return;
        }

        byte[] pixels = new byte[expected];
        if (stream.Read(pixels, 0, (int)expected) != expected) return;

        ThumbnailWidth = width;
        ThumbnailHeight = height;
        ThumbnailPixels = pixels;
        HasThumbnail = true;
    }

    public string PackingText => Packing switch
    {
        Compression.Zstd => "сжат (zstd)",
        Compression.Gzip => "сжат (gzip)",
        _ => "без сжатия",
    };
}
