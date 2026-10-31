using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace TerminalExplorer;

/// <summary>
/// Избранные папки (п. 3.6 паспорта).
///
/// Отдельный файл, а не часть настроек: паспорт предполагает экспорт и перенос избранного
/// между машинами, а тащить с ним заодно цвета и размеры шрифта смысла нет.
/// </summary>
public sealed class FavoriteItem
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Подпись в панели. По умолчанию — имя папки, но её можно переименовать.</summary>
    public string Name { get; set; } = string.Empty;

    public string Display => string.IsNullOrWhiteSpace(Name)
        ? (System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path)
        : Name;
}

public static class Favorites
{
    public static ObservableCollection<FavoriteItem> Items { get; } = [];

    private static string FilePath => Path.Combine(SessionStore.DirectoryPath, "favorites.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Load()
    {
        Items.Clear();

        try
        {
            if (!File.Exists(FilePath)) return;

            var loaded = JsonSerializer.Deserialize<List<FavoriteItem>>(File.ReadAllText(FilePath), Options);
            if (loaded is null) return;

            foreach (FavoriteItem item in loaded) Items.Add(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(SessionStore.DirectoryPath);

            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Items, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Добавляет папку, если её ещё нет. Возвращает false, если путь уже в списке.</summary>
    public static bool Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Contains(path)) return false;

        Items.Add(new FavoriteItem { Path = path });
        Save();
        return true;
    }

    public static bool Contains(string path) =>
        Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public static void Remove(FavoriteItem item)
    {
        Items.Remove(item);
        Save();
    }

    /// <summary>Перемещает элемент вверх или вниз — порядок в панели задаёт пользователь.</summary>
    public static void Move(FavoriteItem item, int delta)
    {
        int index = Items.IndexOf(item);
        if (index < 0) return;

        int target = Math.Clamp(index + delta, 0, Items.Count - 1);
        if (target == index) return;

        Items.Move(index, target);
        Save();
    }
}
