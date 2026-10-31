using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TerminalExplorer;

/// <summary>
/// Операции, которые выполняет оболочка Windows.
///
/// Полное контекстное меню оболочки (IContextMenu) сюда пока не входит — это отдельный
/// объём работы с COM. Но диалог «Свойства» доступен куда дешевле: ShellExecuteEx умеет
/// вызывать глагол "properties" напрямую.
/// </summary>
public static partial class ShellActions
{
    private const int SW_SHOW = 5;
    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIcon;
        public nint hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    /// <summary>Открывает системный диалог «Свойства» для файла или папки.</summary>
    public static bool ShowProperties(string path, nint owner)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            // SEE_MASK_INVOKEIDLIST обязателен: без него глагол "properties" игнорируется.
            fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = SW_SHOW,
        };

        return ShellExecuteExW(ref info);
    }

    /// <summary>Открывает файл ассоциированной программой — как двойной клик в проводнике.</summary>
    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>
    /// Открывает папку в проводнике Windows.
    ///
    /// Отдельно от Open: ShellExecute на каталог идёт через ассоциацию и на некоторых
    /// конфигурациях отвечает «Расположение недоступно». Явный вызов explorer.exe
    /// с путём в кавычках работает всегда.
    /// </summary>
    public static void OpenFolder(string directory) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"")
        {
            UseShellExecute = true,
        });

    /// <summary>Показывает объект в штатном проводнике Windows, выделив его.</summary>
    public static void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
        {
            UseShellExecute = true,
        });

    /// <summary>Открывает окно терминала в указанной папке.</summary>
    public static void OpenTerminal(string directory) =>
        Process.Start(new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = directory,
            UseShellExecute = true,
        });
}
