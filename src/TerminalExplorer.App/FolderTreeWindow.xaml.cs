using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TerminalExplorer;

/// <summary>
/// Встроенный аддон: генератор структуры папок.
///
/// Задача из паспорта (п. 6.3): описать дерево один раз и создавать его сколько угодно раз.
/// Глубина ограничена тремя уровнями осознанно — не техническим пределом, а тем, что
/// более глубокие шаблоны на практике неудобно и редактировать, и читать.
/// </summary>
public partial class FolderTreeWindow : Window
{
    /// <summary>Узел дерева. Отдельный класс, а не запись, потому что имя редактируется в UI.</summary>
    public sealed class TreeNode : INotifyPropertyChanged
    {
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Notify(); } }
        }

        public ObservableCollection<TreeNode> Children { get; init; } = [];

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    /// <summary>Формат шаблона на диске. Плоские вложенные списки, читаемые глазами.</summary>
    public sealed class TemplateNode
    {
        public string Name { get; set; } = string.Empty;
        public List<TemplateNode> Children { get; set; } = [];
    }

    public sealed class FolderTemplate
    {
        public string Name { get; set; } = string.Empty;
        public string RootName { get; set; } = "rootName";
        public List<TemplateNode> Children { get; set; } = [];
    }

    /// <summary>Глубина вложенности, доступная пользователю (корень не считается).</summary>
    private const int MaxDepth = 3;

    private readonly string _targetFolder;
    private readonly ObservableCollection<TreeNode> _roots = [];
    private readonly List<FolderTemplate> _templates = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string TemplatesPath => Path.Combine(SessionStore.DirectoryPath, "folder-templates.json");

    public FolderTreeWindow(string targetFolder)
    {
        InitializeComponent();

        _targetFolder = targetFolder;
        TargetText.Text = targetFolder;

        // Стартовая структура — то, что просили: корень и три ребёнка одного уровня.
        _roots.Add(new TreeNode
        {
            Name = "rootName",
            Children =
            {
                new TreeNode { Name = "0-child" },
                new TreeNode { Name = "1-child" },
                new TreeNode { Name = "2-child" },
            },
        });

        Tree.ItemsSource = _roots;

        LoadTemplates();
        UpdatePreview();
    }

    private TreeNode Root => _roots[0];

    private TreeNode? SelectedNode => Tree.SelectedItem as TreeNode;

    // ---------- редактирование дерева ----------

    private void Tree_SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (SelectedNode is { } node) NodeNameBox.Text = node.Name;
    }

    private void NodeName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        AddChild_Click(sender, e);
    }

    private void AddChild_Click(object sender, RoutedEventArgs e)
    {
        TreeNode parent = SelectedNode ?? Root;

        if (DepthOf(parent) >= MaxDepth)
        {
            SetStatus(string.Format(LanguageManager.Tr("L_TreeTooDeep"), MaxDepth));
            return;
        }

        string name = NodeNameBox.Text.Trim();
        if (!ValidateName(name)) return;

        parent.Children.Add(new TreeNode { Name = name });
        NodeNameBox.Clear();
        UpdatePreview();
    }

    private void AddBatch_Click(object sender, RoutedEventArgs e)
    {
        TreeNode parent = SelectedNode ?? Root;

        if (DepthOf(parent) >= MaxDepth)
        {
            SetStatus(string.Format(LanguageManager.Tr("L_TreeTooDeep"), MaxDepth));
            return;
        }

        int added = 0;

        foreach (string raw in BatchBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = raw.Trim();
            if (name.Length == 0 || !IsNameValid(name)) continue;

            parent.Children.Add(new TreeNode { Name = name });
            added++;
        }

        SetStatus(added > 0
            ? string.Format(LanguageManager.Tr("L_AddedFolders"), added)
            : LanguageManager.Tr("L_NoValidNames"));
        UpdatePreview();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } node) { SetStatus(LanguageManager.Tr("L_SelectFolder")); return; }

        string name = NodeNameBox.Text.Trim();
        if (!ValidateName(name)) return;

        node.Name = name;
        UpdatePreview();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } node) { SetStatus(LanguageManager.Tr("L_SelectFolder")); return; }

        if (ReferenceEquals(node, Root))
        {
            SetStatus(LanguageManager.Tr("L_RootCantDelete"));
            return;
        }

        RemoveNode(Root, node);
        UpdatePreview();
    }

    private static bool RemoveNode(TreeNode parent, TreeNode target)
    {
        if (parent.Children.Remove(target)) return true;

        foreach (TreeNode child in parent.Children)
            if (RemoveNode(child, target)) return true;

        return false;
    }

    /// <summary>Глубина узла относительно корня: у корня 0.</summary>
    private int DepthOf(TreeNode node) => DepthOf(Root, node, 0);

    private static int DepthOf(TreeNode current, TreeNode target, int depth)
    {
        if (ReferenceEquals(current, target)) return depth;

        foreach (TreeNode child in current.Children)
        {
            int found = DepthOf(child, target, depth + 1);
            if (found >= 0) return found;
        }

        return -1;
    }

    // ---------- проверка имён ----------

    private bool ValidateName(string name)
    {
        if (name.Length == 0)
        {
            SetStatus(LanguageManager.Tr("L_EnterFolderName"));
            return false;
        }

        if (!IsNameValid(name))
        {
            SetStatus(string.Format(LanguageManager.Tr("L_InvalidName"), name));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Проверка имени до создания папки. Кроме запрещённых символов отсекаются
    /// зарезервированные имена DOS (CON, PRN, COM1...): Windows до сих пор не даёт
    /// создать такую папку, и без проверки ошибка вылезла бы уже во время создания,
    /// оставив дерево наполовину созданным.
    /// </summary>
    private static bool IsNameValid(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        if (name is "." or "..") return false;
        if (name.EndsWith(' ') || name.EndsWith('.')) return false;

        string bare = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        string[] reserved =
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ];

        return !reserved.Contains(bare);
    }

    // ---------- предпросмотр и создание ----------

    private void Structure_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;

        Root.Name = RootNameBox.Text;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var text = new StringBuilder();
        Describe(Root, text, string.Empty, isLast: true, isRoot: true);

        PreviewText.Text = text.ToString();
    }

    /// <summary>Рисует дерево псевдографикой — тем же способом, что утилита tree.</summary>
    private static void Describe(TreeNode node, StringBuilder text, string prefix, bool isLast, bool isRoot)
    {
        if (isRoot)
        {
            text.AppendLine(node.Name + "\\");
        }
        else
        {
            text.Append(prefix).Append(isLast ? "\\-- " : "+-- ").AppendLine(node.Name);
            prefix += isLast ? "    " : "|   ";
        }

        for (int i = 0; i < node.Children.Count; i++)
            Describe(node.Children[i], text, prefix, i == node.Children.Count - 1, isRoot: false);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateName(Root.Name)) return;

        string rootPath = Path.Combine(_targetFolder, Root.Name);

        if (Directory.Exists(rootPath))
        {
            SetStatus(string.Format(LanguageManager.Tr("L_FolderExists"), Root.Name));
            return;
        }

        try
        {
            int created = CreateRecursive(Root, _targetFolder);
            SetStatus(string.Format(LanguageManager.Tr("L_CreatedFolders"), created));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            SetStatus(string.Format(LanguageManager.Tr("L_CreateError"), ex.Message));
        }
    }

    private static int CreateRecursive(TreeNode node, string parentPath)
    {
        string path = Path.Combine(parentPath, node.Name);
        Directory.CreateDirectory(path);

        int count = 1;
        foreach (TreeNode child in node.Children)
            count += CreateRecursive(child, path);

        return count;
    }

    // ---------- шаблоны ----------

    private void LoadTemplates()
    {
        _templates.Clear();

        try
        {
            if (File.Exists(TemplatesPath))
            {
                var loaded = JsonSerializer.Deserialize<List<FolderTemplate>>(
                    File.ReadAllText(TemplatesPath), JsonOptions);

                if (loaded is not null) _templates.AddRange(loaded);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        RefreshTemplateList();
    }

    private void RefreshTemplateList()
    {
        TemplateBox.ItemsSource = null;
        TemplateBox.ItemsSource = _templates.Select(t => t.Name).ToList();
    }

    private void SaveTemplates()
    {
        try
        {
            Directory.CreateDirectory(SessionStore.DirectoryPath);
            File.WriteAllText(TemplatesPath, JsonSerializer.Serialize(_templates, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(LanguageManager.Tr("L_SaveTemplatesError"));
        }
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        string name = Root.Name.Trim();
        if (name.Length == 0) { SetStatus(LanguageManager.Tr("L_SetRootForTemplate")); return; }

        var template = new FolderTemplate
        {
            Name = name,
            RootName = Root.Name,
            Children = [.. Root.Children.Select(ToTemplate)],
        };

        // Шаблон с таким именем заменяется: иначе список быстро зарастает дубликатами.
        _templates.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        _templates.Add(template);

        SaveTemplates();
        RefreshTemplateList();
        SetStatus(string.Format(LanguageManager.Tr("L_TemplateSaved"), name));
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not string name) { SetStatus(LanguageManager.Tr("L_SelectTemplate")); return; }

        _templates.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        SaveTemplates();
        RefreshTemplateList();
        SetStatus(string.Format(LanguageManager.Tr("L_TemplateDeleted"), name));
    }

    private void Template_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not string name) return;

        FolderTemplate? template = _templates.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        if (template is null) return;

        Root.Name = template.RootName;
        RootNameBox.Text = template.RootName;

        Root.Children.Clear();
        foreach (TemplateNode child in template.Children) Root.Children.Add(FromTemplate(child));

        UpdatePreview();
        SetStatus(string.Format(LanguageManager.Tr("L_TemplateLoaded"), name));
    }

    private static TemplateNode ToTemplate(TreeNode node) => new()
    {
        Name = node.Name,
        Children = [.. node.Children.Select(ToTemplate)],
    };

    private static TreeNode FromTemplate(TemplateNode node)
    {
        var result = new TreeNode { Name = node.Name };
        foreach (TemplateNode child in node.Children) result.Children.Add(FromTemplate(child));

        return result;
    }

    // ---------- окно ----------

    private void SetStatus(string text) => StatusText.Text = text;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
