using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TerminalExplorer;

/// <summary>
/// Правая панель окна поиска: визуальная схема фильтра Блума.
///
/// Не текст «как это работает», а блок-схема из примитивов — видно, как что из чего идёт:
///   запрос → n-граммы → биты маски → сигнатуры имён → AND-вердикт.
///
/// Каждая n-грамма хешируется (FNV-1a) в номер бита; из них собирается маска запроса.
/// Для показа имён рисуется их 64-битная сигнатура, и совпавшие с маской биты подсвечиваются.
/// Цвет тем берётся напрямую из ресурсов, чтобы панель жила в любой палитре.
/// </summary>
public partial class BloomPanel : UserControl
{
    // Ширина сигнатуры для визуала. 64 бита = 8 байт (как выигравшая конфигурация ядра).
    private const int Bits = 64;

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

    // ---------- построение схемы ----------

    private void RenderDemo()
    {
        VisualHost.Children.Clear();

        if (string.IsNullOrWhiteSpace(_query))
        {
            HeaderText.Text = LanguageManager.Tr("L_BloomTitle");
            VisualHost.Children.Add(Label(LanguageManager.Tr("L_BloomEmpty")));
            return;
        }

        HeaderText.Text = string.Format(
            LanguageManager.Tr("L_BloomHeader"), _query, _matchCount);

        // 1. Запрос
        VisualHost.Children.Add(StepHeading(LanguageManager.Tr("L_BloomStep1")));
        VisualHost.Children.Add(Block(_query));

        // 2. n-граммы
        var ngrams = BuildNgrams(_query);
        VisualHost.Children.Add(StepHeading(LanguageManager.Tr("L_BloomStep2")));
        VisualHost.Children.Add(ChipsRow(ngrams));

        // 3. Хеш каждой n-граммы → номер бита
        var bitIndices = ngrams.Select(NgramBitAt).ToArray();
        VisualHost.Children.Add(StepHeading(LanguageManager.Tr("L_BloomStep3")));
        VisualHost.Children.Add(BitLabelsRow(ngrams, bitIndices));

        // 4. Маска запроса (64 бита)
        ulong mask = Mask(bitIndices);
        VisualHost.Children.Add(StepHeading(LanguageManager.Tr("L_BloomStep4")));
        VisualHost.Children.Add(BitRow(mask, mask));

        // 5. Имена-кандидаты
        VisualHost.Children.Add(StepHeading(LanguageManager.Tr("L_BloomStep5")));
        foreach (string name in DemoNames())
            VisualHost.Children.Add(ExampleBlock(name, mask));
    }

    private static List<string> BuildNgrams(string query)
    {
        var result = new List<string>();
        const int n = 2; // биграммы — конфигурация, победившая в BENCHMARKS.md
        for (int i = 0; i + n <= query.Length; i++)
            result.Add(query.Substring(i, n));
        return result;
    }

