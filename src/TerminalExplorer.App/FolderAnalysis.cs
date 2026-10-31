using System.Diagnostics;
using System.IO;

namespace TerminalExplorer;

/// <summary>
/// Результат обхода папки: и сами данные, и цена, которой они достались.
///
/// Стоимость обхода показывается намеренно. Размер папки нельзя узнать иначе, чем обойдя
/// её целиком: файловая система не хранит суммарный размер поддерева, поэтому операция
/// принципиально линейна по числу записей — O(N). Видеть это число полезно: становится
/// понятно, почему «посчитать размер» иногда мгновенно, а иногда занимает минуты.
/// </summary>
public sealed class FolderAnalysis
{
    public string Path { get; init; } = string.Empty;

    public int Files { get; set; }
    public int Directories { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>Глубина самой длинной ветки. Определяет глубину рекурсии обхода.</summary>
    public int MaxDepth { get; set; }

    /// <summary>
    /// Число обращений к файловой системе — по одному перечислению на каталог.
    /// Это и есть реальная стоимость операции: каждое обращение стоит обхода
    /// каталожного индекса NTFS, то есть O(log k) по числу записей в нём.
    /// </summary>
    public int Enumerations { get; set; }

    /// <summary>Каталоги, куда не пустили. Их содержимое в статистику не попало.</summary>
    public int Denied { get; set; }

    public double ElapsedMilliseconds { get; set; }

    /// <summary>Сколько байт приходится на расширение. Основа для карты типов.</summary>
    public Dictionary<string, long> BytesByExtension { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Гистограмма размеров по степеням двойки: индекс i — файлы от 2^i до 2^(i+1) байт.
    /// На реальных файловых системах распределение близко к логнормальному, и в такой
    /// шкале это видно сразу — колокол со смещённой вершиной.
    /// </summary>
    public int[] SizeHistogram { get; } = new int[40];

    /// <summary>Первые значащие цифры размеров — для проверки закона Бенфорда.</summary>
    public int[] LeadingDigits { get; } = new int[10];

    /// <summary>Верхний уровень дерева — то, что показывается в панели.</summary>
    public List<Branch> Branches { get; } = [];

    /// <summary>Ветка первого уровня с собственным итогом.</summary>
    public sealed record Branch(string Name, bool IsDirectory, long Bytes, int Files);

    public bool Completed { get; set; }

    /// <summary>
    /// Обходит папку целиком. Обход итеративный, со своим стеком: рекурсия по глубокому
    /// дереву (например, node_modules) переполнила бы стек потока.
    /// </summary>
    /// <summary>Промежуточные цифры для показа прогресса.</summary>
    public readonly record struct Progress(int Files, int Directories, long Bytes, string Current);

    public static Task<FolderAnalysis> RunAsync(
        string path, CancellationToken cancellation, IProgress<Progress>? progress = null)
    {
        return Task.Run(() =>
        {
            // Отчёт по таймеру, а не на каждую запись: на диске с миллионом файлов
            // обновление интерфейса на каждый файл стоило бы дороже самого обхода.
            var reportTimer = Stopwatch.StartNew();
            long lastReport = 0;
            var analysis = new FolderAnalysis { Path = path };
            var timer = Stopwatch.StartNew();

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
            };

            // Стек пар «каталог, глубина» вместо рекурсии.
            var pending = new Stack<(string Path, int Depth, int BranchIndex)>();

            // Верхний уровень обходим отдельно: его элементы становятся ветками отчёта.
            var branchTotals = new List<(string Name, bool IsDirectory, long Bytes, int Files)>();

            try
            {
                var root = new DirectoryInfo(path);
                analysis.Enumerations++;

                foreach (FileSystemInfo entry in root.EnumerateFileSystemInfos("*", options))
                {
                    cancellation.ThrowIfCancellationRequested();

                    bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;

                    if (isDirectory)
                    {
                        branchTotals.Add((entry.Name, true, 0, 0));
                        pending.Push((entry.FullName, 1, branchTotals.Count - 1));
                        analysis.Directories++;
                    }
                    else
                    {
                        long size = ((FileInfo)entry).Length;
                        branchTotals.Add((entry.Name, false, size, 1));

                        analysis.Files++;
                        analysis.Register(entry.Name, size);
                    }
                }

                while (pending.Count > 0)
                {
                    cancellation.ThrowIfCancellationRequested();

                    (string current, int depth, int branchIndex) = pending.Pop();
                    analysis.MaxDepth = Math.Max(analysis.MaxDepth, depth);
                    analysis.Enumerations++;

                    if (progress is not null && reportTimer.ElapsedMilliseconds - lastReport > 200)
                    {
                        lastReport = reportTimer.ElapsedMilliseconds;
                        progress.Report(new Progress(
                            analysis.Files, analysis.Directories, analysis.TotalBytes, current));
                    }

                    try
                    {
                        foreach (FileSystemInfo entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                        {
                            if ((entry.Attributes & FileAttributes.Directory) != 0)
                            {
                                analysis.Directories++;
                                pending.Push((entry.FullName, depth + 1, branchIndex));
                            }
                            else
                            {
                                long size = ((FileInfo)entry).Length;

                                analysis.Files++;
                                analysis.Register(entry.Name, size);

                                (string name, bool isDir, long bytes, int files) = branchTotals[branchIndex];
                                branchTotals[branchIndex] = (name, isDir, bytes + size, files + 1);
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        analysis.Denied++;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Папку удалили прямо во время обхода — обычное дело на живой системе.
                    }
                }

                analysis.Completed = true;
            }
            catch (OperationCanceledException)
            {
                // Частичный результат тоже полезен — показываем то, что успели посчитать.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                analysis.Denied++;
            }

            foreach ((string name, bool isDirectory, long bytes, int files) in branchTotals)
                analysis.Branches.Add(new Branch(name, isDirectory, bytes, files));

            analysis.Branches.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

            timer.Stop();
            analysis.ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;

            return analysis;
        }, cancellation);
    }

    private void Register(string name, long size)
    {
        TotalBytes += size;

        string extension = System.IO.Path.GetExtension(name).TrimStart('.');
        if (extension.Length == 0) extension = "без расширения";

        BytesByExtension.TryGetValue(extension, out long current);
        BytesByExtension[extension] = current + size;

        if (size > 0)
        {
            int bucket = (int)Math.Log2(size);
            if (bucket >= 0 && bucket < SizeHistogram.Length) SizeHistogram[bucket]++;

            LeadingDigits[FirstDigit(size)]++;
        }
    }

    /// <summary>Первая значащая цифра числа — вход для проверки закона Бенфорда.</summary>
    private static int FirstDigit(long value)
    {
        while (value >= 10) value /= 10;
        return (int)value;
    }
}
