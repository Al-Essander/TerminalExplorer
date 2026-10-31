using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TerminalExplorer;

public partial class SettingsWindow : Window
{
    /// <summary>Строка редактора цвета: подпись, значение и живой образец.</summary>
    public sealed class ColorRow : INotifyPropertyChanged
    {
        private string _value = string.Empty;

        public required string Key { get; init; }
        public required string Title { get; init; }

        public string Value
        {
            get => _value;
            set
            {
                if (_value == value) return;

                _value = value;
                Notify();
                Notify(nameof(Preview));
            }
        }

        public Brush Preview => new SolidColorBrush(ThemeManager.ParseColor(_value));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private readonly ObservableCollection<ColorRow> _colors = [];

    /// <summary>
    /// Флаг подавления реакций во время начального заполнения: иначе установка значений
    /// слайдерам сама вызовет ValueChanged и перезапишет настройки нулями.
    /// </summary>
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        AppSettings settings = AppSettings.Current;

        foreach (string key in ThemeManager.ColorKeys)
        {
            _colors.Add(new ColorRow
            {
                Key = key,
                Title = ThemeManager.ColorTitles.TryGetValue(key, out string? title) ? title : key,
                Value = settings.CustomColors.TryGetValue(key, out string? hex) ? hex : "#FF808080",
            });
        }

        ColorList.ItemsSource = _colors;

        FontSizeSlider.Value = settings.FontSize;
        FontFamilyBox.Text = settings.FontFamily;
        TabHeightSlider.Value = settings.TabHeight;
        TabWidthSlider.Value = settings.TabMaxWidth;
        TabPaddingSlider.Value = settings.TabPaddingHorizontal;
        SoundCheck.IsChecked = settings.SoundEnabled;
        NewTabCheck.IsChecked = settings.OpenFoldersInNewTab;

        PathHint.Text = SessionStore.DirectoryPath;
        UpdateSoundHint();

        // Язык: пункты показываем на выбранном языке, текущий отмечаем.
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(new ComboBoxItem { Content = LanguageManager.Tr("L_LangRussian") });
        LanguageBox.Items.Add(new ComboBoxItem { Content = LanguageManager.Tr("L_LangEnglish") });
        LanguageBox.SelectedIndex = AppSettings.Current.Language;

        _loading = false;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var language = (UiLanguage)LanguageBox.SelectedIndex;
        if (language == LanguageManager.Current) return;

        AppSettings.Current.Language = (int)language;
        AppSettings.Current.Save();

        // Окно настроек пересоздаём под новый язык: его подписи и пункты не привязаны
        // к DynamicResource, поэтому проще показать заново, чем пересчитывать каждую строку.
        LanguageManager.Apply(language);
        LanguageManager.Changed += ReloadWindowOnLanguageChange;
    }

    private void ReloadWindowOnLanguageChange()
    {
        LanguageManager.Changed -= ReloadWindowOnLanguageChange;
        var window = new SettingsWindow { Owner = Owner };
        Close();
        window.ShowDialog();
    }

    private void UpdateSoundHint()
    {
        string directory = Sounds.DirectoryPath;
        var present = new List<string>();
        var missing = new List<string>();

        foreach (string file in Sounds.ExpectedFiles)
        {
            if (File.Exists(Path.Combine(directory, file))) present.Add(file);
            else missing.Add(file);
        }

        SoundHint.Text = present.Count == 0
            ? string.Format(LanguageManager.Tr("L_SoundsNone"), string.Join(", ", Sounds.ExpectedFiles))
            : string.Format(LanguageManager.Tr("L_SoundsFound"), string.Join(", ", present)) +
              (missing.Count > 0 ? string.Format(LanguageManager.Tr("L_SoundsMissing"), string.Join(", ", missing)) : string.Empty);
    }

    // ---------- обработчики ----------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out int index)) return;