    /// <summary>Бит маски (0..63) для данной n-граммы. Повторяет NgramBit ядра.</summary>
    private static int NgramBitAt(string ngram)
    {
        const uint FnvOffset = 2166136261;
        const uint FnvPrime = 16777619;

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

    private static ulong Mask(int[] bits)
    {
        ulong mask = 0;
        foreach (int bit in bits)
            mask |= 1UL << (bit & 63);
        return mask;
    }

    private static ulong Signature(string name)
    {
        ulong signature = 0;
        const int n = 2;
        for (int i = 0; i + n <= name.Length; i++)
            signature |= 1UL << (NgramBitAt(name.Substring(i, n)) & 63);
        return signature;
    }

    private static IEnumerable<string> DemoNames()
    {
        // Разнородный набор: там, где имя содержит запрос, и где нет.
        yield return "red_sofa.blend";
        yield return "document.txt";
        yield return "report.pdf";
        yield return "sofa_final.blend";
    }

    private static bool Contains(string name, string query) =>
        name.Contains(query, StringComparison.OrdinalIgnoreCase);

    // ---------- примитивы для схемы ----------

    private static UIElement Label(string text) => new TextBlock
    {
        Text = text,
        Foreground = Brush("Dim"),
        FontFamily = Font("MonoFont"),
        Margin = new Thickness(0, 2, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private static UIElement StepHeading(string text) => new TextBlock
    {
        Text = text,
        Foreground = Brush("Accent"),
        FontFamily = Font("MonoFont"),
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 12, 0, 4),
    };

    private static UIElement Block(string text) => new Border
    {
        Background = Brush("Surface"),
        BorderBrush = Brush("Border"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 4, 8, 4),
        Margin = new Thickness(0, 0, 0, 2),
        HorizontalAlignment = HorizontalAlignment.Left,
        Child = new TextBlock
        {
            Text = text,
            Foreground = Brush("Foreground"),
            FontFamily = Font("MonoFont"),
        },
    };

    /// <summary>Ряд чипов с n-граммами (текст + подпись).</summary>
    private static UIElement ChipsRow(List<string> ngrams)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

        foreach (string ngram in ngrams)
        {
            panel.Children.Add(new Border
            {
                Background = Brush("Selection"),
                BorderBrush = Brush("Border"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 2, 5, 2),
                Margin = new Thickness(0, 0, 3, 0),
                Child = new TextBlock
                {
                    Text = ngram,
                    Foreground = Brush("Foreground"),
                    FontFamily = Font("MonoFont"),
                },
            });
        }

        return panel;
    }

    /// <summary>Под каждым чипом — номер бита, в который он хешируется.</summary>
    private static UIElement BitLabelsRow(List<string> ngrams, int[] bitIndices)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

        for (int i = 0; i < ngrams.Count; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = ngrams[i] + " → " + bitIndices[i],
                Foreground = Brush("Dim"),
                FontFamily = Font("MonoFont"),
                Margin = new Thickness(0, 0, 8, 0),
            });
        }

        return panel;
    }

    /// <summary>Строка из 64 бит. highlight — биты, которые подкрашиваются акцентом (совпали/установлены).</summary>
    private static UIElement BitRow(ulong value, ulong highlight)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        for (int i = 0; i < Bits; i++)
        {
            bool set = (value & (1UL << i)) != 0;
            bool on = (highlight & (1UL << i)) != 0;

            panel.Children.Add(BitCell(set, on));
        }

        return panel;
    }

    /// <summary>Одна ячейка бита. Заливка: установлен + подсвечен → акцент; установлен → поверхность; иначе пусто.</summary>
    private static Border BitCell(bool set, bool on)
    {
        Brush? fill = on
            ? Brush("Accent")
            : set
                ? Brush("BevelLight")
                : null;

        var cell = new Border
        {
            Width = 6,
            Height = 12,
            Margin = new Thickness(0.5),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(1),
            Background = fill ?? Brush("Background"),
        };

        return cell;
    }

    /// <summary>Один пример имени: имя + его сигнатура + вердикт AND.</summary>
    private UIElement ExampleBlock(string name, ulong mask)
    {
        ulong signature = Signature(name);
        bool passes = (signature & mask) == mask;
        bool contains = Contains(name, _query);

        var container = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        container.Children.Add(new TextBlock
        {
            Text = name,
            Foreground = contains ? Brush("Accent") : Brush("Foreground"),
            FontFamily = Font("MonoFont"),
            FontWeight = contains ? FontWeights.Bold : FontWeights.Normal,
        });

        container.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 2, 0, 0),
            Children = { CreateBitRowView(signature, mask) },
        });

        string verdict = passes
            ? (contains ? LanguageManager.Tr("L_BloomAndPass") : LanguageManager.Tr("L_BloomAndFalse"))
            : LanguageManager.Tr("L_BloomAndRejected");
        container.Children.Add(new TextBlock
        {
            Text = verdict,
            Foreground = passes && !contains ? Brush("Dim") : (passes ? Brush("Accent") : Brush("Dim")),
            FontFamily = Font("MonoFont"),
            Margin = new Thickness(0, 2, 0, 0),
        });

        return container;
    }

    private StackPanel CreateBitRowView(ulong signature, ulong mask)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        for (int i = 0; i < Bits; i++)
        {
            bool set = (signature & (1UL << i)) != 0;
            bool inMask = (mask & (1UL << i)) != 0;
            panel.Children.Add(BitCell(set && inMask, inMask)); // и в маске, и в сигнатуре → акцент
        }

        return panel;
    }

    // ---------- доступ к ресурсам темы ----------

    private static FontFamily Font(string key) =>
        Application.Current.TryFindResource(key) as FontFamily ?? new FontFamily("Consolas");

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}