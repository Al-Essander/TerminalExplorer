using System.Diagnostics;
using System.Numerics;
using MftProbe;

// Бенчмарк ядра. Проверяет две гипотезы проекта:
//   1. том целиком индексируется через MFT за секунды, а не за минуты;
//   2. сигнатуры n-грамм (фильтр Блума над n-граммами имени) дают заметное ускорение
//      поиска подстроки — и насколько фактическая доля ложных срабатываний расходится
//      с расчётной по формуле Блума.

// Консоль Windows по умолчанию в кодовой странице 866 — кириллица превратится в мусор.
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "MftProbe — бенчмарк ядра Terminal Explorer";

char drive = args.Length > 0 ? args[0][0] : 'C';

Log.Line("MftProbe: том " + char.ToUpperInvariant(drive) + ":, вектор " +
         (Vector<ulong>.Count * 64) + " бит, " +
         (Vector.IsHardwareAccelerated ? "SIMD включён" : "SIMD НЕДОСТУПЕН"));

// Режим подбора размера буфера: главный рычаг, раз почти всё время уходит в ядро.
if (args.Length > 1 && args[1] == "iobench")
{
    Console.WriteLine();
    Console.WriteLine("  буфер     время     ядро    разбор   вызовов    MB/s   записей");
    Console.WriteLine("  -------------------------------------------------------------");

    foreach (int megabytes in new[] { 1, 4, 16, 64 })
    {
        var probe = new MftIndex(drive);
        var t = Stopwatch.StartNew();

        try { probe.Build(megabytes << 20); }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine("  ОШИБКА: " + ex.Message);
            Console.WriteLine();
            Console.WriteLine("Нажмите Enter для выхода.");
            Console.ReadLine();
            return 1;
        }

        t.Stop();

        Console.WriteLine(
            "  " + (megabytes + " MB").PadLeft(6) +
            t.Elapsed.TotalSeconds.ToString("F2").PadLeft(9) + "s" +
            (probe.IoMilliseconds / 1000).ToString("F2").PadLeft(8) + "s" +
            (probe.ParseMilliseconds / 1000).ToString("F2").PadLeft(9) + "s" +
            probe.IoCalls.ToString().PadLeft(10) +
            (probe.BytesRead / 1024.0 / 1024.0 / Math.Max(0.001, probe.IoMilliseconds / 1000)).ToString("F0").PadLeft(8) +
            probe.Count.ToString("N0").PadLeft(10));
    }

    Console.WriteLine();
    Console.WriteLine("Нажмите Enter для выхода.");
    Console.ReadLine();
    return 0;
}

var index = new MftIndex(drive);
var timer = Stopwatch.StartNew();

try
{
    index.Build();
}
catch (InvalidOperationException ex)
{
    Log.Line("ОШИБКА: " + ex.Message);

    // Без паузы окно, запущенное двойным кликом, закроется раньше, чем текст будет прочитан.
    Console.WriteLine();
    Console.WriteLine("Нажмите Enter для выхода.");
    Console.ReadLine();
    return 1;
}

timer.Stop();
double indexSeconds = timer.Elapsed.TotalSeconds;

Log.Line("MFT enum complete: " + index.Count.ToString("N0") + " records in " +
         indexSeconds.ToString("F2") + "s (" +
         (index.Count / Math.Max(0.001, indexSeconds)).ToString("N0") + " rec/s)");

// Разделение времени показывает, что чинить: чтение у ядра или собственные структуры.
Log.Line("  из них ядро (DeviceIoControl): " + (index.IoMilliseconds / 1000).ToString("F2") + "s за " +
         index.IoCalls + " вызовов, " + (index.BytesRead / 1024.0 / 1024.0).ToString("F1") + " MB (" +
         (index.BytesRead / 1024.0 / 1024.0 / Math.Max(0.001, index.IoMilliseconds / 1000)).ToString("F0") + " MB/s)");
Log.Line("  из них разбор записей:         " + (index.ParseMilliseconds / 1000).ToString("F2") + "s");

// Биграммы и 256 бит — конфигурация, выигравшая замер на живых данных.
BuildAndReport(2, 4);

Log.Line("память: имена " + (index.NameBufferBytes / 1024.0 / 1024.0).ToString("F1") +
         " MB, записи " + (index.EntriesBytes / 1024.0 / 1024.0).ToString("F1") +
         " MB, сигнатуры " + (index.SignatureBytes / 1024.0 / 1024.0).ToString("F1") + " MB");

Log.Line("ready");

Console.WriteLine();
Console.WriteLine("Введите подстроку для поиска.");
Console.WriteLine("Команды:  bench <запрос> — сравнить конфигурации фильтра");
Console.WriteLine("          set <n> <бит>  — сменить n-грамму и ширину (например: set 3 256)");
Console.WriteLine("          log            — показать лог");
Console.WriteLine("          пустая строка  — выход");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    string? line = Console.ReadLine();
    if (string.IsNullOrEmpty(line)) break;

    if (line == "log") { Log.Dump(); continue; }

    if (line.StartsWith("set ", StringComparison.Ordinal))
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && int.TryParse(parts[1], out int n) && int.TryParse(parts[2], out int bits))
            BuildAndReport(n, bits / 64);
        else
            Console.WriteLine("  формат: set <2..4> <64|128|256>");
        continue;
    }

    if (line.StartsWith("bench ", StringComparison.Ordinal))
    {
        RunBench(line[6..]);
        continue;
    }

    RunSearch(line);
}

return 0;

