using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TerminalExplorer;

/// <summary>
/// Лёгкий токенизатор для подсветки C/C++/C# без внешних библиотек.
///
/// Разбивает код на цветные фрагменты: комментарии, строки, символьные литералы,
/// числа, директивы препроцессора, ключевые слова и встроенные типы. Всё остальное
/// остаётся обычным цветом. Работает поверх TextBlock, который подкладывается под
/// прозрачный TextBox редактора — сам редактор не переписывается.
/// </summary>
public static class SyntaxHighlighter
{
    // Палитра близка к CGA/VGA, как остальной TempleOS-стиль редактора.
    private static readonly Color NormalColor   = Color.FromRgb(0xFF, 0xFF, 0x55); // жёлтый (обычный текст)
    private static readonly Color KeywordColor  = Color.FromRgb(0xFF, 0xFF, 0xFF); // белый
    private static readonly Color StringColor   = Color.FromRgb(0x55, 0xFF, 0x55); // зелёный
    private static readonly Color CommentColor  = Color.FromRgb(0x00, 0xAA, 0xAA); // бирюзовый
    private static readonly Color NumberColor   = Color.FromRgb(0x55, 0xFF, 0xFF); // светло-голубой
    private static readonly Color PreprocColor  = Color.FromRgb(0xFF, 0x55, 0xFF); // светло-пурпурный
    private static readonly Color TypeColor     = Color.FromRgb(0x85, 0x85, 0xFF); // светло-синий

    private static readonly SolidColorBrush NormalBrush   = Brush(NormalColor);
    private static readonly SolidColorBrush KeywordBrush  = Brush(KeywordColor);
    private static readonly SolidColorBrush StringBrush   = Brush(StringColor);
    private static readonly SolidColorBrush CommentBrush  = Brush(CommentColor);
    private static readonly SolidColorBrush NumberBrush   = Brush(NumberColor);
    private static readonly SolidColorBrush PreprocBrush  = Brush(PreprocColor);
    private static readonly SolidColorBrush TypeBrush     = Brush(TypeColor);

    private static readonly HashSet<string> Keywords = new(System.StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue",
        "return", "goto", "using", "public", "private", "protected", "internal", "static",
        "const", "volatile", "extern", "virtual", "override", "sealed", "abstract", "new",
        "delete", "this", "base", "try", "catch", "finally", "throw", "async", "await", "in",
        "out", "ref", "sizeof", "typeof", "operator", "explicit", "implicit", "is", "as",
        "get", "set", "value", "constexpr", "auto", "template", "typename", "friend",
        "true", "false", "null", "nullptr", "from", "select", "where", "var", "namespace",
    };

    private static readonly HashSet<string> Types = new(System.StringComparer.Ordinal)
    {
        "int", "char", "float", "double", "void", "bool", "byte", "short", "long", "size_t",
        "string", "wchar_t", "uint", "ulong", "ushort", "sbyte", "decimal", "object", "struct",
        "class", "enum", "union", "interface", "signed", "unsigned", "const_cast", "intptr_t",
        "uintptr_t", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t",
        "uint64_t", "wstring",
    };

    /// <summary>Считается ли файл кодом (поддерживается подсветка).</summary>
    public static bool IsCode(string extension) => extension is
        ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".cs";

    /// <summary>
    /// Заполняет TextBlock цветными фрагментами кода. Для очень больших файлов
    /// (чтобы не зависнуть на перерисовке) рисует весь текст одним обычным цветом.
    /// </summary>
    public static void Highlight(string text, TextBlock block)
    {
        block.Inlines.Clear();

        // Предел, выше которого подсветка не строит сотни тысяч Run — только для чтения не важно,
        // а при правке 6 МБ на каждый ввод это заморозило бы интерфейс.
        if (text.Length > 2_000_000)
        {
            block.Inlines.Add(new Run(text) { Foreground = NormalBrush });
            return;
        }

        var spans = Tokenize(text);

        int i = 0;
        foreach (var (start, length, color) in spans)
        {
            var run = new Run(text.Substring(start, length)) { Foreground = color };
            block.Inlines.Add(run);
            i = start + length;
        }

        // Неразмеченные «хвосты» (пробелы, знаки) — обычным цветом. Обычно это пустая строка.
        if (i < text.Length)
            block.Inlines.Add(new Run(text.Substring(i)) { Foreground = NormalBrush });
    }

    private readonly record struct Span(int Start, int Length, Brush Brush);

