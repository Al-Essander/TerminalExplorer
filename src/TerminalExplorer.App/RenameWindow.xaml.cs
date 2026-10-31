using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TerminalExplorer;

public partial class RenameWindow : Window
{
    public string NewName { get; private set; } = string.Empty;

    private readonly string _original;

    public RenameWindow(string currentName, bool isDirectory)
    {
        InitializeComponent();

        _original = currentName;

        TitleText.Text = isDirectory ? "  ПЕРЕИМЕНОВАТЬ ПАПКУ" : "  ПЕРЕИМЕНОВАТЬ ФАЙЛ";
        HintText.Text = currentName;
        NameBox.Text = currentName;

        Loaded += (_, _) =>
        {
            NameBox.Focus();

            // У файла выделяется только имя без расширения — менять обычно нужно именно его,
            // и случайная потеря расширения ломает ассоциацию с программой.
            int extension = isDirectory ? -1 : currentName.LastIndexOf('.');

            if (extension > 0) NameBox.Select(0, extension);
            else NameBox.SelectAll();
        };
    }

    private void Name_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Accept_Click(sender, e);
                break;

            case Key.Escape:
                e.Handled = true;
                Cancel_Click(sender, e);
                break;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();

        if (!Validate(name, out string? problem))
        {
            ErrorText.Text = problem;
            ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x4A));
            return;
        }

        NewName = name;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Проверка имени до попытки переименования. Те же правила, что у генератора структуры:
    /// запрещённые символы, точка и пробел в конце, зарезервированные имена DOS.
    /// </summary>
    private bool Validate(string name, out string? problem)
    {
        problem = null;

        if (name.Length == 0) { problem = "имя не может быть пустым"; return false; }
        if (name == _original) { problem = null; return true; }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problem = "недопустимые символы: \\ / : * ? \" < > |";
            return false;
        }

        if (name is "." or "..") { problem = "недопустимое имя"; return false; }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            problem = "имя не может заканчиваться точкой или пробелом";
            return false;
        }

        string bare = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ];

        if (reserved.Contains(bare))
        {
            problem = "имя " + bare + " зарезервировано системой";
            return false;
        }

        return true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
