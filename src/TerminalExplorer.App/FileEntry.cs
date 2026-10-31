using System.IO;

namespace TerminalExplorer;

/// <summary>
/// Строка списка файлов.
///
/// Обычный неизменяемый класс без INotifyPropertyChanged: содержимое папки не меняется
/// точечно, оно перечитывается целиком, а на 100 000 строк уведомления об изменениях
/// стоили бы дороже самой перезагрузки.
/// </summary>
public sealed class FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public required FileAttributes Attributes { get; init; }

    /// <summary>Ссылка (символическая или точка соединения) — показывается отдельным типом.</summary>
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>Расширение без точки, в нижнем регистре. Для папок — пусто.</summary>
    public string Extension
    {
        get
        {
            if (IsDirectory) return string.Empty;

            int dot = Name.LastIndexOf('.');
            return dot > 0 && dot < Name.Length - 1
                ? Name[(dot + 1)..].ToLowerInvariant()
                : string.Empty;
        }
    }

    public string TypeText => IsLink
        ? "<ССЫЛКА>"
        : IsDirectory
            ? "<ПАПКА>"
            : Extension.Length > 0 ? Extension.ToUpperInvariant() : "ФАЙЛ";

    /// <summary>
    /// Размер в DOS-стиле: без десятичных дробей, с разделителями разрядов.
    /// У папок размер не показывается — его подсчёт требует обхода и делается по требованию.
    /// </summary>
    public string SizeText
    {
        get
        {
            if (IsDirectory) return string.Empty;

            return Size switch
            {
                < 1024 => Size.ToString("N0") + " B",
                < 1024L * 1024 => (Size / 1024.0).ToString("N0") + " K",
                < 1024L * 1024 * 1024 => (Size / 1024.0 / 1024).ToString("N1") + " M",
                _ => (Size / 1024.0 / 1024 / 1024).ToString("N2") + " G",
            };
        }
    }

    public string ModifiedText => Modified == default
        ? string.Empty
        : Modified.ToString("dd.MM.yyyy  HH:mm");

    /// <summary>Строка атрибутов в стиле DOS: R/H/S/A.</summary>
    public string AttributesText
    {
        get
        {
            Span<char> flags =
            [
                (Attributes & FileAttributes.ReadOnly) != 0 ? 'R' : '-',
                (Attributes & FileAttributes.Hidden) != 0 ? 'H' : '-',
                (Attributes & FileAttributes.System) != 0 ? 'S' : '-',
                (Attributes & FileAttributes.Archive) != 0 ? 'A' : '-',
            ];

            return new string(flags);
        }
    }
}
