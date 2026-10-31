using System.Text;

namespace TerminalExplorer.Terminal;

/// <summary>Атрибуты начертания ячейки.</summary>
[Flags]
public enum CellAttr : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strike = 64,
    /// <summary>Левая половина символа двойной ширины.</summary>
    Wide = 128,
    /// <summary>Правая половина символа двойной ширины — рисуется вместе с левой.</summary>
    Continuation = 256,
}

/// <summary>
/// Ячейка экрана. Цвет закодирован числом: <see cref="DefaultColor"/> — цвет по умолчанию
/// (берётся из темы), 0..255 — индекс палитры, <see cref="RgbFlag"/> | 0xRRGGBB — истинный цвет.
/// </summary>
public struct Cell
{
    public const int DefaultColor = -1;
    public const int RgbFlag = 0x1000000;

    public int Codepoint;
    public int Fg;
    public int Bg;
    public CellAttr Attr;

    public static Cell Blank(int bg = DefaultColor) =>
        new() { Codepoint = ' ', Fg = DefaultColor, Bg = bg };
}

/// <summary>Строка экрана или истории. Wrapped — продолжается на следующей (нужно для копирования).</summary>
public sealed class Row
{
    public Cell[] Cells;
    public bool Wrapped;

    public Row(int columns, int bg = Cell.DefaultColor)
    {
        Cells = new Cell[columns];
        Array.Fill(Cells, Cell.Blank(bg));
    }

    public Row Clone() => new(0) { Cells = (Cell[])Cells.Clone(), Wrapped = Wrapped };
}

/// <summary>
/// Эмулятор терминала: буфер экрана с историей и разбор потока VT-последовательностей.
///
/// Покрывает то, что реально выдаёт ConPTY и ожидают консольные программы: SGR (16/256/true color),
/// перемещение курсора, области прокрутки, вставку/удаление строк и символов, альтернативный
/// экран (vim, less, mc), режимы курсорных клавиш, мыши и вставки в скобках, ответы на запросы
/// положения курсора. Всё остальное молча пропускается — лучше проигнорировать неизвестное,
/// чем показать мусор.
///
/// Разбор идёт в потоке чтения, отрисовка — в потоке интерфейса, поэтому доступ к состоянию
/// ограничен блокировкой <see cref="Sync"/>.
/// </summary>
public sealed class TerminalScreen
{
    public const int MaxScrollback = 8000;

    public readonly object Sync = new();

    private Row[] _main;
    private Row[] _alt;
    private Row[] _lines;
    private bool _usingAlt;

    private readonly List<Row> _scrollback = [];

    private int _cursorRow;
    private int _cursorCol;
    private bool _pendingWrap;

    private int _scrollTop;
    private int _scrollBottom;

    private int _fg = Cell.DefaultColor;
    private int _bg = Cell.DefaultColor;
    private CellAttr _attr;

    private int _savedRow, _savedCol, _savedFg = Cell.DefaultColor, _savedBg = Cell.DefaultColor;
    private CellAttr _savedAttr;
    private int _altSavedRow, _altSavedCol;

    private bool[] _tabStops;

    // Состояние разбора.
    private enum ParseState { Ground, Escape, EscapeIntermediate, Csi, Osc, OscEscape, StringIgnore, StringIgnoreEscape }

    private ParseState _state = ParseState.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _osc = new();
    private char _csiPrefix;
    private char _csiIntermediate;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private int _highSurrogate;
    private int _lastPrinted;

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool UsingAlternateScreen => _usingAlt;