        ThemeManager.Apply(index);
        AppSettings.Current.ThemeIndex = index;
        AppSettings.Current.Save();
    }

    private void Color_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox { Tag: string key } box) return;

        // Сохраняем только дописанное значение: иначе в файл попадёт промежуточное
        // состояние вроде "#FF264F7", и при следующем запуске цвет станет серым.
        if (!ThemeManager.TryParseColor(box.Text, out _))
        {
            box.Foreground = (Brush)FindResource("Dim");
            return;
        }

        box.Foreground = (Brush)FindResource("Foreground");

        AppSettings.Current.CustomColors[key] = box.Text.Trim();
        AppSettings.Current.Save();

        // Перерисовываем немедленно, но только если сейчас показана пользовательская тема —
        // иначе правка цветов молча перебила бы выбранную встроенную палитру.
        if (ThemeManager.Current == ThemeManager.CustomIndex)
            ThemeManager.Apply(ThemeManager.CustomIndex);
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColorRow row }) return;

        string originalValue = row.Value;
        int originalTheme = ThemeManager.Current;

        var picker = new ColorPickerWindow(row.Title, ThemeManager.ParseColor(row.Value)) { Owner = this };

        // Живое превью: каждое движение по палитре сразу перекрашивает интерфейс,
        // чтобы цвет оценивался на реальных элементах, а не в образце размером с ноготь.
        picker.ColorChanged += color => ApplyColorPreview(row, ToHex(color));

        bool accepted = picker.ShowDialog() == true;

        if (accepted)
        {
            AppSettings.Current.CustomColors[row.Key] = row.Value;
            AppSettings.Current.ThemeIndex = ThemeManager.CustomIndex;
            AppSettings.Current.Save();
            return;
        }

        // Отмена возвращает всё как было — и цвет, и выбранную до этого тему.
        row.Value = originalValue;
        AppSettings.Current.CustomColors[row.Key] = originalValue;
        ThemeManager.Apply(originalTheme);
    }

    /// <summary>Применяет цвет к живому интерфейсу, не записывая его в файл настроек.</summary>
    private void ApplyColorPreview(ColorRow row, string hex)
    {
        row.Value = hex;
        AppSettings.Current.CustomColors[row.Key] = hex;

        // Превью имеет смысл только на своей теме: на встроенной палитре правка не видна.
        ThemeManager.Apply(ThemeManager.CustomIndex);
    }

    private static string ToHex(Color color) =>
        "#FF" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2");

    private void SeedColors_Click(object sender, RoutedEventArgs e)
    {
        ThemeManager.SeedCustomFromCurrent();

        _loading = true;
        foreach (ColorRow row in _colors)
        {
            if (AppSettings.Current.CustomColors.TryGetValue(row.Key, out string? hex))
                row.Value = hex;
        }
        _loading = false;

        AppSettings.Current.Save();
    }

    private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        AppSettings.Current.FontSize = e.NewValue;
        AppSettings.Current.ApplyToResources();
        AppSettings.Current.Save();
    }

    private void FontFamily_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading || string.IsNullOrWhiteSpace(FontFamilyBox.Text)) return;

        AppSettings.Current.FontFamily = FontFamilyBox.Text;
        AppSettings.Current.ApplyToResources();
        AppSettings.Current.Save();
    }

    private void TabHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        AppSettings.Current.TabHeight = e.NewValue;
        AppSettings.Current.ApplyToResources();
        AppSettings.Current.Save();
    }

    private void TabWidth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        AppSettings.Current.TabMaxWidth = e.NewValue;
        AppSettings.Current.ApplyToResources();
        AppSettings.Current.Save();
    }

    private void TabPadding_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        AppSettings.Current.TabPaddingHorizontal = e.NewValue;
        AppSettings.Current.ApplyToResources();
        AppSettings.Current.Save();
    }

    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        AppSettings.Current.SoundEnabled = SoundCheck.IsChecked == true;
        AppSettings.Current.Save();

        if (AppSettings.Current.SoundEnabled) Sounds.Play(Sounds.TabSwitch);
    }

    private void Behaviour_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        AppSettings.Current.OpenFoldersInNewTab = NewTabCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OpenSounds_Click(object sender, RoutedEventArgs e)
    {
        string directory = Sounds.EnsureDirectory();
        ShellActions.OpenFolder(directory);
    }

    private void ReloadSounds_Click(object sender, RoutedEventArgs e)
    {
        Sounds.Reload();
        UpdateSoundHint();
        Sounds.Play(Sounds.TabSwitch);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
