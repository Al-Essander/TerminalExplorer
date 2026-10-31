using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TerminalExplorer.Core;

/// <summary>
/// Индекс имён одного NTFS-тома, построенный прямым перечислением MFT.
///
/// Ключевая идея, отличающая нас от рекурсивного обхода каталогов: мы один раз читаем
/// таблицу MFT последовательно и получаем ВСЕ записи тома. Ни одного вызова FindFirstFile.
///
/// Имена не хранятся как отдельные string-объекты: миллион строк в .NET — это миллион
/// объектов по 24+ байта заголовка. Вместо этого все имена лежат встык в одном char-буфере,
/// а запись хранит лишь смещение и длину. Это экономит память и даёт линейный скан при поиске.
/// </summary>
public sealed partial class MftIndex
{
    /// <summary>Одна запись MFT. Без единой ссылки на объект — GC её не обходит.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Entry
    {
        public ulong Frn;         // номер записи в MFT (file reference number)
        public ulong ParentFrn;   // номер записи родительского каталога
        public int NameOffset;    // смещение имени в общем буфере
        public int NameLength;
        public uint Attributes;

        public readonly bool IsDirectory => (Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    private readonly char _driveLetter;
    private Entry[] _entries = [];
    private char[] _names = [];
    private int _entryCount;
    private int _nameLength;

    /// <summary>frn -> индекс в _entries. Нужен только для восстановления полного пути.</summary>
    private Dictionary<ulong, int> _byFrn = [];

    public MftIndex(char driveLetter) => _driveLetter = char.ToUpperInvariant(driveLetter);

    public int Count => _entryCount;
    public long NameBufferBytes => (long)_nameLength * sizeof(char);
    public long EntriesBytes => (long)_entryCount * Marshal.SizeOf<Entry>();

    /// <summary>Время внутри DeviceIoControl — собственно чтение MFT ядром.</summary>
    public double IoMilliseconds { get; private set; }

    /// <summary>Время разбора записей и наполнения структур.</summary>
    public double ParseMilliseconds { get; private set; }

    /// <summary>Сколько раз пришлось обратиться к ядру за очередной порцией.</summary>
    public int IoCalls { get; private set; }

    /// <summary>Объём данных, полученных от ядра.</summary>
    public long BytesRead { get; private set; }

    /// <summary>
    /// Читает весь MFT тома. Требует прав администратора: открытие тома с GENERIC_READ
    /// доступно только привилегированному процессу.
    /// </summary>
    /// <param name="bufferSize">
    /// Размер буфера одного обращения к ядру.
    ///
    /// Замер (docs/BENCHMARKS.md) показал, что на результат он практически не влияет:
    /// сокращение числа вызовов со 128 до 3 не изменило времени. Узкое место — не переходы
    /// в ядро, а скорость самого FSCTL (~35 МБ/с). 4 МБ выбраны как чуть лучший из
    /// измеренных вариантов.
    /// </param>
    public void Build(int bufferSize = 4 << 20)
    {
        // Форма пути важна: \\.\C: — БЕЗ завершающего слэша. С завершающим слэшем
        // CreateFile откроет корневой каталог, а не сам том, и FSCTL завершится ошибкой.
        string volumePath = @"\\.\" + _driveLetter + ":";

        using SafeFileHandle volume = Native.CreateFile(
            volumePath,
            Native.GENERIC_READ,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            0, Native.OPEN_EXISTING, 0, 0);

        if (volume.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                err == 5
                    ? "Отказано в доступе к тому " + _driveLetter + ": — запустите от имени администратора."
                    : "Не удалось открыть том " + _driveLetter + ": (код " + err + ").");
        }

        Log.Line("volume " + _driveLetter + ": opened");

        // Ёмкости подобраны под средний системный диск, чтобы избежать десятка перевыделений.
        _entries = new Entry[1 << 19];      // ~524k записей
        _names = new char[1 << 23];         // ~8M символов
        _byFrn = new Dictionary<ulong, int>(1 << 19);

        nint buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            var input = new Native.MFT_ENUM_DATA_V0
            {
                StartFileReferenceNumber = 0,
                LowUsn = 0,
                HighUsn = long.MaxValue,
            };

            long ioTicks = 0, parseTicks = 0;

            while (true)
            {
                long mark = Stopwatch.GetTimestamp();

                bool ok = Native.DeviceIoControl(
                    volume, Native.FSCTL_ENUM_USN_DATA,
                    ref input, Marshal.SizeOf<Native.MFT_ENUM_DATA_V0>(),
                    buffer, bufferSize,
                    out int bytesReturned, 0);

                ioTicks += Stopwatch.GetTimestamp() - mark;
                IoCalls++;
                if (ok) BytesRead += bytesReturned;

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    // 38 = ERROR_HANDLE_EOF — нормальное завершение перечисления.
                    if (err == 38) break;
                    throw new InvalidOperationException("FSCTL_ENUM_USN_DATA завершился с кодом " + err + ".");
                }

                // Первые 8 байт ответа — FRN, с которого продолжать. Меньше 8 байт = данных больше нет.
                if (bytesReturned <= sizeof(ulong)) break;

                mark = Stopwatch.GetTimestamp();

                unsafe
                {
                    byte* start = (byte*)buffer;
                    input.StartFileReferenceNumber = *(ulong*)start;

                    int offset = sizeof(ulong);
                    while (offset < bytesReturned)
                    {
                        byte* record = start + offset;
                        int recordLength = *(int*)record;
                        if (recordLength <= 0) break;

                        AddRecord(record);
                        offset += recordLength;
                    }
                }

                parseTicks += Stopwatch.GetTimestamp() - mark;

                // Копия нужна, чтобы input не попал в замыкание: он передаётся по ref в DeviceIoControl.
                ulong position = input.StartFileReferenceNumber;

                // Вызывается на каждом мегабайтном блоке, но печатает не чаще 100 мс.
                Log.Progress(() =>
                    "      MFT enum: " + _entryCount.ToString("N0") + " records, frn=0x" +
                    position.ToString("x"));
            }

            IoMilliseconds = ioTicks * 1000.0 / Stopwatch.Frequency;
            ParseMilliseconds = parseTicks * 1000.0 / Stopwatch.Frequency;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        Log.ClearProgress();
    }

