using System.Text;
using System.Windows.Controls;

namespace TerminalExplorer;

/// <summary>
/// Правая панель окна поиска: показывает, как запрос обрабатывается фильтром Блума.
///
/// Слева направо шаг за шагом:
///   1. запрос разбивается на n-граммы;
///   2. каждая n-грамма хешируется в номер бита сигнатуры (NgramBit);
///   3. для нескольких имен рисуется их 64-битная сигнатура и проверка AND;
///   4. показывается, сколько имён «отсеяно» и сколько прошло на сравнение.
///
/// Логика повторяет ComputeSignature/NgramBit из ядра, но на маленьком наборе данных,
/// чтобы пользователь видел принцип, а не результат на миллионе имён. Анимация — текстом
/// в mono-шрифте, в духе MS-DOS.
/// </summary>
public partial class BloomPanel : UserControl
{
    private string _query = string.Empty;
    private int _matchCount;

    public BloomPanel()
    {
        InitializeComponent();
        RenderDemo();
    }

    public void Reset() => RenderDemo();

    public void ShowForQuery(string query, SearchScope scope, int matchCount)
    {
        _query = query;
        _matchCount = matchCount;
        RenderDemo();
    }

    private void RenderDemo()
    {
        string query = _query;

        // Пустой запрос — показываем подсказку.
        if (string.IsNullOrWhiteSpace(query))
        {
            HeaderText.Text = "КАК РАБОТАЕТ ФИЛЬТР";
            AsciiText.Text = "Введите запрос, чтобы увидеть,\n" +
                             "как имя отсеивается сигнатурой.";
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("  запрос: " + query);
        builder.AppendLine();

        var ngrams = BuildNgrams(query);
        builder.AppendLine("  n-граммы:");
        builder.AppendLine("    " + string.Join(" ", ngrams));
        builder.AppendLine();

        var bits = ngrams.Select(NgramBit).ToArray();
        builder.AppendLine("  биты маски (по FNV-1a):");
        builder.AppendLine("    " + string.Join(" ", bits.Select(b => b.ToString())));
        builder.AppendLine();

        // Несколько «придуманных» имён: отсев нагляднее на разнородном наборе.
        string[] names = ["red_sofa.blend", "document.txt", "report.pdf", "sofa_final.blend"];
        foreach (string name in names)
        {
            ulong signature = ComputeSignature(name);
            bool passes = (signature & Mask(bits)) == Mask(bits);
            bool contains = name.Contains(query, StringComparison.OrdinalIgnoreCase);

            builder.AppendLine("  имя: " + name);
            builder.Append("    сигнатура: " + FormatSignature(signature));
            builder.AppendLine();
            builder.AppendLine(passes
                ? "    AND: прошло (возможно совпадение)"
                : "    AND: отсеяно (не содержит подстроку)");
            if (passes && !contains)
                builder.AppendLine("      -> ложноположительное срабатывание (пошло на сравнение, но не совпало)");
            builder.AppendLine();
        }

        builder.AppendLine("  итог по запросу: найдено " + _matchCount + " вхождений.");

        HeaderText.Text = "КАК РАБОТАЕТ ФИЛЬТР БЛУМА";
        AsciiText.Text = builder.ToString().TrimEnd();
    }

    private static List<string> BuildNgrams(string query)
    {
        var result = new List<string>();
        int n = 2; // биграммы — конфигурация, победившая в BENCHMARKS.md
        for (int i = 0; i + n <= query.Length; i++)
            result.Add(query.Substring(i, n));
        return result;
    }

    private static int NgramBit(string ngram)
    {
        // Повторяет NgramBit из ядра (4096 бит для наглядности; в ядре 64/128/256).
        const uint FnvOffset = 2166136261;
        const uint FnvPrime = 16777619;
        const int Bits = 4096;

        uint hash = FnvOffset;
        foreach (char c in ngram)
        {
            uint lower = char.ToLowerInvariant(c);
            hash = (hash ^ (lower & 0xFF)) * FnvPrime;
            hash = (hash ^ (lower >> 8)) * FnvPrime;
        }

        hash ^= hash >> 16;
        hash *= 2246822507;
        hash ^= hash >> 13;
        hash *= 3266489909;
        hash ^= hash >> 16;

        return (int)(hash % Bits);
    }

    // Маска запроса: один бит на каждую n-грамму.
    private static ulong Mask(int[] bits)
    {
        ulong mask = 0;
        foreach (int bit in bits)
            mask |= 1UL << (bit & 63);
        return mask;
    }

    // Сигнатура имени: устанавливаем биты для всех n-грамм.
    private static ulong ComputeSignature(string name)
    {
        ulong signature = 0;
        int n = 2;
        for (int i = 0; i + n <= name.Length; i++)
        {
            int bit = NgramBit(name.Substring(i, n));
            signature |= 1UL << (bit & 63);
        }
        return signature;
    }

    private static string FormatSignature(ulong signature) =>
        Convert.ToString((long)signature, 2).PadLeft(64, '0');
}