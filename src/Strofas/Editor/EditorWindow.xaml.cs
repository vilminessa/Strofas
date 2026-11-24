using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Strofas.Core;
using Strofas.Menu;
using Strofas.Models;

namespace Strofas.Editor;

/// <summary>GUI-редактор menu.json: дерево, свойства, живой предпросмотр круга.</summary>
public sealed partial class EditorWindow : Window
{
    /// <summary>Вызывается после «Сохранить» — приложение перезагружает меню.</summary>
    public event Action<MenuConfig>? ConfigApplied;

    private MenuConfig _working;
    private readonly RadialMenuRenderer _preview;
    private MenuItemConfig? _selected;
    private bool _loading;

    public EditorWindow(MenuConfig cfg)
    {
        InitializeComponent();
        _working = ConfigService.Clone(cfg);
        _preview = new RadialMenuRenderer(PreviewCanvas, 480, 10, _ => false);

        HotkeyBox.Text = _working.Hotkey;
        SizeBox.Text = _working.MenuSize.ToString();
        CloseAfterBox.IsChecked = _working.CloseAfterAction;

        RebuildTree(null);
        RefreshPreview();
    }

    // ---------- Дерево ----------

    private TreeViewItem MakeNode(MenuItemConfig item)
    {
        var node = new TreeViewItem
        {
            Header = HeaderFor(item),
            Tag = item,
            IsExpanded = true,
        };
        if (item.Children is not null)
            foreach (var child in item.Children)
                node.Items.Add(MakeNode(child));
        return node;
    }

    private static string HeaderFor(MenuItemConfig item)
        => item.IsCategory ? $"{item.Label}  ({item.Children!.Count})" : item.Label;

    private void RebuildTree(MenuItemConfig? select)
    {
        _loading = true;
        Tree.Items.Clear();
        foreach (var item in _working.Items)
            Tree.Items.Add(MakeNode(item));
        _selected = null;
        if (select is not null && TrySelect(Tree, select))
        {
            _selected = select;
            LoadFields();
        }
        else
        {
            LoadFields();
        }
        _loading = false;
    }

