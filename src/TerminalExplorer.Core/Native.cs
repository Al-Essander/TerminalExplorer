using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerminalExplorer.Core;

/// <summary>
/// P/Invoke-обёртки, нужные для перечисления MFT. Только kernel32, сторонних библиотек нет.
/// </summary>
internal static partial class Native
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, function=44, METHOD_NEITHER=3, FILE_ANY_ACCESS=0)
    public const uint FSCTL_ENUM_USN_DATA = (9u << 16) | (44u << 2) | 3u;

    public const uint GENERIC_READ = 0x8000_0000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    /// <summary>Номер записи MFT для корневого каталога тома. Обход путей завершается на нём.</summary>
    public const ulong RootFileReferenceNumber = 0x5;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref MFT_ENUM_DATA_V0 lpInBuffer,
        int nInBufferSize,
        nint lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        nint lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct MFT_ENUM_DATA_V0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }
}
