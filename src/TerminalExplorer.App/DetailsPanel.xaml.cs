using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TerminalExplorer;

/// <summary>
/// Панель сведений: что за объект, как устроена папка, чем занято место
/// и во что обошлись эти цифры.
/// </summary>
public partial class DetailsPanel : UserControl
{
    /// <summary>Строка карты типов: подпись, полоса и значение.</summary>
    public sealed record MapRow(string Label, string Bar, string Value);

    private CancellationTokenSource? _analysis;
    private FileSystemWatcher? _watcher;

    /// <summary>
    /// Гасит индикатор активности. События файловой системы приходят пачками, и без
    /// таймера индикатор либо мигал бы неразличимо быстро, либо горел постоянно.
    /// </summary>
    private readonly DispatcherTimer _activityTimer;

    private int _activityCount;

    public DetailsPanel()
    {
        InitializeComponent();

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _activityTimer.Tick += (_, _) =>
        {
            ActivityGlyph.Foreground = (System.Windows.Media.Brush)FindResource("Dim");
            _activityTimer.Stop();
        };
    }

    // ---------- показ объекта ----------

    private void HideBlender()
    {
        BlenderSection.Visibility = Visibility.Collapsed;
        ThumbnailFrame.Visibility = Visibility.Collapsed;
        ThumbnailImage.Source = null;
        _blendPath = null;
    }

    public void ShowNothing()
    {
        StopAnalysis();

        ObjectName.Text = LanguageManager.Tr("L_NothingSelected");
        ObjectKind.Text = string.Empty;
        ObjectFacts.Text = string.Empty;
        ObjectIcon.Source = null;

        TreeText.Text = string.Empty;
        TypeMap.ItemsSource = null;
        HistogramText.Text = string.Empty;
        HistogramNote.Text = string.Empty;
        BenfordText.Text = string.Empty;
        BenfordNote.Text = string.Empty;
        CostText.Text = string.Empty;
    }

    public void ShowFile(FileEntry entry)
    {
        StopAnalysis();

        ObjectIcon.Source = IconProvider.Get(entry);
        ObjectName.Text = entry.Name;
        ObjectKind.Text = entry.TypeText;

        var facts = new StringBuilder();
        facts.AppendLine("Размер:    " + entry.Size.ToString("N0") + " байт");
        facts.AppendLine("Изменён:   " + entry.ModifiedText);
        facts.AppendLine("Атрибуты:  " + entry.AttributesText);
        facts.Append("Путь:      " + entry.FullPath);

        ObjectFacts.Text = facts.ToString();

        // Для файла показываем, во сколько кластеров он лёг: это объясняет разницу
        // между «размером» и «размером на диске».
        const int ClusterSize = 4096;
        long clusters = (entry.Size + ClusterSize - 1) / ClusterSize;
        long onDisk = clusters * ClusterSize;

        CostText.Text =
            "Занято на диске: " + onDisk.ToString("N0") + " байт — это " + clusters.ToString("N0") +
            " кластеров по 4 КБ.\n" +
            "Потери на последнем кластере: " + (onDisk - entry.Size).ToString("N0") + " байт.\n\n" +
            "Поиск файла по имени в открытой папке — линейный проход, O(N).\n" +
            "Поиск по всему тому через индекс MFT — O(N) по числу записей тома, " +
            "но с отсевом сигнатурами n-грамм до сравнения строк доходит около 2% записей.";

        TreeText.Text = string.Empty;
        TypeMap.ItemsSource = null;
        HistogramText.Text = string.Empty;
        HistogramNote.Text = string.Empty;
        BenfordText.Text = string.Empty;
        BenfordNote.Text = string.Empty;

        ShowBlenderInfo(entry);
        WatchFolder(Path.GetDirectoryName(entry.FullPath));
    }

