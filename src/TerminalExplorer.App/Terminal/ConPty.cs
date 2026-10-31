using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TerminalExplorer.Terminal;

/// <summary>
/// Псевдоконсоль Windows (ConPTY) и дочерний процесс внутри неё.
///
/// ConPTY — штатный способ встроить настоящую консоль в чужое окно: система сама
/// запускает conhost, а нам отдаёт два потока байтов (ввод и вывод в виде VT-последовательностей).
/// Именно так устроены Windows Terminal и терминал VS Code. Альтернатива — перенаправить
/// stdout обычного процесса — ломает всё интерактивное: PSReadLine, цвета, vim, less.
///
/// Требуется Windows 10 1809+; программа и так ориентирована на Windows 11.
/// </summary>
public sealed class ConPty : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const nint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        public JobBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(nint console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment,
        string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref JobExtendedLimit info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    /// <summary>
    /// Общий job-объект с флагом «убить всех при закрытии». Если программа упадёт,
    /// система сама завершит все оболочки — иначе они остались бы висеть сиротами.
    /// </summary>
    private static readonly Lazy<nint> Job = new(() =>
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0) return 0;

        var info = new JobExtendedLimit();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JobExtendedLimit>());

        return job;
    });

    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private FileStream? _writer;
    private nint _console;
    private nint _process;
    private bool _disposed;

    public int ProcessId { get; private set; }

    /// <summary>Поток вывода оболочки (VT-последовательности в UTF-8).</summary>
    public Stream Output { get; private set; } = Stream.Null;

    /// <summary>Запускает команду в новой псевдоконсоли. Бросает Win32Exception, если не вышло.</summary>
    public static ConPty Start(string commandLine, string? directory, int columns, int rows)
    {
        var pty = new ConPty();

        try
        {
            pty.Launch(commandLine, directory, columns, rows);
            return pty;
        }
        catch
        {
            pty.Dispose();
            throw;
        }
    }

    private void Launch(string commandLine, string? directory, int columns, int rows)
    {
        if (!CreatePipe(out SafeFileHandle inputRead, out SafeFileHandle inputWrite, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _inputWrite = inputWrite;

        if (!CreatePipe(out SafeFileHandle outputRead, out SafeFileHandle outputWrite, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        _outputRead = outputRead;

        var size = new Coord { X = (short)columns, Y = (short)rows };
        int hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _console);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole");

        // Консоль держит собственные копии концов канала; наши можно закрывать,
        // иначе чтение вывода не завершится при выходе оболочки.
        inputRead.Dispose();
        outputWrite.Dispose();

        _writer = new FileStream(_inputWrite, FileAccess.Write, 4096, isAsync: false);
        Output = new FileStream(_outputRead, FileAccess.Read, 4096, isAsync: false);

        nint listSize = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref listSize);

        nint list = Marshal.AllocHGlobal(listSize);

        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributePseudoConsole, _console, nint.Size, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfoEx { lpAttributeList = list };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();

            if (!CreateProcessW(null, new StringBuilder(commandLine), 0, 0, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, 0, directory,
                    ref startup, out ProcessInformation info))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            _process = info.hProcess;
            ProcessId = info.dwProcessId;
            CloseHandle(info.hThread);

            if (Job.Value != 0) AssignProcessToJobObject(Job.Value, _process);
        }
        finally
        {
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || _writer is null) return;

        try
        {
            _writer.Write(data);
            _writer.Flush();
        }
        catch (IOException)
        {
            // Оболочка уже завершилась — вводить некому.
        }
    }

    public void Resize(int columns, int rows)
    {
        if (_disposed || _console == 0) return;

        ResizePseudoConsole(_console, new Coord { X = (short)columns, Y = (short)rows });
    }

    /// <summary>Жив ли процесс оболочки.</summary>
    public bool IsRunning => !_disposed && _process != 0 && WaitForSingleObject(_process, 0) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_process != 0)
        {
            TerminateProcess(_process, 0);
            CloseHandle(_process);
            _process = 0;
        }

        nint console = _console;
        _console = 0;

        // ClosePseudoConsole на старых сборках Windows ждёт, пока кто-то дочитает вывод,
        // поэтому закрываем его в фоне — читающий поток к этому моменту ещё жив.
        if (console != 0) Task.Run(() => ClosePseudoConsole(console));

        _writer?.Dispose();
        _inputWrite?.Dispose();
    }
}