    private static List<Span> Tokenize(string text)
    {
        var spans = new List<Span>();
        int i = 0;
        int n = text.Length;
        int plainStart = 0;

        void FlushPlain(int upTo)
        {
            if (upTo > plainStart) spans.Add(new Span(plainStart, upTo - plainStart, NormalBrush));
        }

        while (i < n)
        {
            char c = text[i];

            // line comment // ...
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                int end = i + 2;
                while (end < n && text[end] != '\n') end++;
                FlushPlain(i);
                spans.Add(new Span(i, end - i, CommentBrush));
                i = end; plainStart = i;
                continue;
            }

            // block comment /* ... */
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, System.StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                FlushPlain(i);
                spans.Add(new Span(i, end - i, CommentBrush));
                i = end; plainStart = i;
                continue;
            }

            // regular string "..." (escapes)
            if (c == '"')
            {
                int end = ScanString(text, i);
                FlushPlain(i);
                spans.Add(new Span(i, end - i, StringBrush));
                i = end; plainStart = i;
                continue;
            }

            // verbatim string @"..."
            if (c == '@' && i + 1 < n && text[i + 1] == '"')
            {
                int end = ScanVerbatimString(text, i + 1);
                FlushPlain(i);
                spans.Add(new Span(i, end - i, StringBrush));
                i = end; plainStart = i;
                continue;
            }

            // char literal '...'
            if (c == '\'')
            {
                int end = ScanCharLiteral(text, i);
                FlushPlain(i);
                spans.Add(new Span(i, end - i, StringBrush));
                i = end; plainStart = i;
                continue;
            }

            // numbers
            if (char.IsDigit(c))
            {
                int end = ScanNumber(text, i);
                FlushPlain(i);
                spans.Add(new Span(i, end - i, NumberBrush));
                i = end; plainStart = i;
                continue;
            }

            // preprocessor / attribute line starting with #
            if (c == '#')
            {
                int end = i;
                while (end < n && text[end] != '\n') end++;
                FlushPlain(i);
                spans.Add(new Span(i, end - i, PreprocBrush));
                i = end; plainStart = i;
                continue;
            }

            // identifiers / keywords / types
            if (char.IsLetter(c) || c == '_')
            {
                int end = i + 1;
                while (end < n && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;

                string word = text.Substring(i, end - i);
                if (Keywords.Contains(word))
                {
                    FlushPlain(i);
                    spans.Add(new Span(i, end - i, KeywordBrush));
                    i = end; plainStart = i;
                    continue;
                }

                if (Types.Contains(word))
                {
                    FlushPlain(i);
                    spans.Add(new Span(i, end - i, TypeBrush));
                    i = end; plainStart = i;
                    continue;
                }

                // обычный идентификатор — часть «плоского» текста
                i = end;
                continue;
            }

            i++;
        }

        FlushPlain(n);
        return spans;
    }

    private static int ScanString(string text, int start) // start at opening "
    {
        int i = start + 1;
        int n = text.Length;
        while (i < n)
        {
            char ch = text[i];
            if (ch == '\\') { i += 2; continue; }
            if (ch == '"') { i++; break; }
            i++;
        }
        return i;
    }

    private static int ScanVerbatimString(string text, int start) // start at opening "
    {
        int i = start + 1;
        int n = text.Length;
        while (i < n)
        {
            if (text[i] == '"')
            {
                if (i + 1 < n && text[i + 1] == '"') { i += 2; continue; }
                i++; break;
            }
            i++;
        }
        return i;
    }

    private static int ScanCharLiteral(string text, int start) // start at opening '
    {
        int i = start + 1;
        int n = text.Length;
        while (i < n)
        {
            char ch = text[i];
            if (ch == '\\') { i += 2; continue; }
            if (ch == '\'') { i++; break; }
            i++;
        }
        return i;
    }

    private static int ScanNumber(string text, int start)
    {
        int i = start;
        int n = text.Length;

        if (text[i] == '0' && i + 1 < n && (text[i + 1] == 'x' || text[i + 1] == 'X'))
        {
            i += 2;
            while (i < n && IsHex(text[i])) i++;
        }
        else
        {
            while (i < n && char.IsDigit(text[i])) i++;
            if (i < n && text[i] == '.')
            {
                i++;
                while (i < n && char.IsDigit(text[i])) i++;
            }
            if (i < n && (text[i] == 'e' || text[i] == 'E' || text[i] == 'p' || text[i] == 'P'))
            {
                i++;
                if (i < n && (text[i] == '+' || text[i] == '-')) i++;
                while (i < n && char.IsDigit(text[i])) i++;
            }
        }

        // суффикс типа: u, l, f
        while (i < n && (text[i] is 'u' or 'U' or 'l' or 'L' or 'f' or 'F')) i++;

        return i;
    }

    private static bool IsHex(char c) => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}