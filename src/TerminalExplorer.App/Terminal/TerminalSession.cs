using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace TerminalExplorer.Terminal;

/// <summary>Какая оболочка запущена — от этого зависят команда смены папки и способ сообщить папку нам.</summary>
public enum ShellKind { Cmd, PowerShell, Other }

/// <summary>
/// Один сеанс терминала: оболочка в псевдоконсоли, экран с историей и поток чтения.
///
/// Сеанс принадлежит вкладке и живёт, пока вкладка открыта, — в том числе когда её не видно.
/// Поэтому долгая команда (сборка, ping) продолжает работать при переключении на другую вкладку.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private ConPty? _pty;
    private Thread? _reader;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private int _redrawQueued;
    private volatile bool _disposed;

    public TerminalScreen Screen { get; private set; }

    public ShellKind Shell { get; private set; }
    public string ShellName { get; private set; } = string.Empty;

    /// <summary>Папка, в которой оболочка по её собственному сообщению находится сейчас.</summary>
    public string? ReportedDirectory { get; private set; }

    public bool HasExited { get; private set; }

    /// <summary>Оболочка принимает смену папки скрыто, без печати команды.</summary>
    public bool SilentSync => _silentPid != 0;

    private int _silentPid;

    /// <summary>Папка, которую мы сами отправили; её отчёт оболочки не должен гнать панель обратно.</summary>
    private string? _lastSent;

    public bool IsOwnReport(string path)
    {
        if (_lastSent is null ||
            !string.Equals(_lastSent.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return false;

        _lastSent = null;
        return true;
    }

    private long _sentAt;

    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("TERMINALEXPLORER_TRACE") == "1";

    /// <summary>Журнал задержек терминала: включается переменной среды TERMINALEXPLORER_TRACE=1.</summary>
    internal static void Trace(string message)
    {
        if (!TraceEnabled) return;

        try
        {
            File.AppendAllText(Path.Combine(SessionStore.DirectoryPath, "terminal-timing.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + "\n");
        }
        catch (IOException) { }
    }

    /// <summary>Содержимое изменилось — пора перерисовать. Вызывается в потоке интерфейса.</summary>
    public event Action? Changed;

    public event Action? Exited;
    public event Action<string>? DirectoryChanged;
    public event Action? TitleChanged;

    public TerminalSession(int columns, int rows)
    {
        Screen = new TerminalScreen(columns, rows);

        Screen.Reply += text => Send(text);
        Screen.SilentSyncReady += pid => _silentPid = pid;
        Screen.TitleChanged += () => _dispatcher.BeginInvoke(() => TitleChanged?.Invoke());
        Screen.WorkingDirectoryChanged += path => { Trace("OSC 9;9: " + path); _dispatcher.BeginInvoke(() =>
        {
            ReportedDirectory = path;
            DirectoryChanged?.Invoke(path);
        }); };
    }

    // ---------- запуск ----------

    /// <summary>
    /// Запускает оболочку в папке. Порядок поиска: настройка пользователя, затем PowerShell 7,
    /// Windows PowerShell и, в крайнем случае, cmd.exe — он есть всегда.
    /// </summary>
    public void Start(string directory, string preferredShell)
    {
        if (!Directory.Exists(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Exception? failure = null;

        foreach (string candidate in ShellCandidates(preferredShell))
        {
            try
            {
                string command = BuildCommandLine(candidate, out ShellKind kind);

                // Для cmd сообщение о папке зашито в PROMPT ($e — ESC, $P — папка). Переменная
                // выставляется только на время запуска, чтобы не протекать в другие программы.
                if (kind == ShellKind.Cmd)
                    Environment.SetEnvironmentVariable("PROMPT", "$e]9;9;$P$e\\$P$G");

                try
                {
                    _pty = ConPty.Start(command, directory, Screen.Columns, Screen.Rows);
                }
                finally
                {
                    if (kind == ShellKind.Cmd) Environment.SetEnvironmentVariable("PROMPT", null);
                }

                Shell = kind;
                ShellName = Path.GetFileNameWithoutExtension(candidate);

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "terminal-reader" };
                _reader.Start();
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                failure = ex;
            }
        }

        string reason = failure?.Message ?? "оболочка не найдена";
        Screen.Feed(Encoding.UTF8.GetBytes("\x1b[31mНе удалось запустить оболочку: " + reason + "\x1b[0m\r\n"));
        HasExited = true;
    }

    private static IEnumerable<string> ShellCandidates(string preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            string? found = Resolve(preferred);
            if (found is not null) yield return found;
        }

        foreach (string name in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            string? found = Resolve(name);
            if (found is not null) yield return found;
        }
    }

    /// <summary>Находит исполняемый файл по полному пути или в PATH.</summary>
    private static string? Resolve(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;

        foreach (string folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Командная строка с «вшитым» сообщением о рабочей папке: оболочка печатает OSC 9;9
    /// в каждом приглашении, и мы узнаём о смене папки без опроса процесса. Для PowerShell
    /// оборачивается уже загруженная функция prompt, поэтому темы вроде oh-my-posh не страдают.
    /// </summary>
    private static string BuildCommandLine(string executable, out ShellKind kind)
    {
        string name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();

        if (name is "pwsh" or "powershell")
        {
            kind = ShellKind.PowerShell;

            const string hook = """
                $global:__teOriginalPrompt = $function:prompt
                function global:prompt {
                  $e = [char]27
                  $p = & $global:__teOriginalPrompt
                  if ($PWD.Provider.Name -eq 'FileSystem') {
                    [Console]::Write("$e]9;9;`"$($PWD.ProviderPath)`"$e\")
                  }
                  $p
                }

                Import-Module PSReadLine -ErrorAction SilentlyContinue
                if ([Microsoft.PowerShell.PSConsoleReadLine].GetMethod('InvokePrompt')) {
                  $global:__teCdFile = Join-Path ([IO.Path]::GetTempPath()) "terminalexplorer-cd-$PID.txt"
                  $null = Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -Action {
                    if (Test-Path -LiteralPath $global:__teCdFile) {
                      $dir = (Get-Content -LiteralPath $global:__teCdFile -Raw).Trim()
                      Remove-Item -LiteralPath $global:__teCdFile -Force -ErrorAction SilentlyContinue
                      if ($dir -and (Test-Path -LiteralPath $dir -PathType Container)) {
                        Set-Location -LiteralPath $dir
                        [Microsoft.PowerShell.PSConsoleReadLine]::InvokePrompt()
                      }
                    }
                  }
                  [Console]::Write("$([char]27)]1337;TerminalExplorer=silent;$PID$([char]27)\")
                }
                """;

            // EncodedCommand — чтобы не воевать с вложенными кавычками командной строки.
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(hook));
            return $"\"{executable}\" -NoLogo -NoExit -EncodedCommand {encoded}";
        }

        if (name == "cmd")
        {
            kind = ShellKind.Cmd;
            return $"\"{executable}\"";
        }

        kind = ShellKind.Other;
        return $"\"{executable}\"";
    }

    // ---------- поток вывода ----------

    private void ReadLoop()
    {
        ConPty? pty = _pty;
        if (pty is null) return;

        var buffer = new byte[65536];

        try
        {
            while (!_disposed)
            {
                int count = pty.Output.Read(buffer, 0, buffer.Length);
                if (count <= 0) break;

                if (_sentAt != 0)
                {
                    Trace($"первый вывод через {System.Diagnostics.Stopwatch.GetElapsedTime(_sentAt).TotalMilliseconds:F0} мс ({count} байт)");
                    _sentAt = 0;
                }

                Screen.Feed(buffer.AsSpan(0, count));
                QueueRedraw();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Канал закрыт — оболочка завершилась или сеанс закрывают.
        }

        if (_disposed) return;

        HasExited = true;
        _dispatcher.BeginInvoke(() => Exited?.Invoke());
    }

    /// <summary>
    /// Склеивает всплески вывода в одну перерисовку. Без этого `cat` большого файла
    /// ставил бы в очередь интерфейса тысячи обновлений и окно переставало бы отзываться.
    /// </summary>
    private void QueueRedraw()
    {
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 1) return;

        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _redrawQueued, 0);
            Changed?.Invoke();
        });
    }

    // ---------- ввод ----------

    public void Send(string text)
    {
        if (_disposed || HasExited || text.Length == 0) return;
        _pty?.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int columns, int rows)
    {
        if (columns < 2 || rows < 1) return;
        if (columns == Screen.Columns && rows == Screen.Rows) return;

        Screen.Resize(columns, rows);
        _pty?.Resize(columns, rows);
    }

    // ---------- смена папки ----------

    /// <summary>
    /// Оболочка простаивает, если у неё нет дочерних процессов: тогда безопасно «печатать»
    /// в неё команду. Во время сборки или `ping` ввод ушёл бы не в приглашение, а в программу.
    /// </summary>
    public bool IsIdle
    {
        get
        {
            if (_pty is null || HasExited || Screen.UsingAlternateScreen) return false;
            return !ProcessTree.HasChildren(_pty.ProcessId);
        }
    }

    /// <summary>
    /// Отправляет в оболочку команду перехода в папку. Esc перед ней стирает недописанную
    /// пользователем строку, чтобы команда не склеилась с чужим вводом.
    /// </summary>
    public bool ChangeDirectory(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool idle = IsIdle;
        Trace($"ChangeDirectory: IsIdle={idle} за {watch.ElapsedMilliseconds} мс");
        if (!idle) return false;

        if (SilentSync && TrySendSilently(path)) return true;

        string command = Shell switch
        {
            ShellKind.Cmd => $"cd /d \"{path}\"",
            ShellKind.PowerShell => "Set-Location -LiteralPath '" + path.Replace("'", "''") + "'",
            _ => string.Empty,
        };

        if (command.Length == 0) return false;

        _sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Trace("send: " + command);
        Send("\x1b" + command + "\r");
        ReportedDirectory = path;
        return true;
    }

    /// <summary>Кладёт путь в файл, который оболочка заберёт в простое. Запись атомарна: оболочка не прочтёт половину.</summary>
    private bool TrySendSilently(string path)
    {
        try
        {
            string file = Path.Combine(Path.GetTempPath(), $"terminalexplorer-cd-{_silentPid}.txt");
            File.WriteAllText(file + ".tmp", path);
            File.Move(file + ".tmp", file, overwrite: true);

            _lastSent = path;
            ReportedDirectory = path;
            Trace("тихая смена папки: " + path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pty?.Dispose();
    }
}

/// <summary>Минимальный просмотр дерева процессов через Toolhelp.</summary>
internal static class ProcessTree
{
    private const uint SnapshotProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public int dwSize;
        public int cntUsage;
        public int th32ProcessID;
        public nint th32DefaultHeapID;
        public int th32ModuleID;
        public int cntThreads;
        public int th32ParentProcessID;
        public int pcPriClassBase;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    public static bool HasChildren(int parentId)
    {
        nint snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == -1 || snapshot == 0) return false;

        try
        {
            var entry = new ProcessEntry { dwSize = Marshal.SizeOf<ProcessEntry>() };

            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID == parentId && entry.th32ProcessID != parentId)
                    return true;
            }

            return false;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }
}
