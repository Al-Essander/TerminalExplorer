using System.Runtime.InteropServices;

namespace TerminalExplorer;

/// <summary>
/// Настоящее контекстное меню оболочки Windows.
///
/// Своё меню не даст ни «Создать», ни «Открыть с помощью», ни пунктов от установленных
/// программ (архиваторы, системы контроля версий, редакторы) — всё это живёт в расширениях
/// оболочки и доступно только через IContextMenu.
///
/// Порядок работы такой:
///   1. путь превращается в PIDL — внутренний идентификатор объекта оболочки;
///   2. у родительской папки запрашивается IShellFolder;
///   3. у неё берётся IContextMenu для выбранных элементов (или для фона папки);
///   4. меню наполняет наш HMENU через QueryContextMenu;
///   5. TrackPopupMenuEx показывает его и возвращает выбранную команду;
///   6. InvokeCommand выполняет её.
///
/// Отдельная сложность — подменю. Расширения вроде «Создать» или «Отправить» наполняют
/// свои подменю лениво, в ответ на оконные сообщения WM_INITMENUPOPUP. Их нужно
/// переадресовывать объекту меню через IContextMenu2/IContextMenu3, иначе подменю
/// открывается пустым. Этим занимается HandleMenuMessage.
/// </summary>
public static class ShellContextMenu
{
    // --- COM-интерфейсы ---

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(nint hwnd, nint bc, [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref uint eaten, out nint pidl, ref uint attributes);

        [PreserveSig] int EnumObjects(nint hwnd, int flags, out nint enumIdList);

        [PreserveSig] int BindToObject(nint pidl, nint bc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig] int BindToStorage(nint pidl, nint bc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig] int CompareIDs(nint lParam, nint pidl1, nint pidl2);

        [PreserveSig] int CreateViewObject(nint hwndOwner, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] pidls,
            ref uint attributes);

        [PreserveSig] int GetUIObjectOf(nint hwndOwner, uint count,
            [MarshalAs(UnmanagedType.LPArray)] nint[] pidls, ref Guid riid, nint reserved,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig] int GetDisplayNameOf(nint pidl, uint flags, nint name);

        [PreserveSig] int SetNameOf(nint hwnd, nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags, out nint pidlOut);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(nint id, uint type, nint reserved, nint commandName, int max);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214f4-0000-0000-c000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(nint id, uint type, nint reserved, nint commandName, int max);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719")]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint indexMenu, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(nint id, uint type, nint reserved, nint commandName, int max);
        [PreserveSig] int HandleMenuMsg(uint message, nint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, nint wParam, nint lParam, out nint result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public nint lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // --- P/Invoke ---

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder folder);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint bc,
        out nint pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern nint ILFindLastID(nint pidl);

    [DllImport("shell32.dll")]
    private static extern int ILRemoveLastID(nint pidl);

    [DllImport("shell32.dll")]
    private static extern nint ILClone(nint pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint pidl);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXPLORE = 0x00000004;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_LEFTALIGN = 0x0000;

    private const uint CMIC_MASK_UNICODE = 0x00004000;

    /// <summary>Диапазон идентификаторов команд, отдаваемый расширениям оболочки.</summary>
    private const uint IdFirst = 0x1000;
    private const uint IdLast = 0x7FFF;

    /// <summary>
    /// Активное меню — нужно, чтобы оконная процедура могла переадресовать ему сообщения.
    /// Меню показывается модально, поэтому одновременно оно всегда одно.
    /// </summary>
    private static object? _active;

    /// <summary>
    /// Показывает меню оболочки и выполняет выбранную команду.
    /// </summary>
    /// <param name="paths">Выбранные объекты. Пустой список — меню фона папки (там живёт «Создать»).</param>
    /// <param name="folder">Папка, в которой всё происходит.</param>
    /// <param name="owner">Окно-владелец: ему приходят сообщения меню.</param>
    /// <param name="screenX">Экранные координаты, где показать меню.</param>
    public static bool Show(IReadOnlyList<string> paths, string folder, nint owner, int screenX, int screenY)
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return false;

        var pidls = new List<nint>();
        nint parentPidl = 0;
        object? contextMenu = null;