    /// <summary>
    /// Разбирает USN_RECORD_V2. Смещения полей зафиксированы структурой из winioctl.h
    /// и не меняются между версиями Windows, поэтому читаем их напрямую.
    /// </summary>
    private unsafe void AddRecord(byte* record)
    {
        ulong frn = *(ulong*)(record + 8);
        ulong parentFrn = *(ulong*)(record + 16);
        uint attributes = *(uint*)(record + 52);
        int nameLength = *(ushort*)(record + 56) / sizeof(char);
        int nameOffset = *(ushort*)(record + 58);

        var name = new ReadOnlySpan<char>(record + nameOffset, nameLength);

        if (_entryCount == _entries.Length)
            Array.Resize(ref _entries, _entries.Length * 2);
        while (_nameLength + nameLength > _names.Length)
            Array.Resize(ref _names, _names.Length * 2);

        name.CopyTo(_names.AsSpan(_nameLength));

        _entries[_entryCount] = new Entry
        {
            Frn = frn,
            ParentFrn = parentFrn,
            NameOffset = _nameLength,
            NameLength = nameLength,
            Attributes = attributes,
        };

        // MFT может содержать несколько записей с одним FRN (жёсткие ссылки).
        // Для восстановления пути достаточно первой встреченной.
        _byFrn.TryAdd(frn, _entryCount);

        _nameLength += nameLength;
        _entryCount++;
    }

    public ReadOnlySpan<char> GetName(int index)
    {
        ref readonly Entry e = ref _entries[index];
        return _names.AsSpan(e.NameOffset, e.NameLength);
    }

    /// <summary>Является ли запись каталогом. Для UI: папки и файлы выглядят по-разному.</summary>
    public bool IsDirectoryAt(int index)
    {
        if ((uint)index >= (uint)_entryCount) return false;
        return _entries[index].IsDirectory;
    }

    /// <summary>
    /// Поиск подстроки по всем именам тома. Ровно тот линейный скан, который заменяет Trie:
    /// работает по середине имени (Trie умеет только префикс) и не требует отдельной структуры.
    /// </summary>
    public List<int> Search(string query, int limit = int.MaxValue)
    {
        var results = new List<int>();
        ReadOnlySpan<char> q = query;

        for (int i = 0; i < _entryCount && results.Count < limit; i++)
        {
            ref readonly Entry e = ref _entries[i];
            var name = _names.AsSpan(e.NameOffset, e.NameLength);
            if (name.Contains(q, StringComparison.OrdinalIgnoreCase))
                results.Add(i);
        }

        return results;
    }

    /// <summary>Восстанавливает полный путь подъёмом по цепочке родителей до корня тома.</summary>
    public string GetFullPath(int index)
    {
        var parts = new List<string>(8);
        ref readonly Entry e = ref _entries[index];

        parts.Add(new string(_names.AsSpan(e.NameOffset, e.NameLength)));
        ulong parent = e.ParentFrn;

        // Ограничение глубины страхует от цикла в повреждённой MFT.
        for (int depth = 0; depth < 256 && parent != Native.RootFileReferenceNumber; depth++)
        {
            if (!_byFrn.TryGetValue(parent, out int parentIndex)) break;

            ref readonly Entry p = ref _entries[parentIndex];
            parts.Add(new string(_names.AsSpan(p.NameOffset, p.NameLength)));
            parent = p.ParentFrn;
        }

        var sb = new StringBuilder().Append(_driveLetter).Append(":\\");
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            sb.Append(parts[i]);
            if (i > 0) sb.Append('\\');
        }

        return sb.ToString();
    }
}