    /// <summary>0 — мышь не отслеживается, 1000 — нажатия, 1002 — с перетаскиванием, 1003 — любое движение.</summary>
    public int MouseTracking { get; private set; }
    public bool MouseSgr { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>Рабочая папка оболочки, если та сообщает её через OSC 9;9 или OSC 7.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>Растёт при каждом изменении содержимого — по нему вид понимает, что пора перерисоваться.</summary>
    public long Version { get; private set; }

    public int ScrollbackCount => _scrollback.Count;

    /// <summary>Ответы на запросы (положение курсора и т.п.) — отправляются обратно в оболочку.</summary>
    public event Action<string>? Reply;

    public event Action? TitleChanged;
    public event Action<string>? WorkingDirectoryChanged;
    public event Action? Bell;

    /// <summary>Оболочка умеет менять папку скрыто (через файл); аргумент — её PID.</summary>
    public event Action<int>? SilentSyncReady;

    public TerminalScreen(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(1, rows);

        _main = NewBuffer();
        _alt = NewBuffer();
        _lines = _main;
        _tabStops = NewTabStops();
        _scrollBottom = Rows - 1;
    }

    private Row[] NewBuffer()
    {
        var buffer = new Row[Rows];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = new Row(Columns);
        return buffer;
    }

    private bool[] NewTabStops()
    {
        var stops = new bool[Columns];
        for (int i = 8; i < Columns; i += 8) stops[i] = true;
        return stops;
    }

    // ---------- доступ для отрисовки ----------

    /// <summary>
    /// Строка в «абсолютных» координатах: 0 — самая старая строка истории,
    /// ScrollbackCount — первая строка экрана. Вне диапазона — null.
    /// </summary>
    public Row? GetAbsoluteRow(int index)
    {
        if (index < 0) return null;
        if (index < _scrollback.Count) return _scrollback[index];

        int line = index - _scrollback.Count;
        return line < _lines.Length ? _lines[line] : null;
    }

    public int TotalRows => _scrollback.Count + Rows;

    // ---------- ввод потока ----------

    /// <summary>Разбирает порцию байтов из вывода оболочки.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length + 4];
        int count = _decoder.GetChars(bytes, chars, flush: false);

        lock (Sync)
        {
            for (int i = 0; i < count; i++)
            {
                char c = chars[i];

                if (char.IsHighSurrogate(c)) { _highSurrogate = c; continue; }

                if (char.IsLowSurrogate(c) && _highSurrogate != 0)
                {
                    Process(char.ConvertToUtf32((char)_highSurrogate, c));
                    _highSurrogate = 0;
                    continue;
                }

                _highSurrogate = 0;
                Process(c);
            }

            Version++;
        }
    }

    /// <summary>Подгружает сохранённый текст в историю — «воспоминание» о прошлом сеансе.</summary>
    public void PreloadHistory(IEnumerable<string> lines)
    {
        lock (Sync)
        {
            foreach (string line in lines)
            {
                var row = new Row(Columns);
                int column = 0;

                foreach (Rune rune in line.EnumerateRunes())
                {
                    if (column >= Columns) break;

                    row.Cells[column++] = new Cell
                    {
                        Codepoint = rune.Value, Fg = Cell.DefaultColor, Bg = Cell.DefaultColor, Attr = CellAttr.Dim,
                    };
                }

                _scrollback.Add(row);
            }

            TrimScrollback();
            Version++;
        }
    }

