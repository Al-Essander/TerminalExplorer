using System.IO;
using System.Media;

namespace TerminalExplorer;

/// <summary>
/// Звуковые события интерфейса.
///
/// Своих звуков в проекте нет и не будет: класть чужие системные WAV в репозиторий —
/// вопрос лицензии. Вместо этого пользователь кладёт свои файлы в
/// %APPDATA%\TerminalExplorer\Sounds, а мы их подхватываем по имени.
///
/// Поддерживается WAV: System.Media.SoundPlayer входит в BCL и не тянет зависимостей,
/// в отличие от MediaPlayer, который требует живого Dispatcher и умеет больше форматов,
/// но для коротких щелчков интерфейса это лишнее.
/// </summary>
public static class Sounds
{
    public const string TabSwitch = "tab";
    public const string TabClose = "close";
    public const string Navigate = "navigate";
    public const string FolderIn = "folder-in";
    public const string FolderOut = "folder-out";
    public const string Error = "error";

    public static string DirectoryPath => Path.Combine(SessionStore.DirectoryPath, "Sounds");

    /// <summary>
    /// Кэш проигрывателей: SoundPlayer читает файл с диска при каждой загрузке,
    /// а звук переключения вкладки срабатывает часто.
    /// </summary>
    private static readonly Dictionary<string, SoundPlayer?> Cache = [];

    public static void Play(string name)
    {
        if (!AppSettings.Current.SoundEnabled) return;

        try
        {
            if (!Cache.TryGetValue(name, out SoundPlayer? player))
            {
                string path = Path.Combine(DirectoryPath, name + ".wav");
                if (File.Exists(path)) player = new SoundPlayer(path);
                else if (name is FolderIn or FolderOut) player = new SoundPlayer(new MemoryStream(Whoosh(entering: name == FolderIn)));
                else player = null;

                player?.Load();
                Cache[name] = player;
            }

            // Play, а не PlaySync: интерфейс не должен ждать окончания звука.
            player?.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            // Битый или занятый WAV не повод ломать навигацию — просто молчим.
            Cache[name] = null;
        }
    }

    /// <summary>
    /// Встроенный «вжух» для входа и выхода из папки — чтобы звук работал без единого файла.
    /// Синтезируется на лету: белый шум через фильтр низких частот, у которого частота среза
    /// ползёт вверх (вход: воздух «втягивается» и светлеет) или вниз (выход: затихает и тускнеет),
    /// под плавной огибающей. Файл folder-in.wav / folder-out.wav в папке звуков заменит его.
    /// </summary>
    private static byte[] Whoosh(bool entering)
    {
        const int rate = 22050;
        const double seconds = 0.27;

        int count = (int)(rate * seconds);
        var random = new Random(entering ? 7 : 11);
        var samples = new short[count];
        double low = 0;

        for (int i = 0; i < count; i++)
        {
            double t = i / (double)count;
            double sweep = entering ? t : 1 - t;

            double cutoff = 220 + 3600 * sweep * sweep;
            double a = 1 - Math.Exp(-2 * Math.PI * cutoff / rate);

            low += a * ((random.NextDouble() * 2 - 1) - low);

            // Дисперсия отфильтрованного шума зависит от среза — выравниваем, иначе низ звучал бы тише.
            double level = low / Math.Sqrt(a / (2 - a) / 3);

            // Вход нарастает быстрее и затухает медленнее, выход — наоборот.
            double envelope = Math.Sin(Math.PI * Math.Pow(t, entering ? 0.65 : 1.5));

            samples[i] = (short)Math.Clamp(level * envelope * 0.15 * short.MaxValue, short.MinValue, short.MaxValue);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + count * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(count * 2);

        foreach (short sample in samples) writer.Write(sample);

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Сбрасывает кэш — нужно после того, как пользователь положил новые файлы.</summary>
    public static void Reload() => Cache.Clear();

    /// <summary>Создаёт папку для звуков и возвращает её путь — для кнопки «открыть папку».</summary>
    public static string EnsureDirectory()
    {
        Directory.CreateDirectory(DirectoryPath);
        return DirectoryPath;
    }

    /// <summary>Имена файлов, которые программа ищет. Показывается в настройках.</summary>
    public static IReadOnlyList<string> ExpectedFiles =>
        [TabSwitch + ".wav", TabClose + ".wav", Navigate + ".wav", FolderIn + ".wav", FolderOut + ".wav", Error + ".wav"];
}
