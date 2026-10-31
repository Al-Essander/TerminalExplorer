using System.Windows;

namespace TerminalExplorer;

/// <summary>Доступные языки интерфейса.</summary>
public enum UiLanguage { Russian, English }

/// <summary>
/// Переключение языка интерфейса в рантайме.
///
/// Работает так же, как <see cref="ThemeManager"/> с палитрами: все строки в XAML
/// берутся через DynamicResource по ключу L_*, а подмена словаря в MergedDictionaries
/// перекрашивает уже построенное дерево элементов. Окно пересоздавать не нужно.
///
/// LanguageManager не знает, какой словарь сейчас базовый (App.xaml задаёт RU), он
/// лишь кладёт выбранный словарь в отдельный слот и событием просит окна обновиться.
/// </summary>
public static class LanguageManager
{
    // Индекс словаря языка в MergedDictionaries — задан порядком в App.xaml (см. ниже).
    // Палитра — слот 0, тема — слот 1, язык — слот 2.
    private const int Slot = 2;

    public static UiLanguage Current { get; private set; } = UiLanguage.Russian;

    /// <summary>Язык изменён — окнам с вычисляемым текстом (заголовок, меню) пора пересчитаться.</summary>
    public static event Action? Changed;

    public static void Apply(UiLanguage language)
    {
        if (language == Current && Application.Current.Resources.MergedDictionaries.Count > Slot) return;

        string source = language == UiLanguage.English ? "Strings.En.xaml" : "Strings.Ru.xaml";
        var dictionary = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        // Словарь строк держим за словарём языка, а не поверх темы, чтобы при следующей
        // смене языка тема (слот 1) не трогалась.
        if (Application.Current.Resources.MergedDictionaries.Count > Slot)
            Application.Current.Resources.MergedDictionaries[Slot] = dictionary;
        else
            Application.Current.Resources.MergedDictionaries.Add(dictionary);

        Current = language;
        Changed?.Invoke();
    }

    public static string Tr(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}