void BuildAndReport(int ngram, int words)
{
    var built = Stopwatch.StartNew();
    index.BuildSignatures(ngram, words);
    built.Stop();

    Log.Line("сигнатуры: " + ngram + "-граммы, " + index.SignatureBits + " бит, построены за " +
             built.ElapsedMilliseconds + " мс, " +
             (index.SignatureBytes / 1024.0 / 1024.0).ToString("F1") + " MB, заполнение " +
             (index.MeasureAverageFill() * 100).ToString("F1") + "%");
}

void RunSearch(string query)
{
    var naiveTimer = Stopwatch.StartNew();
    var naive = index.Search(query);
    naiveTimer.Stop();

    var filteredTimer = Stopwatch.StartNew();
    var filtered = index.SearchFiltered(query, out var stats);
    filteredTimer.Stop();

    foreach (int hit in filtered.Take(15))
        Console.WriteLine("  " + index.GetFullPath(hit));

    if (filtered.Count > 15)
        Console.WriteLine("  ... ещё " + (filtered.Count - 15));

    Console.WriteLine();

    double naiveMs = naiveTimer.Elapsed.TotalMilliseconds;
    double filteredMs = filteredTimer.Elapsed.TotalMilliseconds;

    Console.WriteLine("  найдено:      " + filtered.Count +
                      (naive.Count == filtered.Count
                          ? " (совпадает с наивным поиском)"
                          : "  !!! РАСХОЖДЕНИЕ: наивный нашёл " + naive.Count +
                            " — это ложноотрицательные, у фильтра Блума их быть не может, значит баг"));

    if (!stats.FilterUsed)
    {
        Console.WriteLine("  фильтр не применялся: запрос короче " + index.NgramSize + " символов");
        Console.WriteLine("  наивный скан: " + naiveMs.ToString("F1") + " мс");
        Console.WriteLine();
        return;
    }

    Console.WriteLine("  наивный скан: " + naiveMs.ToString("F1") + " мс");
    Console.WriteLine("  с фильтром:   " + filteredMs.ToString("F1") + " мс  (ускорение " +
                      (naiveMs / Math.Max(0.001, filteredMs)).ToString("F1") + "x)");
    Console.WriteLine("  отсеяно:      " + (stats.RejectionRate * 100).ToString("F2") +
                      "% записей, " + stats.BlocksSkipped.ToString("N0") + " векторных блоков целиком");
    Console.WriteLine("  прошли слово 1: " + stats.PassedFirstWord.ToString("N0") +
                      "  ->  всю сигнатуру: " + stats.Candidates.ToString("N0") +
                      "  ->  совпали: " + stats.Matches.ToString("N0"));
    Console.WriteLine("  ложные сраб.: " + (stats.FalsePositiveRate * 100).ToString("F2") +
                      "% факт  против  " + (index.PredictFalsePositiveRate(query) * 100).ToString("F2") +
                      "% по формуле Блума");
    Console.WriteLine();
}

// Перебор конфигураций на одном запросе. Именно эта таблица отвечает на вопрос,
// что сильнее бьёт по фильтру — ширина сигнатуры или частотность n-грамм.
void RunBench(string query)
{
    const int Runs = 5;   // одиночный замер шумит на десятки процентов, берём минимум

    int savedNgram = index.NgramSize, savedWords = index.SignatureWords;

    int expected = 0;
    double naiveMs = double.MaxValue;
    for (int r = 0; r < Runs; r++)
    {
        var nt = Stopwatch.StartNew();
        expected = index.Search(query).Count;
        nt.Stop();
        naiveMs = Math.Min(naiveMs, nt.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine();
    Console.WriteLine("  запрос \"" + query + "\", совпадений " + expected +
                      ", наивный скан " + naiveMs.ToString("F1") + " мс (лучшее из " + Runs + ")");
    Console.WriteLine();
    Console.WriteLine("  n-грамма  бит  заполн.  кандидатов   FP факт   FP формула   время   ускор.  память");
    Console.WriteLine("  --------------------------------------------------------------------------------");

    foreach (int ngram in new[] { 2, 3, 4 })
    {
        foreach (int words in new[] { 1, 2, 4 })
        {
            index.BuildSignatures(ngram, words);

            double ms = double.MaxValue;
            List<int> found = [];
            MftIndex.SearchStats stats = default;

            for (int r = 0; r < Runs; r++)
            {
                var t = Stopwatch.StartNew();
                found = index.SearchFiltered(query, out stats);
                t.Stop();
                ms = Math.Min(ms, t.Elapsed.TotalMilliseconds);
            }

            string flag = found.Count == expected ? "" : "  !!! РАСХОЖДЕНИЕ " + found.Count;

            Console.WriteLine(
                "  " + ngram.ToString().PadLeft(5) +
                (index.SignatureBits).ToString().PadLeft(7) +
                (index.MeasureAverageFill() * 100).ToString("F1").PadLeft(8) + "%" +
                stats.Candidates.ToString("N0").PadLeft(12) +
                (stats.FalsePositiveRate * 100).ToString("F2").PadLeft(9) + "%" +
                (index.PredictFalsePositiveRate(query) * 100).ToString("F3").PadLeft(12) + "%" +
                ms.ToString("F1").PadLeft(8) +
                (naiveMs / Math.Max(0.001, ms)).ToString("F1").PadLeft(7) + "x" +
                (index.SignatureBytes / 1024.0 / 1024.0).ToString("F0").PadLeft(6) + " MB" +
                flag);
        }
    }

    Console.WriteLine();
    index.BuildSignatures(savedNgram, savedWords);
}