    /// <summary>Текст истории и экрана для сохранения между запусками.</summary>
    public List<string> SnapshotText(int maxLines)
    {
        lock (Sync)
        {
            var result = new List<string>();

            foreach (Row row in _scrollback) result.Add(RowText(row));
            foreach (Row row in _main) result.Add(RowText(row));

            while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);

            if (result.Count > maxLines) result.RemoveRange(0, result.Count - maxLines);
            return result;
        }
    }

    private static string RowText(Row row) => RowText(row, 0, row.Cells.Length);

    public static string RowText(Row row, int from, int to)
    {
        var text = new StringBuilder();
        to = Math.Min(to, row.Cells.Length);

        for (int i = Math.Max(0, from); i < to; i++)
        {
            ref Cell cell = ref row.Cells[i];
            if ((cell.Attr & CellAttr.Continuation) != 0) continue;

            text.Append(cell.Codepoint <= 0 ? ' ' : char.ConvertFromUtf32(cell.Codepoint));
        }

        return text.ToString().TrimEnd();
    }

    // ---------- конечный автомат ----------

    private void Process(int c)
    {
        switch (_state)
        {
            case ParseState.Ground:
                Ground(c);
                break;

            case ParseState.Escape:
                EscapeByte(c);
                break;

            case ParseState.EscapeIntermediate:
                // ESC ( B, ESC ) 0 и подобное — выбор набора символов. ConPTY сам переводит
                // псевдографику в Unicode, так что просто съедаем завершающий байт.
                _state = ParseState.Ground;
                break;

            case ParseState.Csi:
                CsiByte(c);
                break;

            case ParseState.Osc:
                if (c == 0x07) { EndOsc(); }
                else if (c == 0x1B) _state = ParseState.OscEscape;
                else if (_osc.Length < 4096) _osc.Append((char)c);
                break;

            case ParseState.OscEscape:
                if (c == '\\') EndOsc();
                else { _state = ParseState.Ground; EscapeByte(c); }
                break;

            case ParseState.StringIgnore:
                if (c == 0x07) _state = ParseState.Ground;
                else if (c == 0x1B) _state = ParseState.StringIgnoreEscape;
                break;

            case ParseState.StringIgnoreEscape:
                _state = c == '\\' ? ParseState.Ground : ParseState.StringIgnore;
                break;
        }
    }

    private void Ground(int c)
    {
        switch (c)
        {
            case 0x1B: _state = ParseState.Escape; return;
            case 0x07: Bell?.Invoke(); return;
            case 0x08: Backspace(); return;
            case 0x09: Tab(); return;
            case 0x0A or 0x0B or 0x0C: LineFeed(); return;
            case 0x0D: _cursorCol = 0; _pendingWrap = false; return;
            case < 0x20 or 0x7F: return;
            case >= 0x80 and < 0xA0: return;
        }

        Print(c);
    }

    private void EscapeByte(int c)
    {
        _state = ParseState.Ground;

        switch (c)
        {
            case '[':
                _params.Clear();
                _csiPrefix = '\0';
                _csiIntermediate = '\0';
                _state = ParseState.Csi;
                break;

            case ']':
                _osc.Clear();
                _state = ParseState.Osc;
                break;

            case 'P' or 'X' or '^' or '_':
                _state = ParseState.StringIgnore;
                break;

            case '(' or ')' or '*' or '+' or '#' or ' ' or '%':
                _state = ParseState.EscapeIntermediate;
                break;

            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E': _cursorCol = 0; _pendingWrap = false; LineFeed(); break;
            case 'M': ReverseIndex(); break;
            case 'H': if (_cursorCol < _tabStops.Length) _tabStops[_cursorCol] = true; break;
            case 'c': FullReset(); break;
            case 0x1B: _state = ParseState.Escape; break;
        }
    }

    private void CsiByte(int c)
    {
        // Параметры: цифры, ';' и ':'. Префиксы <=>? допустимы только в начале.
        if (c is >= '0' and <= '9' or ';' or ':')
        {
            if (_params.Length < 256) _params.Append((char)c);
            return;
        }

        if (c is '?' or '>' or '<' or '=' && _params.Length == 0 && _csiPrefix == '\0')
        {
            _csiPrefix = (char)c;
            return;
        }

        if (c is >= 0x20 and <= 0x2F)
        {
            _csiIntermediate = (char)c;
            return;
        }

        if (c == 0x1B) { _state = ParseState.Escape; return; }

        if (c is >= 0x40 and <= 0x7E)
        {
            _state = ParseState.Ground;
            DispatchCsi((char)c);
            return;
        }

        // Управляющие символы внутри последовательности исполняются, не прерывая её.
        if (c < 0x20) Ground(c);
    }

    private void EndOsc()
    {
        _state = ParseState.Ground;
        string text = _osc.ToString();
        int separator = text.IndexOf(';');
        string command = separator < 0 ? text : text[..separator];
        string argument = separator < 0 ? string.Empty : text[(separator + 1)..];

        switch (command)
        {
            case "0" or "2":
                Title = argument;
                TitleChanged?.Invoke();
                break;

            // OSC 9;9;"путь" — соглашение Windows Terminal: оболочка сообщает рабочую папку.
            case "9" when argument.StartsWith("9;"):
                SetWorkingDirectory(argument[2..].Trim('"'));
                break;

            // OSC 1337;TerminalExplorer=silent;PID — собственное сообщение нашего приглашения PowerShell.
            case "1337" when argument.StartsWith("TerminalExplorer=silent;"):
                if (int.TryParse(argument["TerminalExplorer=silent;".Length..], out int shellPid)) SilentSyncReady?.Invoke(shellPid);
                break;

            // OSC 7;file://host/путь — то же самое в формате URI.
            case "7":
                if (Uri.TryCreate(argument, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                    SetWorkingDirectory(uri.LocalPath);
                break;
        }
    }

    private void SetWorkingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        WorkingDirectory = path;
        WorkingDirectoryChanged?.Invoke(path);
    }

    // ---------- параметры CSI ----------

    private int[] ParseParams(out char[] separators)
    {
        if (_params.Length == 0)
        {
            separators = [];
            return [];
        }

        var values = new List<int>();
        var seps = new List<char>();
        int current = -1;

        foreach (char c in _params.ToString())
        {
            if (c is ';' or ':')
            {
                values.Add(current);
                seps.Add(c);
                current = -1;
            }
            else
            {
                current = (current < 0 ? 0 : current) * 10 + (c - '0');
                if (current > 65535) current = 65535;
            }
        }

        values.Add(current);
        separators = [.. seps];
        return [.. values];
    }

    private static int Param(int[] values, int index, int fallback)
    {
        if (index >= values.Length || values[index] < 0) return fallback;
        return values[index];
    }

    private static int ParamNonZero(int[] values, int index, int fallback)
    {
        int value = Param(values, index, fallback);
        return value == 0 ? fallback : value;
    }

    private void DispatchCsi(char final)
    {
        int[] p = ParseParams(out char[] separators);

        if (_csiPrefix == '?')
        {
            if (final is 'h' or 'l')
                foreach (int mode in p) SetPrivateMode(mode, final == 'h');

            return;
        }

        if (_csiPrefix != '\0')
        {
            // CSI > c — запрос вторичных атрибутов.
            if (_csiPrefix == '>' && final == 'c') Reply?.Invoke("\x1b[>0;10;1c");
            return;
        }

        if (_csiIntermediate != '\0') return; // курсорные стили, DECSTR и подобное

        switch (final)
        {
            case 'A': MoveCursor(-ParamNonZero(p, 0, 1), 0); break;
            case 'B' or 'e': MoveCursor(ParamNonZero(p, 0, 1), 0); break;
            case 'C' or 'a': MoveCursor(0, ParamNonZero(p, 0, 1)); break;
            case 'D': MoveCursor(0, -ParamNonZero(p, 0, 1)); break;
            case 'E': MoveCursor(ParamNonZero(p, 0, 1), 0); _cursorCol = 0; break;
            case 'F': MoveCursor(-ParamNonZero(p, 0, 1), 0); _cursorCol = 0; break;
            case 'G' or '`': SetCursor(_cursorRow, ParamNonZero(p, 0, 1) - 1); break;
            case 'd': SetCursor(ParamNonZero(p, 0, 1) - 1, _cursorCol); break;
            case 'H' or 'f': SetCursor(ParamNonZero(p, 0, 1) - 1, ParamNonZero(p, 1, 1) - 1); break;
            case 'I': for (int n = ParamNonZero(p, 0, 1); n > 0; n--) Tab(); break;
            case 'Z': BackTab(ParamNonZero(p, 0, 1)); break;
            case 'J': EraseInDisplay(Param(p, 0, 0)); break;
            case 'K': EraseInLine(Param(p, 0, 0)); break;
            case 'L': InsertLines(ParamNonZero(p, 0, 1)); break;
            case 'M': DeleteLines(ParamNonZero(p, 0, 1)); break;
            case '@': InsertChars(ParamNonZero(p, 0, 1)); break;
            case 'P': DeleteChars(ParamNonZero(p, 0, 1)); break;
            case 'X': EraseChars(ParamNonZero(p, 0, 1)); break;
            case 'S': ScrollUp(ParamNonZero(p, 0, 1)); break;
            case 'T': ScrollDown(ParamNonZero(p, 0, 1)); break;
            case 'b': for (int n = ParamNonZero(p, 0, 1); n > 0 && _lastPrinted != 0; n--) Print(_lastPrinted); break;
            case 'm': Sgr(p, separators); break;
            case 'r': SetScrollRegion(ParamNonZero(p, 0, 1), ParamNonZero(p, 1, Rows)); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'g': if (Param(p, 0, 0) == 3) Array.Clear(_tabStops); else if (_cursorCol < _tabStops.Length) _tabStops[_cursorCol] = false; break;
            case 'c': if (Param(p, 0, 0) == 0) Reply?.Invoke("\x1b[?1;0c"); break;
            case 'n':
                // ConPTY при старте спрашивает положение курсора и ждёт ответа.
                if (Param(p, 0, 0) == 6) Reply?.Invoke($"\x1b[{_cursorRow + 1};{_cursorCol + 1}R");
                else if (Param(p, 0, 0) == 5) Reply?.Invoke("\x1b[0n");
                break;
        }
    }

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 7: AutoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 1000 or 1002 or 1003:
                MouseTracking = on ? mode : 0;
                break;
            case 1006: MouseSgr = on; break;
            case 2004: BracketedPaste = on; break;
            case 47 or 1047: SwitchScreen(on, saveCursor: false); break;
            case 1049: SwitchScreen(on, saveCursor: true); break;
        }
    }

    // ---------- SGR ----------

    private void Sgr(int[] p, char[] separators)
    {
        if (p.Length == 0) { ResetAttributes(); return; }

        for (int i = 0; i < p.Length; i++)
        {
            int code = p[i] < 0 ? 0 : p[i];

            switch (code)
            {
                case 0: ResetAttributes(); break;
                case 1: _attr |= CellAttr.Bold; break;
                case 2: _attr |= CellAttr.Dim; break;
                case 3: _attr |= CellAttr.Italic; break;
                case 4 or 21: _attr |= CellAttr.Underline; break;
                case 5 or 6: break;
                case 7: _attr |= CellAttr.Inverse; break;
                case 8: _attr |= CellAttr.Hidden; break;
                case 9: _attr |= CellAttr.Strike; break;
                case 22: _attr &= ~(CellAttr.Bold | CellAttr.Dim); break;
                case 23: _attr &= ~CellAttr.Italic; break;
                case 24: _attr &= ~CellAttr.Underline; break;
                case 25: break;
                case 27: _attr &= ~CellAttr.Inverse; break;
                case 28: _attr &= ~CellAttr.Hidden; break;
                case 29: _attr &= ~CellAttr.Strike; break;
                case >= 30 and <= 37: _fg = code - 30; break;
                case 39: _fg = Cell.DefaultColor; break;
                case >= 40 and <= 47: _bg = code - 40; break;
                case 49: _bg = Cell.DefaultColor; break;
                case >= 90 and <= 97: _fg = code - 90 + 8; break;
                case >= 100 and <= 107: _bg = code - 100 + 8; break;
                case 38 or 48:
                    i = ExtendedColor(p, separators, i, code == 38);
                    break;
            }
        }
    }

    /// <summary>
    /// 38;5;n / 38;2;r;g;b и их двоеточечные варианты (38:2::r:g:b). Возвращает индекс
    /// последнего потреблённого параметра.
    /// </summary>
    private int ExtendedColor(int[] p, char[] separators, int index, bool foreground)
    {
        int mode = Param(p, index + 1, -1);
        int color;
        int consumed;

        if (mode == 5)
        {
            color = Math.Clamp(Param(p, index + 2, 0), 0, 255);
            consumed = 2;
        }
        else if (mode == 2)
        {
            // В двоеточечной форме перед компонентами стоит необязательный идентификатор
            // цветового пространства: 38:2::r:g:b.
            bool colon = index < separators.Length && separators[index] == ':';
            int offset = colon && p.Length - index - 2 >= 4 ? 3 : 2;

            int r = Math.Clamp(Param(p, index + offset, 0), 0, 255);
            int g = Math.Clamp(Param(p, index + offset + 1, 0), 0, 255);
            int b = Math.Clamp(Param(p, index + offset + 2, 0), 0, 255);

            color = Cell.RgbFlag | (r << 16) | (g << 8) | b;
            consumed = offset + 2;
        }
        else
        {
            return index;
        }

        if (foreground) _fg = color; else _bg = color;
        return index + consumed;
    }

    private void ResetAttributes()
    {
        _fg = Cell.DefaultColor;
        _bg = Cell.DefaultColor;
        _attr = CellAttr.None;
    }

    // ---------- печать ----------

    private static int Width(int codepoint)
    {
        if (codepoint < 0x300) return 1;

        // Комбинирующие и нулевой ширины — не занимают ячейку.
        if (codepoint is (>= 0x300 and <= 0x36F) or (>= 0x200B and <= 0x200F) or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF)
            return 0;

        bool wide =
            codepoint is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3)
                or (>= 0xF900 and <= 0xFAFF) or (>= 0xFE30 and <= 0xFE6F) or (>= 0xFF00 and <= 0xFF60)
                or (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1F300 and <= 0x1F64F) or (>= 0x1F900 and <= 0x1F9FF)
                or (>= 0x20000 and <= 0x3FFFD);

        return wide ? 2 : 1;
    }

    private void Print(int codepoint)
    {
        int width = Width(codepoint);
        if (width == 0) return;

        if (_pendingWrap || _cursorCol + width > Columns)
        {
            if (AutoWrap)
            {
                _lines[_cursorRow].Wrapped = true;
                _cursorCol = 0;
                LineFeed();
            }
            else
            {
                _cursorCol = Columns - width;
            }
        }

        _pendingWrap = false;
        Row row = _lines[_cursorRow];

        // Затирание половины двойного символа оставило бы «осиротевшую» половину.
        ClearWideOverlap(row, _cursorCol);
        if (width == 2) ClearWideOverlap(row, _cursorCol + 1);

        row.Cells[_cursorCol] = new Cell
        {
            Codepoint = codepoint, Fg = _fg, Bg = _bg,
            Attr = _attr | (width == 2 ? CellAttr.Wide : CellAttr.None),
        };

        if (width == 2)
        {
            row.Cells[_cursorCol + 1] = new Cell
            {
                Codepoint = ' ', Fg = _fg, Bg = _bg, Attr = _attr | CellAttr.Continuation,
            };
        }

        _lastPrinted = codepoint;
        _cursorCol += width;

        if (_cursorCol >= Columns)
        {
            _cursorCol = Columns - 1;
            _pendingWrap = true;
        }
    }

    private static void ClearWideOverlap(Row row, int column)
    {
        if (column < 0 || column >= row.Cells.Length) return;

        ref Cell cell = ref row.Cells[column];

        if ((cell.Attr & CellAttr.Continuation) != 0 && column > 0)
            row.Cells[column - 1] = Cell.Blank();
        else if ((cell.Attr & CellAttr.Wide) != 0 && column + 1 < row.Cells.Length)
            row.Cells[column + 1] = Cell.Blank();
    }

    // ---------- курсор ----------

    private void Backspace()
    {
        _pendingWrap = false;
        if (_cursorCol > 0) _cursorCol--;
    }

    private void Tab()
    {
        _pendingWrap = false;

        do _cursorCol++;
        while (_cursorCol < Columns - 1 && !_tabStops[_cursorCol]);

        if (_cursorCol >= Columns) _cursorCol = Columns - 1;
    }

    private void BackTab(int count)
    {
        _pendingWrap = false;

        for (; count > 0; count--)
        {
            do _cursorCol--;
            while (_cursorCol > 0 && !_tabStops[_cursorCol]);

            if (_cursorCol < 0) _cursorCol = 0;
        }
    }

    private void LineFeed()
    {
        _pendingWrap = false;

        if (_cursorRow == _scrollBottom) ScrollUp(1);
        else if (_cursorRow < Rows - 1) _cursorRow++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;

        if (_cursorRow == _scrollTop) ScrollDown(1);
        else if (_cursorRow > 0) _cursorRow--;
    }

    private void MoveCursor(int rows, int columns)
    {
        _pendingWrap = false;
        _cursorRow = Math.Clamp(_cursorRow + rows, 0, Rows - 1);
        _cursorCol = Math.Clamp(_cursorCol + columns, 0, Columns - 1);
    }

    private void SetCursor(int row, int column)
    {
        _pendingWrap = false;
        _cursorRow = Math.Clamp(row, 0, Rows - 1);
        _cursorCol = Math.Clamp(column, 0, Columns - 1);
    }

    private void SaveCursor()
    {
        _savedRow = _cursorRow;
        _savedCol = _cursorCol;
        _savedFg = _fg;
        _savedBg = _bg;
        _savedAttr = _attr;
    }

    private void RestoreCursor()
    {
        SetCursor(_savedRow, _savedCol);
        _fg = _savedFg;
        _bg = _savedBg;
        _attr = _savedAttr;
    }

    // ---------- прокрутка и стирание ----------

    private void SetScrollRegion(int top, int bottom)
    {
        top = Math.Clamp(top - 1, 0, Rows - 1);
        bottom = Math.Clamp(bottom - 1, 0, Rows - 1);

        if (top >= bottom)
        {
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
        }
        else
        {
            _scrollTop = top;
            _scrollBottom = bottom;
        }

        SetCursor(0, 0);
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);

        for (; count > 0; count--)
        {
            Row removed = _lines[_scrollTop];

            // В историю попадает только то, что ушло с верха основного экрана целиком:
            // область прокрутки (панели vim, строка состояния) историей не считается.
            if (!_usingAlt && _scrollTop == 0)
            {
                _scrollback.Add(removed);
                TrimScrollback();
            }

            for (int i = _scrollTop; i < _scrollBottom; i++) _lines[i] = _lines[i + 1];
            _lines[_scrollBottom] = new Row(Columns, _bg);
        }
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);

        for (; count > 0; count--)
        {
            for (int i = _scrollBottom; i > _scrollTop; i--) _lines[i] = _lines[i - 1];
            _lines[_scrollTop] = new Row(Columns, _bg);
        }
    }

    private void TrimScrollback()
    {
        if (_scrollback.Count > MaxScrollback + 500)
            _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
    }

    private void InsertLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom) return;

        _pendingWrap = false;
        count = Math.Min(count, _scrollBottom - _cursorRow + 1);

        for (; count > 0; count--)
        {
            for (int i = _scrollBottom; i > _cursorRow; i--) _lines[i] = _lines[i - 1];
            _lines[_cursorRow] = new Row(Columns, _bg);
        }

        _cursorCol = 0;
    }

    private void DeleteLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom) return;

        _pendingWrap = false;
        count = Math.Min(count, _scrollBottom - _cursorRow + 1);

        for (; count > 0; count--)
        {
            for (int i = _cursorRow; i < _scrollBottom; i++) _lines[i] = _lines[i + 1];
            _lines[_scrollBottom] = new Row(Columns, _bg);
        }

        _cursorCol = 0;
    }

    private void InsertChars(int count)
    {
        _pendingWrap = false;
        Cell[] cells = _lines[_cursorRow].Cells;
        count = Math.Min(count, Columns - _cursorCol);

        for (int i = Columns - 1; i >= _cursorCol + count; i--) cells[i] = cells[i - count];
        for (int i = _cursorCol; i < _cursorCol + count; i++) cells[i] = Cell.Blank(_bg);
    }

    private void DeleteChars(int count)
    {
        _pendingWrap = false;
        Cell[] cells = _lines[_cursorRow].Cells;
        count = Math.Min(count, Columns - _cursorCol);

        for (int i = _cursorCol; i < Columns - count; i++) cells[i] = cells[i + count];
        for (int i = Columns - count; i < Columns; i++) cells[i] = Cell.Blank(_bg);
    }

    private void EraseChars(int count)
    {
        _pendingWrap = false;
        Cell[] cells = _lines[_cursorRow].Cells;
        int end = Math.Min(Columns, _cursorCol + count);

        for (int i = _cursorCol; i < end; i++) cells[i] = Cell.Blank(_bg);
    }

    private void EraseInLine(int mode)
    {
        Row row = _lines[_cursorRow];
        int from = mode == 0 ? _cursorCol : 0;
        int to = mode == 1 ? _cursorCol + 1 : Columns;

        for (int i = from; i < Math.Min(to, Columns); i++) row.Cells[i] = Cell.Blank(_bg);
        if (mode != 1) row.Wrapped = false;
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int i = _cursorRow + 1; i < Rows; i++) _lines[i] = new Row(Columns, _bg);
                break;

            case 1:
                EraseInLine(1);
                for (int i = 0; i < _cursorRow; i++) _lines[i] = new Row(Columns, _bg);
                break;

            case 2:
                for (int i = 0; i < Rows; i++) _lines[i] = new Row(Columns, _bg);
                break;

            case 3:
                _scrollback.Clear();
                break;
        }
    }

    // ---------- экраны ----------

    private void SwitchScreen(bool alt, bool saveCursor)
    {
        if (alt == _usingAlt) return;

        _pendingWrap = false;

        if (alt)
        {
            if (saveCursor) { _altSavedRow = _cursorRow; _altSavedCol = _cursorCol; }

            _usingAlt = true;
            _lines = _alt;
            for (int i = 0; i < Rows; i++) _lines[i] = new Row(Columns);
        }
        else
        {
            _usingAlt = false;
            _lines = _main;

            if (saveCursor) SetCursor(_altSavedRow, _altSavedCol);
        }

        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    private void FullReset()
    {
        _usingAlt = false;
        _lines = _main;
        for (int i = 0; i < Rows; i++) _lines[i] = new Row(Columns);

        ResetAttributes();
        _cursorRow = _cursorCol = 0;
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CursorVisible = true;
        ApplicationCursorKeys = false;
        BracketedPaste = false;
        MouseTracking = 0;
        MouseSgr = false;
        AutoWrap = true;
        _tabStops = NewTabStops();
    }

    // ---------- размер ----------

    /// <summary>
    /// Меняет размер экрана. Переноса строк (reflow) нет сознательно: ConPTY сам перерисовывает
    /// видимую область после изменения размера, а наш собственный перенос вступал бы с ним в конфликт.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);

        lock (Sync)
        {
            if (columns == Columns && rows == Rows) return;

            _main = ResizeBuffer(_main, columns, rows, pushToHistory: true);
            _alt = ResizeBuffer(_alt, columns, rows, pushToHistory: false);

            foreach (Row row in _scrollback)
            {
                if (row.Cells.Length < columns)
                {
                    int old = row.Cells.Length;
                    Array.Resize(ref row.Cells, columns);
                    Array.Fill(row.Cells, Cell.Blank(), old, columns - old);
                }
            }

            Columns = columns;
            Rows = rows;
            _lines = _usingAlt ? _alt : _main;

            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            _cursorRow = Math.Clamp(_cursorRow, 0, Rows - 1);
            _cursorCol = Math.Clamp(_cursorCol, 0, Columns - 1);
            _pendingWrap = false;
            _tabStops = NewTabStops();
            Version++;
        }
    }

    private Row[] ResizeBuffer(Row[] buffer, int columns, int rows, bool pushToHistory)
    {
        var result = new Row[rows];
        int shrink = Math.Max(0, buffer.Length - rows);

        // Лишние строки сверху уходят в историю, пока курсор ниже новой границы.
        int drop = pushToHistory && !ReferenceEquals(buffer, _alt) ? Math.Min(shrink, Math.Max(0, _cursorRow - rows + 1)) : 0;

        for (int i = 0; i < drop; i++) _scrollback.Add(buffer[i]);
        if (drop > 0) _cursorRow -= drop;

        for (int i = 0; i < rows; i++)
        {
            int source = i + drop;

            if (source < buffer.Length)
            {
                Row row = buffer[source];

                if (row.Cells.Length != columns)
                {
                    int old = row.Cells.Length;
                    Array.Resize(ref row.Cells, columns);
                    if (columns > old) Array.Fill(row.Cells, Cell.Blank(), old, columns - old);
                }

                result[i] = row;
            }
            else
            {
                result[i] = new Row(columns);
            }
        }

        return result;
    }

    // ---------- выделенный текст ----------

    /// <summary>
    /// Текст между двумя точками в абсолютных координатах (включительно). Мягкие переносы
    /// не превращаются в разрывы строк — иначе скопированная длинная команда рассыпалась бы.
    /// </summary>
    public string GetText((int Row, int Col) start, (int Row, int Col) end)
    {
        lock (Sync)
        {
            if (start.Row > end.Row || (start.Row == end.Row && start.Col > end.Col))
                (start, end) = (end, start);

            var text = new StringBuilder();

            for (int r = start.Row; r <= end.Row; r++)
            {
                Row? row = GetAbsoluteRow(r);
                if (row is null) continue;

                int from = r == start.Row ? start.Col : 0;
                int to = r == end.Row ? end.Col + 1 : row.Cells.Length;

                string line = RowText(row, from, to);
                text.Append(line);

                if (r < end.Row && !row.Wrapped) text.Append("\r\n");
            }

            return text.ToString();
        }
    }
}
