using System.IO;
using System.Windows;

namespace TerminalExplorer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Перенос данных из %APPDATA% выполняется до первого чтения настроек.
        SessionStore.MigrateFromAppData();

        AppSettings.Load().ApplyToResources();
        LanguageManager.Apply((UiLanguage)AppSettings.Current.Language);

        // Первый запуск (нет файла настроек) — показать настройки, чтобы пользователь
        // сразу увидел язык, тему и поведение, а не открывал приложение вслепую.
        if (!File.Exists(AppSettings.FilePath))
        {
            var settings = new SettingsWindow();
            settings.ShowDialog();
        }

        var main = new MainWindow();
        main.Show();
    }
}