using System.Windows;
using System.Windows.Media;

namespace TerminalExplorer;

/// <summary>
/// Переключение палитры в рантайме.
///
/// Работает за счёт того, что все шаблоны в Theme.xaml ссылаются на цвета через
/// DynamicResource: подмена словаря палитры перекрашивает уже построенное дерево
/// элементов, окно пересоздавать не нужно.
/// </summary>
public static class ThemeManager
{
    /// <summary>Пользовательская палитра всегда последняя — она строится, а не загружается.</summary>
    public const int CustomIndex = 4;

    public static readonly string[] Names = ["Спокойная", "MS-DOS классика", "MS-DOS Far", "Классика", "Пользовательская"];

    private static readonly string[] Sources =
        ["Palette.Calm.xaml", "Palette.Dos.xaml", "Palette.DosFar.xaml", "Palette.Classic.xaml"];

    /// <summary>Ключи, из которых состоит палитра. Порядок задаёт порядок полей в настройках.</summary>
    public static readonly string[] ColorKeys =
    [
        "Background", "Surface", "Foreground", "Dim", "Selection", "Accent", "Border", "Hover",
        "BevelLight", "BevelDark",
    ];

    /// <summary>Понятные подписи для окна настроек.</summary>
    public static readonly Dictionary<string, string> ColorTitles = new()
    {
        ["Background"] = "Фон",
        ["Surface"] = "Панели",
        ["Foreground"] = "Текст",
        ["Dim"] = "Приглушённый текст",
        ["Selection"] = "Выделение",
        ["Accent"] = "Акцент",
        ["Border"] = "Рамки",
        ["Hover"] = "Наведение",
        ["BevelLight"] = "Грань светлая",
        ["BevelDark"] = "Грань тёмная",
    };

    /// <summary>Индекс словаря палитры в MergedDictionaries — задан порядком в App.xaml.</summary>
    private const int PaletteSlot = 0;

    public static int Current { get; private set; }

    /// <summary>Тема применена — элементам с вычисляемыми цветами пора пересчитаться.</summary>
    public static event Action? Changed;

    public static string CurrentName => Names[Math.Clamp(Current, 0, Names.Length - 1)];

    public static void Apply(int index)
    {
        index = ((index % Names.Length) + Names.Length) % Names.Length;

        ResourceDictionary palette = index == CustomIndex
            ? BuildCustomPalette()
            : new ResourceDictionary { Source = new Uri(Sources[index], UriKind.Relative) };

        Application.Current.Resources.MergedDictionaries[PaletteSlot] = palette;
        Current = index;
        Changed?.Invoke();
    }

    public static void Next() => Apply(Current + 1);

    /// <summary>
    /// Собирает палитру из пользовательских цветов. Шрифт берётся из встроенной палитры,
    /// чтобы ключ MonoFont не пропал: его перекрывает ApplyToResources, но при отсутствии
    /// ключа вовсе WPF свалится на системный шрифт.
    /// </summary>
    private static ResourceDictionary BuildCustomPalette()
    {
        var palette = new ResourceDictionary();

        foreach (string key in ColorKeys)
        {
            string value = AppSettings.Current.CustomColors.TryGetValue(key, out string? hex) ? hex : "#FF808080";
            palette[key] = new SolidColorBrush(ParseColor(value));
        }

        palette["MonoFont"] = new FontFamily(AppSettings.Current.FontFamily);
        return palette;
    }

    /// <summary>Разбирает #RRGGBB или #AARRGGBB. Некорректное значение даёт серый, а не исключение.</summary>
    public static Color ParseColor(string value) =>
        TryParseColor(value, out Color color) ? color : Colors.Gray;

    /// <summary>
    /// Строгая проверка цвета.
    ///
    /// Нужна отдельно от ParseColor, потому что настройки сохраняются на каждый введённый
    /// символ: без проверки в файл попадает недописанное значение вроде "#FF264F7",
    /// и при следующем запуске цвет молча становится серым.
    /// </summary>
    public static bool TryParseColor(string value, out Color color)
    {
        color = Colors.Gray;

        if (string.IsNullOrWhiteSpace(value)) return false;

        // ColorConverter принимает и имена цветов, но допускает же и неполные hex-строки,
        // дополняя их — поэтому длину проверяем сами.
        string text = value.Trim();
        if (text.StartsWith('#') && text.Length is not (7 or 9)) return false;

        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Копирует текущую палитру в пользовательскую — основа для правки «от готового».</summary>
    public static void SeedCustomFromCurrent()
    {
        foreach (string key in ColorKeys)
        {
            if (Application.Current.Resources[key] is SolidColorBrush brush)
                AppSettings.Current.CustomColors[key] = brush.Color.ToString();
        }
    }
}