        try
        {
            IShellFolder parent;

            if (paths.Count == 0)
            {
                // Меню фона папки: берётся у представления самой папки, а не у её элементов.
                if (!TryGetShellFolder(folder, out parent, out parentPidl)) return false;

                Guid contextMenuGuid = typeof(IContextMenu).GUID;
                if (parent.CreateViewObject(owner, ref contextMenuGuid, out object view) != 0) return false;

                contextMenu = view;
            }
            else
            {
                // Меню элементов: нужен IShellFolder их общего родителя.
                if (!TryGetShellFolder(folder, out parent, out parentPidl)) return false;

                foreach (string path in paths)
                {
                    if (SHParseDisplayName(path, 0, out nint full, 0, out _) != 0) continue;

                    // GetUIObjectOf принимает PIDL относительно родителя — только последний сегмент.
                    pidls.Add(ILClone(ILFindLastID(full)));
                    ILFree(full);
                }

                if (pidls.Count == 0) return false;

                Guid contextMenuGuid = typeof(IContextMenu).GUID;
                if (parent.GetUIObjectOf(owner, (uint)pidls.Count, [.. pidls], ref contextMenuGuid, 0,
                        out object result) != 0) return false;

                contextMenu = result;
            }

            if (contextMenu is not IContextMenu shellMenu) return false;

            uint flags = CMF_NORMAL | CMF_EXPLORE;

            // Shift открывает расширенные пункты — то же соглашение, что в проводнике.
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
                flags |= CMF_EXTENDEDVERBS;

            if (shellMenu.QueryContextMenu(menu, 0, IdFirst, IdLast, flags) == 0) return false;

            _active = contextMenu;

            uint command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_LEFTALIGN,
                screenX, screenY, owner, 0);

            if (command == 0) return false;

            Invoke(shellMenu, command - IdFirst, folder, owner);
            return true;
        }
        catch (COMException)
        {
            // Расширения оболочки — сторонний код; их сбой не должен ронять проводник.
            return false;
        }
        finally
        {
            _active = null;

            foreach (nint pidl in pidls) ILFree(pidl);
            if (parentPidl != 0) ILFree(parentPidl);
            if (contextMenu is not null) Marshal.ReleaseComObject(contextMenu);

            DestroyMenu(menu);
        }
    }

    private static void Invoke(IContextMenu menu, uint commandId, string folder, nint owner)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE,
            hwnd = owner,

            // Команда задаётся не строкой, а номером в младшем слове указателя —
            // соглашение MAKEINTRESOURCE, унаследованное от Win16.
            lpVerb = (nint)commandId,
            lpVerbW = (nint)commandId,

            lpDirectory = folder,
            lpDirectoryW = folder,
            nShow = 1,
        };

        menu.InvokeCommand(ref info);
    }

    private static bool TryGetShellFolder(string path, out IShellFolder folder, out nint pidl)
    {
        folder = null!;
        pidl = 0;

        if (SHParseDisplayName(path, 0, out pidl, 0, out _) != 0) return false;
        if (SHGetDesktopFolder(out IShellFolder desktop) != 0) return false;

        Guid shellFolderGuid = typeof(IShellFolder).GUID;

        if (desktop.BindToObject(pidl, 0, ref shellFolderGuid, out object result) != 0) return false;
        if (result is not IShellFolder bound) return false;

        folder = bound;
        return true;
    }

    /// <summary>
    /// Переадресует оконные сообщения активному меню.
    ///
    /// Без этого подменю «Создать», «Отправить» и пункты сторонних расширений открываются
    /// пустыми: они наполняются лениво, получив WM_INITMENUPOPUP или WM_MEASUREITEM.
    /// Вызывается из хука оконной процедуры.
    /// </summary>
    public static bool HandleMenuMessage(int message, nint wParam, nint lParam, out nint result)
    {
        result = 0;

        if (_active is null) return false;

        const int WM_INITMENUPOPUP = 0x0117;
        const int WM_DRAWITEM = 0x002B;
        const int WM_MEASUREITEM = 0x002C;
        const int WM_MENUCHAR = 0x0120;

        if (message is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
            return false;

        try
        {
            // IContextMenu3 умеет вернуть результат — он нужен для WM_MENUCHAR.
            if (_active is IContextMenu3 menu3)
                return menu3.HandleMenuMsg2((uint)message, wParam, lParam, out result) == 0;

            if (_active is IContextMenu2 menu2)
                return menu2.HandleMenuMsg((uint)message, wParam, lParam) == 0;
        }
        catch (COMException)
        {
        }

        return false;
    }
}
