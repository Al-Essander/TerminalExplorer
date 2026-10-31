using System.Diagnostics;

namespace TerminalExplorer.Core;

/// <summary>
/// Лог процесса с двумя скоростями подачи.
///
/// Постоянно видна одна строка состояния, обновляемая НЕ ЧАЩЕ ThrottleMs. Это не
/// косметика: миллион записей MFT — это миллион событий, и если отчитываться о каждом,
/// вывод отчёта съест больше времени, чем сама индексация.
///
/// Полный лог при этом пишется всегда — в кольцевой буфер — и показывается по требованию.
/// В оболочке этому соответствует раскрываемая консоль; здесь, в прототипе, команда "log".
/// </summary>
public static class Log
{
    private const int Capacity = 4096;
    private const int ThrottleMs = 100;

    private static readonly string[] Buffer = new string[Capacity];
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lock Sync = new();

    private static int _written;
    private static long _lastProgressMs = -ThrottleMs;
    private static int _progressLength;

    /// <summary>Записывает строку в лог и печатает её. Для редких, значимых событий.</summary>
    public static void Line(string message)
    {
        string stamped = "[" + (Clock.ElapsedMilliseconds / 1000.0).ToString("00.00") + "] " + message;

        lock (Sync)
        {
            Buffer[_written++ % Capacity] = stamped;
            ClearProgress();
            Console.WriteLine(stamped);
        }
    }

    /// <summary>
    /// Обновляет строку состояния. Вызывать можно хоть на каждой записи: лишние вызовы
    /// отбрасываются по таймеру, поэтому стоимость вызова — одно сравнение.
    /// </summary>
    public static void Progress(Func<string> message)
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - _lastProgressMs < ThrottleMs) return;

        lock (Sync)
        {
            if (now - _lastProgressMs < ThrottleMs) return;
            _lastProgressMs = now;

            string text = message();
            ClearProgress();
            Console.Write(text);
            _progressLength = text.Length;
        }
    }

    /// <summary>Убирает строку состояния, чтобы обычный вывод её не затирал частично.</summary>
    public static void ClearProgress()
    {
        if (_progressLength == 0) return;

        Console.Write('\r');
        Console.Write(new string(' ', _progressLength));
        Console.Write('\r');
        _progressLength = 0;
    }

    /// <summary>Печатает последние n строк накопленного лога.</summary>
    public static void Dump(int lines = 40)
    {
        lock (Sync)
        {
            ClearProgress();

            int available = Math.Min(_written, Capacity);
            int start = Math.Max(0, available - lines);

            for (int i = start; i < available; i++)
            {
                int slot = _written <= Capacity ? i : (_written - available + i) % Capacity;
                Console.WriteLine("  " + Buffer[slot]);
            }
        }
    }
}