    public async void ShowFolder(string path, string displayName)
    {
        StopAnalysis();

        ObjectIcon.Source = IconProvider.GetForPath(path, isDirectory: true);
        ObjectName.Text = displayName;
        ObjectKind.Text = LanguageManager.Tr("L_ObjectFolder");
        ObjectFacts.Text = string.Format(LanguageManager.Tr("L_ObjectPath"), path);
        HideBlender();

        TreeText.Text = LanguageManager.Tr("L_Walking");
        CostText.Text = string.Empty;

        WatchFolder(path);

        var cancellation = new CancellationTokenSource();
        _analysis = cancellation;

        var progress = new Progress<FolderAnalysis.Progress>(report =>
        {
            if (cancellation.IsCancellationRequested) return;

            TreeText.Text =
                "обход...\n" +
                "файлов:  " + report.Files.ToString("N0") + "\n" +
                "папок:   " + report.Directories.ToString("N0") + "\n" +
                "объём:   " + FormatSize(report.Bytes) + "\n\n" +
                Shorten(report.Current);
        });

        try
        {
            FolderAnalysis analysis = await FolderAnalysis.RunAsync(path, cancellation.Token, progress);
            if (cancellation.IsCancellationRequested) return;

            Render(analysis);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopAnalysis()
    {
        _analysis?.Cancel();
        _analysis = null;
    }

    // ---------- Blender ----------

    private string? _blendPath;

    /// <summary>
    /// Разбирает .blend и показывает, что внутри: версия, чем сохранён, содержимое сцены
    /// и встроенное превью, если оно есть.
    /// </summary>
    private void ShowBlenderInfo(FileEntry entry)
    {
        BlenderSection.Visibility = Visibility.Collapsed;
        ThumbnailFrame.Visibility = Visibility.Collapsed;
        ThumbnailImage.Source = null;
        _blendPath = null;

        if (!string.Equals(entry.Extension, "blend", StringComparison.OrdinalIgnoreCase)) return;

        BlenderSection.Visibility = Visibility.Visible;
        _blendPath = entry.FullPath;

        BlendFile? blend = BlendFile.Read(entry.FullPath);

        if (blend is null || !blend.IsValid)
        {
            BlenderFacts.Text = "Не удалось прочитать заголовок — файл повреждён или это не .blend.";
            BlenderContent.Text = string.Empty;
            BlenderNote.Text = string.Empty;
            return;
        }

        BlenderFacts.Text =
            "Версия Blender:  " + blend.Version + "\n" +
            "Упаковка:        " + blend.PackingText + "\n" +
            "Указатели:       " + blend.PointerSize + " байта" +
            (blend.LittleEndian ? "  (little endian)" : "  (big endian)");

        RenderThumbnail(blend);
        RenderDatablocks(blend);
    }

    private void RenderThumbnail(BlendFile blend)
    {
        if (!blend.HasThumbnail || blend.ThumbnailPixels is null) return;

        try
        {
            // Превью хранится сырыми пикселями RGBA, а WPF ждёт BGRA — меняем местами
            // красный и синий каналы. Заодно переворачиваем по вертикали: Blender пишет
            // строки снизу вверх, как принято в OpenGL.
            int width = blend.ThumbnailWidth;
            int height = blend.ThumbnailHeight;

            byte[] source = blend.ThumbnailPixels;
            byte[] target = new byte[source.Length];

            for (int y = 0; y < height; y++)
            {
                int sourceRow = y * width * 4;
                int targetRow = (height - 1 - y) * width * 4;

                for (int x = 0; x < width; x++)
                {
                    int s = sourceRow + x * 4;
                    int t = targetRow + x * 4;

                    target[t + 0] = source[s + 2];   // B <- R
                    target[t + 1] = source[s + 1];   // G
                    target[t + 2] = source[s + 0];   // R <- B
                    target[t + 3] = source[s + 3];   // A
                }
            }

            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, target, width * 4);

            bitmap.Freeze();

            ThumbnailImage.Source = bitmap;
            ThumbnailFrame.Visibility = Visibility.Visible;
        }
        catch (ArgumentException)
        {
            // Размеры не сошлись — показываем остальное без картинки.
        }
    }

    private void RenderDatablocks(BlendFile blend)
    {
        if (!blend.BlocksParsed)
        {
            BlenderContent.Text = string.Empty;

            BlenderNote.Text = blend.Packing == BlendFile.Compression.None
                ? "Структура файла не разобрана до конца."
                : "Содержимое сцены доступно только у несжатых файлов: распаковка zstd " +
                  "потребовала бы сторонней библиотеки, а проект обходится без зависимостей. " +
                  "Сохраните файл без сжатия (в настройках Blender снимите «Compress»), " +
                  "чтобы увидеть состав сцены и превью.";
            return;
        }

        var text = new StringBuilder();
        int shown = 0;

        foreach (KeyValuePair<string, int> pair in blend.Datablocks.OrderByDescending(pair => pair.Value))
        {
            if (!BlendFile.DatablockNames.TryGetValue(pair.Key, out string? name)) continue;

            text.Append(name.PadRight(20)).Append(pair.Value.ToString("N0")).AppendLine();
            shown++;

            if (shown >= 12) break;
        }

        BlenderContent.Text = text.Length > 0 ? text.ToString() : "датаблоков не найдено";

        BlenderNote.Text =
            "Счётчики получены разбором цепочки блоков файла: у каждого блока есть " +
            "двухбуквенный код (OB — объект, ME — меш, MA — материал), размер и число " +
            "элементов. Blender для этого запускать не нужно — читается сам файл." +
            (blend.HasThumbnail
                ? "\n\nПревью взято из блока TEST: " + blend.ThumbnailWidth + "×" +
                  blend.ThumbnailHeight + " пикселей, сырой RGBA без сжатия."
                : "\n\nПревью в файле нет — включите «Save Preview Images» в настройках Blender.");
    }

    private void OpenInBlender_Click(object sender, RoutedEventArgs e)
    {
        if (_blendPath is null) return;

        try
        {
            // Без ключа -b: он запускает Blender в фоновом режиме, и окно не откроется.
            ShellActions.Open(_blendPath);
        }
        catch (Exception ex)
        {
            BlenderNote.Text = "не удалось открыть: " + ex.Message;
        }
    }

    // ---------- отрисовка отчёта ----------

    private void Render(FolderAnalysis analysis)
    {
        ObjectFacts.Text =
            "Путь:      " + analysis.Path + "\n" +
            "Файлов:    " + analysis.Files.ToString("N0") + "\n" +
            "Папок:     " + analysis.Directories.ToString("N0") + "\n" +
            "Объём:     " + FormatSize(analysis.TotalBytes) +
            (analysis.Denied > 0 ? "\nНет доступа к " + analysis.Denied + " папкам" : string.Empty);

        RenderTree(analysis);
        RenderTypeMap(analysis);
        RenderHistogram(analysis);
        RenderBenford(analysis);
        RenderCost(analysis);
    }

    /// <summary>Верхний уровень дерева псевдографикой, крупнейшие ветки сверху.</summary>
    private void RenderTree(FolderAnalysis analysis)
    {
        var text = new StringBuilder();
        text.AppendLine(Path.GetFileName(analysis.Path.TrimEnd('\\')) + "\\");

        const int Shown = 12;
        int count = Math.Min(Shown, analysis.Branches.Count);

        for (int i = 0; i < count; i++)
        {
            FolderAnalysis.Branch branch = analysis.Branches[i];
            bool last = i == count - 1 && analysis.Branches.Count <= Shown;

            text.Append(last ? "\\-- " : "+-- ")
                .Append(branch.Name)
                .Append(branch.IsDirectory ? "\\" : string.Empty)
                .Append("   ")
                .Append(FormatSize(branch.Bytes));

            if (branch.IsDirectory) text.Append("  (" + branch.Files.ToString("N0") + " файлов)");

            text.AppendLine();
        }

        if (analysis.Branches.Count > Shown)
            text.Append("\\-- ... ещё " + (analysis.Branches.Count - Shown));

        TreeText.Text = text.ToString();
    }

    private void RenderTypeMap(FolderAnalysis analysis)
    {
        var rows = new List<MapRow>();

        long total = Math.Max(1, analysis.TotalBytes);

        foreach (KeyValuePair<string, long> pair in analysis.BytesByExtension
                     .OrderByDescending(pair => pair.Value)
                     .Take(8))
        {
            double share = (double)pair.Value / total;

            rows.Add(new MapRow(
                pair.Key,
                new string('#', (int)Math.Round(share * 24)).PadRight(24, '-'),
                (share * 100).ToString("F1") + "%"));
        }

        TypeMap.ItemsSource = rows;
    }

    /// <summary>
    /// Гистограмма размеров в шкале степеней двойки. Столбцы нормируются к самому
    /// высокому — абсолютные числа тут менее интересны, чем форма распределения.
    /// </summary>
    private void RenderHistogram(FolderAnalysis analysis)
    {
        int peak = analysis.SizeHistogram.Max();
        if (peak == 0)
        {
            HistogramText.Text = LanguageManager.Tr("L_NoFiles");
            HistogramNote.Text = string.Empty;
            return;
        }

        var text = new StringBuilder();
        int peakBucket = 0;

        for (int i = 0; i < analysis.SizeHistogram.Length; i++)
        {
            int value = analysis.SizeHistogram[i];
            if (value == 0) continue;

            if (value == peak) peakBucket = i;

            text.Append(FormatSize(1L << i).PadLeft(8))
                .Append("  ")
                .Append(new string('#', Math.Max(1, value * 22 / peak)))
                .Append("  ")
                .AppendLine(value.ToString("N0"));
        }

        HistogramText.Text = text.ToString();

        HistogramNote.Text =
            "Шкала логарифмическая: каждая строка — файлы вдвое крупнее предыдущей. " +
            "Пик приходится на " + FormatSize(1L << peakBucket) + ". " +
            "Такая форма — колокол в логарифмической шкале — означает логнормальное " +
            "распределение: типичное для файловых систем, где размер складывается из " +
            "множества независимых множителей.";
    }

    /// <summary>
    /// Закон Бенфорда: доля чисел, начинающихся с цифры d, стремится к log10(1 + 1/d).
    /// Выполняется для величин, охватывающих несколько порядков, — размеры файлов
    /// как раз такие.
    /// </summary>
    private void RenderBenford(FolderAnalysis analysis)
    {
        int total = 0;
        for (int d = 1; d <= 9; d++) total += analysis.LeadingDigits[d];

        if (total < 30)
        {
            BenfordText.Text = LanguageManager.Tr("L_TooFewBenford");
            BenfordNote.Text = string.Empty;
            return;
        }

        var text = new StringBuilder();
        double deviation = 0;

        for (int d = 1; d <= 9; d++)
        {
            double actual = (double)analysis.LeadingDigits[d] / total;
            double expected = Math.Log10(1 + 1.0 / d);

            deviation += Math.Abs(actual - expected);

            text.Append(d)
                .Append("  ")
                .Append(new string('#', (int)Math.Round(actual * 60)).PadRight(20, '.'))
                .Append("  ")
                .Append((actual * 100).ToString("F1").PadLeft(5))
                .Append("%   ожидалось ")
                .Append((expected * 100).ToString("F1").PadLeft(5))
                .AppendLine("%");
        }

        BenfordText.Text = text.ToString();

        BenfordNote.Text =
            "Ожидаемая доля для цифры d равна log10(1 + 1/d): единица встречается " +
            "в 30,1% случаев, девятка — в 4,6%. Суммарное отклонение по всем цифрам: " +
            (deviation * 100).ToString("F1") + " процентных пункта. " +
            "Проверено на " + total.ToString("N0") + " файлах.";
    }

    private void RenderCost(FolderAnalysis analysis)
    {
        int entries = analysis.Files + analysis.Directories;

        double perEntry = entries > 0
            ? analysis.ElapsedMilliseconds * 1000 / entries
            : 0;

        CostText.Text =
            "Обход занял " + analysis.ElapsedMilliseconds.ToString("F0") + " мс: " +
            analysis.Enumerations.ToString("N0") + " обращений к файловой системе, " +
            entries.ToString("N0") + " записей, " +
            perEntry.ToString("F1") + " мкс на запись.\n\n" +

            "Сложность обхода — O(N) по числу записей: суммарный размер поддерева нигде " +
            "не хранится, узнать его можно только пройдя всё дерево. Глубина рекурсии " +
            "здесь " + analysis.MaxDepth + ", поэтому обход сделан итеративным, со своим стеком — " +
            "рекурсивный переполнил бы стек потока на глубоких деревьях.\n\n" +

            "Каждое обращение к каталогу — поиск в его B-дереве индекса NTFS, то есть " +
            "O(log k) по числу записей в каталоге. Итоговая оценка: O(N + D·log k), " +
            "где D — число каталогов." +

            (analysis.Completed ? string.Empty : "\n\n" + LanguageManager.Tr("L_AnalysisAborted"));
    }

    // ---------- живая активность ----------

    /// <summary>
    /// Следит за папкой и мигает индикатором на каждое событие. Это не декорация:
    /// события приходят от самой файловой системы, так что видно реальную активность —
    /// свою и чужую, от других программ.
    /// </summary>
    private void WatchFolder(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;

        _activityCount = 0;
        ActivityText.Text = string.Empty;

        if (path is null || !Directory.Exists(path)) return;

        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // На сетевых и съёмных дисках наблюдение может быть недоступно.
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // События приходят из потока наблюдателя — в UI только через диспетчер.
        Dispatcher.BeginInvoke(() =>
        {
            _activityCount++;

            ActivityGlyph.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
            ActivityText.Text = e.ChangeType.ToString().ToLowerInvariant() + " · " + _activityCount;

            _activityTimer.Stop();
            _activityTimer.Start();
        });
    }

    public void Release()
    {
        StopAnalysis();

        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>Сообщает главному окну, что панель просят открепить («поверх других»).</summary>
    public event Action? FloatRequested;

    private void Float_Click(object sender, RoutedEventArgs e) => FloatRequested?.Invoke();

    /// <summary>Показывает/скрывает кнопку открепления в зависимости от флага настройки.</summary>
    public void SetFloatVisible(bool visible) => FloatButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Укорачивает путь с середины — конец информативнее начала.</summary>
    private static string Shorten(string path, int limit = 44)
    {
        if (path.Length <= limit) return path;

        return "..." + path[^(limit - 3)..];
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024L * 1024 => (bytes / 1024.0).ToString("N0") + " K",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("N1") + " M",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("N2") + " G",
    };
}