    private static bool TrySelect(ItemsControl parent, MenuItemConfig target)
    {
        foreach (TreeViewItem node in parent.Items)
        {
            if (ReferenceEquals(node.Tag, target))
            {
                node.IsSelected = true;
                node.BringIntoView();
                return true;
            }
            if (TrySelect(node, target)) return true;
        }
        return false;
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_loading) return;
        _selected = (e.NewValue as TreeViewItem)?.Tag as MenuItemConfig;
        LoadFields();
    }

    // ---------- Поля свойств ----------

    private void LoadFields()
    {
        _loading = true;
        var it = _selected;
        LabelBox.Text = it?.Label ?? "";
        IconBox.Text = it?.Icon ?? "";
        SelectActionType(it?.Action?.Type ?? "");
        PathBox.Text = it?.Action?.Path ?? "";
        ArgsBox.Text = it?.Action?.Args ?? "";
        WorkDirBox.Text = it?.Action?.WorkingDir ?? "";
        ProcessBox.Text = it?.Action?.ProcessName ?? "";
        HiddenBox.IsChecked = it?.Action?.Hidden ?? false;
        RepoBox.Text = it?.Action?.Repo ?? "";
        TagBox.Text = string.IsNullOrWhiteSpace(it?.Action?.Tag) ? "latest" : it!.Action!.Tag!;
        AssetBox.Text = it?.Action?.Asset ?? "";
        AutoUpdateBox.IsChecked = it?.Action?.AutoUpdate ?? true;
        ShaBox.Text = it?.Action?.Sha256 ?? "";
        _loading = false;
        UpdateTypeVisibility();
    }

    /// <summary>Поля пути скрываются для github-пунктов (и наоборот).</summary>
    private void UpdateTypeVisibility()
    {
        var isGithub = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as string == "github";
        var visible = isGithub ? Visibility.Collapsed : Visibility.Visible;
        PathRow.Visibility = visible;
        PathBoxRow.Visibility = visible;
        WorkDirRow.Visibility = visible;
        WorkDirBoxRow.Visibility = visible;
        GithubPanel.Visibility = isGithub ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectActionType(string type)
    {
        foreach (ComboBoxItem item in ActionTypeBox.Items)
        {
            if ((item.Tag as string) == type)
            {
                ActionTypeBox.SelectedItem = item;
                return;
            }
        }
        ActionTypeBox.SelectedIndex = ActionTypeBox.Items.Count - 1;
    }

    private void OnFieldChanged(object sender, EventArgs e)
    {
        if (ReferenceEquals(sender, ActionTypeBox))
            UpdateTypeVisibility();

        if (_loading || _selected is null) return;

        var it = _selected;
        it.Label = LabelBox.Text;
        it.Icon = NullIfEmpty(IconBox.Text);

        var type = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (string.IsNullOrEmpty(type))
        {
            it.Action = null;
        }
        else
        {
            it.Action ??= new ActionConfig();
            it.Action.Type = type;
            it.Action.Path = NullIfEmpty(PathBox.Text);
            it.Action.Args = NullIfEmpty(ArgsBox.Text);
            it.Action.WorkingDir = NullIfEmpty(WorkDirBox.Text);
            it.Action.ProcessName = NullIfEmpty(ProcessBox.Text);
            it.Action.Hidden = HiddenBox.IsChecked == true;

            // github
            it.Action.Repo = NullIfEmpty(RepoBox.Text);
            it.Action.Tag = string.IsNullOrWhiteSpace(TagBox.Text) ? "latest" : TagBox.Text.Trim();
            it.Action.Asset = NullIfEmpty(AssetBox.Text);
            it.Action.AutoUpdate = AutoUpdateBox.IsChecked ?? true;
            it.Action.Sha256 = NullIfEmpty(ShaBox.Text);
        }

        if (Tree.SelectedItem is TreeViewItem node)
            node.Header = HeaderFor(it);

        RefreshPreview();
    }

    /// <summary>«Проверить»: парсим ссылку, тянем последний тег и список ассетов.</summary>
    private async void OnCheckRepo(object sender, RoutedEventArgs e)
    {
        if (!GitHubRelease.TryParseRepo(RepoBox.Text, out var repo))
        {
            StatusText.Text = "Ссылка не распознана: нужен owner/repo или https://github.com/owner/repo/releases";
            return;
        }

        CheckButton.IsEnabled = false;
        StatusText.Text = "Проверяю релизы…";
        try
        {
            var tag = await GitHubRelease.GetLatestTagAsync(repo, CancellationToken.None)
                ?? throw new InvalidOperationException("у репозитория нет релизов");
            var assets = await GitHubRelease.GetAssetsAsync(repo, "latest", CancellationToken.None);

            RepoBox.Text = repo; // нормализуем к owner/repo
            TagBox.Text = tag;
            AssetBox.ItemsSource = assets.Select(a => a.Name).ToList();

            // подсказка: если exe в релизе ровно один — подставляем
            var exes = assets
                .Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Name)
                .ToList();
            if (string.IsNullOrWhiteSpace(AssetBox.Text))
            {
                if (exes.Count == 1) AssetBox.Text = exes[0];
                else if (assets.Count == 1) AssetBox.Text = assets[0].Name;
            }

            StatusText.Text = $"последний тег {tag} · ассетов: {assets.Count} — выберите exe и «Сохранить»";
            OnFieldChanged(AssetBox, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка проверки: " + ex.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------- Структурные операции ----------

    private void OnAddLeaf(object sender, RoutedEventArgs e)
        => AddItem(new MenuItemConfig
        {
            Label = "Новый пункт",
            Action = new ActionConfig { Type = "launch" },
        });

    private void OnAddCategory(object sender, RoutedEventArgs e)
        => AddItem(new MenuItemConfig
        {
            Label = "Новая категория",
            Children = new List<MenuItemConfig>(),
        });

    private void AddItem(MenuItemConfig item)
    {
        var list = _selected is { IsCategory: true } ? _selected.Children! : _working.Items;
        list.Add(item);
        RebuildTree(item);
        RefreshPreview();
        StatusText.Text = $"Добавлено: {item.Label} (не забудьте «Сохранить»)";
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!Find(_working.Items, _selected, out var parent, out var index)) return;
        var name = _selected.Label;
        parent.RemoveAt(index);
        _selected = null;
        RebuildTree(null);
        RefreshPreview();
        StatusText.Text = $"Удалено: {name} (не забудьте «Сохранить»)";
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        if (_selected is null) return;
        if (!Find(_working.Items, _selected, out var parent, out var index)) return;
        var target = index + delta;
        if (target < 0 || target >= parent.Count) return;
        (parent[index], parent[target]) = (parent[target], parent[index]);
        RebuildTree(_selected);
        RefreshPreview();
    }

    private static bool Find(List<MenuItemConfig> list, MenuItemConfig target,
        out List<MenuItemConfig> parent, out int index)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], target))
            {
                parent = list;
                index = i;
                return true;
            }

            var children = list[i].Children;
            if (children is not null && Find(children, target, out parent, out index))
                return true;
        }

        parent = null!;
        index = -1;
        return false;
    }

    // ---------- Предпросмотр ----------

    private void RefreshPreview()
    {
        var items = new List<MenuItemVm>(_working.Items.Count);
        for (var i = 0; i < _working.Items.Count; i++)
            items.Add(new MenuItemVm(i.ToString(), _working.Items[i]));
        _preview.SetRoot(items);
        _preview.SetCenter("Strofas", "", null);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var hit = _preview.HitTest(e.GetPosition(PreviewCanvas));
        _preview.SetHover(hit);

        var main = "Strofas";
        if (hit.Kind == HitKind.Outer && hit.Index < _working.Items.Count)
            main = string.IsNullOrWhiteSpace(_working.Items[hit.Index].Label)
                ? "?"
                : _working.Items[hit.Index].Label;
        _preview.SetCenter(main, "", null);
    }

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e)
    {
        _preview.SetHover(MenuHit.Miss);
        _preview.SetCenter("Strofas", "", null);
    }

    // ---------- Сохранение ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!HotkeyService.TryParse(HotkeyBox.Text, out _, out _))
        {
            MessageBox.Show(this,
                "Горячка распознана неправильно. Примеры: Ctrl+B, Ctrl+Alt+Space, F12",
                "Strofas", MessageBoxButton.OK, MessageBoxImage.Warning);
            HotkeyBox.Focus();
            HotkeyBox.SelectAll();
            return;
        }

        if (!int.TryParse(SizeBox.Text, out var size) || size is < 400 or > 1200)
        {
            MessageBox.Show(this, "Размер меню — число от 400 до 1200.", "Strofas",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SizeBox.Focus();
            SizeBox.SelectAll();
            return;
        }

        _working.Hotkey = HotkeyBox.Text.Trim();
        _working.MenuSize = size;
        _working.CloseAfterAction = CloseAfterBox.IsChecked == true;

        ConfigService.Save(_working);
        ConfigApplied?.Invoke(_working);

        // отвязываемся от сохранённого объекта: дальнейшие правки не должны
        // мутировать конфиг, который уже держит менюшка
        _working = ConfigService.Clone(_working);
        RebuildTree(null);
        RefreshPreview();
        StatusText.Text = $"Сохранено · {DateTime.Now:HH:mm:ss}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- Диалоги выбора ----------

    private void OnBrowseIcon(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Иконки|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.exe;*.dll|Все файлы|*.*",
        };
        if (dlg.ShowDialog(this) == true) IconBox.Text = dlg.FileName;
    }

    private void OnBrowsePath(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Программы и файлы|*.exe;*.bat;*.cmd;*.msc;*.lnk;*.url;*.ms-settings|Все файлы|*.*",
        };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
    }

    private void OnBrowseWorkDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog(this) == true) WorkDirBox.Text = dlg.FolderName;
    }
}
