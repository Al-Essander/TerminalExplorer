using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace TerminalExplorer;

/// <summary>
/// Плавающее окно предпросмотра PDF, TXT и Markdown одним нажатием.
///
/// Открывается поверх интерфейса (F3 или пункт меню «Предпросмотр») и не трогает
/// сетку панелей и терминал — в отличие от разбиения панели. Закрывается Esc или крестиком.
///
/// PDF рендерится через системный Windows.Data.Pdf: он входит в Windows и не тянет
/// сторонних библиотек. Текст (txt/md и прочие) читается с определением кодировки,
/// как это делает сам редактор, но только для чтения.
/// </summary>
public partial class PreviewWindow : Window
{
    // Текстовые файлы открываем только разумного размера — иначе загрузим гигабайт.
    private const long MaxTextBytes = 16 * 1024 * 1024;

    // Расширения, которые предпросмотр считает текстовыми (все остальные — как бинарные).
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".json", ".xml",
        ".yaml", ".yml", ".csv", ".tsv", ".html", ".htm", ".css", ".js", ".cs", ".ts",
        ".py", ".c", ".cpp", ".h", ".hpp", ".java", ".sh", ".bat", ".cmd", ".ps1", ".sql",
    };

    public PreviewWindow(string path)
    {
        InitializeComponent();

        Loaded += (_, _) => Load(path);
    }

    /// <summary>Показывает ли предпросмотр этот файл: PDF или известный текстовый формат.</summary>
    public static bool CanPreview(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return false;

        return TextExtensions.Contains(extension) ||
               string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private void Load(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            TitleText.Text = Path.GetFileName(path);

            if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                _ = LoadPdfAsync(path);
                return;
            }

            if (TextExtensions.Contains(extension))
            {
                LoadText(path);
                return;
            }

            ShowError(LanguageManager.Tr("L_PreviewUnsupported") + ": " + extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(LanguageManager.Tr("L_PreviewError") + ": " + ex.Message);
        }
    }

    // ---------- текст ----------

    private void LoadText(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { ShowError(LanguageManager.Tr("L_FileNotFound")); return; }

        if (info.Length > MaxTextBytes)
        {
            ShowError(LanguageManager.Tr("L_FileTooBig"));
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);

        // Двоичные файлы не показываем как текст — иначе в панель попадёт мусор.
        if (bytes.Contains((byte)0)) { ShowError(LanguageManager.Tr("L_BinaryFile")); return; }

        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();

        PdfViewer.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        PreviewText.Text = text;
        StatusText.Text = LanguageManager.Tr("L_PreviewTextStatus") + " " + FormatSize(bytes.LongLength);
    }

    // ---------- PDF ----------

    private async System.Threading.Tasks.Task LoadPdfAsync(string path)
    {
        StatusText.Text = LanguageManager.Tr("L_PreviewLoading");

        try
        {
            PdfDocument? document = null;

            // Windows.Data.Pdf открывает файл через IInputStream: используем StorageFile,
            // чтобы избежать чтения всего файла в память при большом PDF.
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using (IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read))
            {
                document = await PdfDocument.LoadFromStreamAsync(stream);
            }

            if (document is null) { ShowError(LanguageManager.Tr("L_PreviewPdfFailed")); return; }

            PdfViewer.Visibility = Visibility.Visible;
            PreviewText.Visibility = Visibility.Collapsed;
            PdfPages.Children.Clear();

            // Рендерим каждую страницу в BitmapImage. Для больших файлов ограничиваемся
            // первыми страницами, чтобы окно не зависло на открытии.
            const int MaxPages = 40;
            int total = (int)document.PageCount;

            for (uint pageIndex = 0; pageIndex < document.PageCount && pageIndex < MaxPages; pageIndex++)
            {
                using PdfPage page = document.GetPage(pageIndex);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream.AsStream();
                bitmap.EndInit();
                bitmap.Freeze();

                PdfPages.Children.Add(new Image
                {
                    Source = bitmap,
                    Margin = new Thickness(0, 0, 0, 8),
                    MaxWidth = 720,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                });
            }

            StatusText.Text = total > MaxPages
                ? string.Format(LanguageManager.Tr("L_PreviewPages"), MaxPages, total)
                : string.Format(LanguageManager.Tr("L_PreviewPages"), total, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowError(LanguageManager.Tr("L_PreviewPdfFailed") + ": " + ex.Message);
        }
    }

    // ---------- сообщения ----------

    private void ShowError(string message)
    {
        PdfViewer.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        PreviewText.Text = string.Empty;
        StatusText.Text = message;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024L * 1024 => (bytes / 1024.0).ToString("N0") + " KB",
        _ => (bytes / 1024.0 / 1024).ToString("N1") + " MB",
    };

    // ---------- окно ----------

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }
}