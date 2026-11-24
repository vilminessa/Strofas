using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        // путь указан, а подпись пустая/заглушка — подтягиваем имя файла
        if (ReferenceEquals(sender, PathBox))
        {
            var placeholder = string.IsNullOrWhiteSpace(it.Label) || it.Label == "Новый пункт";
            var name = NameFromPath(PathBox.Text);
            if (placeholder && name is not null)
            {
                it.Label = name;
                LabelBox.Text = name;
            }
        }

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

    // ---------- Drag & drop файлов из проводника ----------

    private void OnFilesDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop)
                 && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 };
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Перетащили файлы — создаём пункты: имя из файла, путь, иконка автоматически.</summary>
    private void OnFilesDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        // куда вставлять: выбранная категория, иначе корень (как «+ Пункт»)
        var list = _selected is { IsCategory: true } ? _selected.Children! : _working.Items;
        MenuItemConfig? last = null;
        foreach (var path in files)
        {
            var item = ItemFromFile(path);
            if (item is null) continue;
            list.Add(item);
            last = item;
        }
        if (last is null)
        {
            StatusText.Text = "Нет файлов для добавления";
            e.Handled = true;
            return;
        }

        RebuildTree(last);
        RefreshPreview();
        StatusText.Text = files.Length == 1
            ? $"Добавлено: {last.Label} (не забудьте «Сохранить»)"
            : $"Добавлено пунктов: {files.Length} (не забудьте «Сохранить»)";
        e.Handled = true;
    }

    /// <summary>Пункт из перетащенного файла: ярлык разрешается до цели, exe/dll/файл/папка.</summary>
    private static MenuItemConfig? ItemFromFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                // ярлык .lnk — берём цель, чтобы получить иконку и имя реальной программы
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ResolveShortcut(path);
                    if (target is not null) path = target;
                }

                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name)) name = path;

                return new MenuItemConfig
                {
                    Label = name,
                    Action = new ActionConfig { Type = "launch", Path = path },
                };
            }

            if (System.IO.Directory.Exists(path))
            {
                return new MenuItemConfig
                {
                    Label = new System.IO.DirectoryInfo(path).Name,
                    Action = new ActionConfig { Type = "launch", Path = path },
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Drop: {ex.Message}");
        }
        return null;
    }

    /// <summary>Цель ярлыка .lnk через WScript.Shell; null — не разрешился.</summary>
    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            var shell = Activator.CreateInstance(t);
            if (shell is null) return null;
            var shortcut = t.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            var target = shortcut?.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Имя файла без расширения из строки пути; не-файловые пути (URL и т.п.) → null.</summary>
    private static string? NameFromPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var p = raw.Trim();
        if (p.Contains("://") || p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(p);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (ArgumentException)
        {
            return null; // недопустимые символы в пути
        }
    }


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

    // ---------- Drag & drop узлов в дереве ----------

    private MenuItemConfig? _dragItem;
    private Point _pressPoint;
    private TreeViewItem? _dropHintTarget;
    private bool _dropHintInto;

    private void OnTreeMouseLeftDown(object sender, MouseButtonEventArgs e)
        => _pressPoint = e.GetPosition(Tree);

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is not null) return;
        var p = e.GetPosition(Tree);
        if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumHorizontalDragDistance)
            return;

        var container = ContainerAt(p);
        if (container?.Tag is not MenuItemConfig item) return;

        _dragItem = item;
        try
        {
            DragDrop.DoDragDrop(Tree, new DataObject("StrofasItem", item.Label), DragDropEffects.Move);
        }
        finally
        {
            _dragItem = null;
            ClearDropHint();
        }
    }

    private TreeViewItem? ContainerAt(Point p)
    {
        var hit = VisualTreeHelper.HitTest(Tree, p)?.VisualHit;
        return hit is null ? null : ItemsControl.ContainerFromElement(Tree, hit) as TreeViewItem;
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        if (_dragItem is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var p = e.GetPosition(Tree);
        var container = ContainerAt(p);
        if (container?.Tag is not MenuItemConfig target)
        {
            // пустое место под деревом — в конец корня
            SetDropHint(null, false);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        if (!CanDropInto(_dragItem, target))
        {
            ClearDropHint();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        // зоны: категория — середина = «внутрь», края = перед/после; пункт — верх/низ
        var rel = p.Y / Math.Max(1, container.ActualHeight);
        var into = target.IsCategory && rel is >= 0.25 and <= 0.75;
        SetDropHint(container, into);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave дёргается и при попадании на дочерние визуалы — подсветку
        // сбрасываем только когда курсор реально ушёл с области Tree
        if (!Tree.IsMouseOver) ClearDropHint();
    }

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        var item = _dragItem;
        if (item is null) { ClearDropHint(); return; }

        var p = e.GetPosition(Tree);
        var container = ContainerAt(p);
        _lastDropPoint = p;

        if (container?.Tag is MenuItemConfig target && !ReferenceEquals(target, item))
        {
            if (!CanDropInto(item, target)) { ClearDropHint(); return; }

            var rel = p.Y / Math.Max(1, container.ActualHeight);
            var into = target.IsCategory && rel is >= 0.25 and <= 0.75;
            if (!Move(item, target, into)) { ClearDropHint(); return; }
        }
        else if (container is null)
        {
            // пустое место — в конец корня
            if (!Find(_working.Items, item, out var srcParent, out var srcIndex)) { ClearDropHint(); return; }
            srcParent.RemoveAt(srcIndex);
            _working.Items.Add(item);
        }
        else
        {
            ClearDropHint();
            return;
        }

        _dragItem = null;
        ClearDropHint();
        RebuildTree(item);
        RefreshPreview();
        StatusText.Text = $"Перемещено: {item.Label} (не забудьте «Сохранить»)";
        e.Handled = true;
    }

    /// <summary>Перемещение item в цель: into=true — в конец категории, иначе до/после по половине узла.</summary>
    private bool Move(MenuItemConfig item, MenuItemConfig target, bool into)
    {
        if (!Find(_working.Items, item, out var srcParent, out var srcIndex)) return false;

        if (into)
        {
            if (!target.IsCategory) return false;
            srcParent.RemoveAt(srcIndex);
            target.Children!.Add(item);
            return true;
        }

        // позиция цели до удаления источника; если списки одинаковые и источник раньше — сдвигаем
        if (!Find(_working.Items, target, out var dstParent, out var dstIndex)) return false;
        var sameList = ReferenceEquals(srcParent, dstParent);
        srcParent.RemoveAt(srcIndex);
        if (sameList && srcIndex < dstIndex) dstIndex--;
        // dstIndex — индекс цели; offset 0 = перед ней, 1 = после
        dstIndex = Math.Clamp(dstIndex + OffsetFromPoint(target), 0, dstParent.Count);
        dstParent.Insert(dstIndex, item);
        return true;
    }

    /// <summary>0 — верхняя половина целевого узла (вставить перед), 1 — нижняя (после).</summary>
    private int OffsetFromPoint(MenuItemConfig target)
    {
        var container = ContainerFromItem(target);
        if (container is null) return 1;
        var rel = _lastDropPoint.Y / Math.Max(1, container.ActualHeight);
        return rel < 0.5 ? 0 : 1;
    }

    private Point _lastDropPoint;

    private TreeViewItem? ContainerFromItem(MenuItemConfig item)
    {
        foreach (var root in Tree.Items)
        {
            if (root is TreeViewItem node && TryFindNode(node, item, out var found)) return found;
        }
        return null;
    }

    private static bool TryFindNode(TreeViewItem node, MenuItemConfig target, out TreeViewItem found)
    {
        if (ReferenceEquals(node.Tag, target)) { found = node; return true; }
        foreach (var child in node.Items)
        {
            if (child is TreeViewItem c && TryFindNode(c, target, out found)) return true;
        }
        found = null!;
        return false;
    }

    /// <summary>Куда нельзя дропать: в самого себя и в собственных потомков.</summary>
    private static bool CanDropInto(MenuItemConfig drag, MenuItemConfig target)
        => !Contains(drag, target);

    private static bool Contains(MenuItemConfig root, MenuItemConfig? target)
    {
        if (target is null) return false;
        if (ReferenceEquals(root, target)) return true;
        if (root.Children is null) return false;
        return root.Children.Any(c => Contains(c, target));
    }

    private void SetDropHint(TreeViewItem? container, bool into)
    {
        if (ReferenceEquals(_dropHintTarget, container) && _dropHintInto == into) return;
        ClearDropHint();
        _dropHintTarget = container;
        _dropHintInto = into;
        if (container is null) return;
        if (container.Template.FindName("item", container) is System.Windows.Controls.Border bd)
        {
            bd.Background = into
                ? new SolidColorBrush(Color.FromArgb(0x66, 0x7C, 0x9B, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x33, 0x7C, 0x9B, 0xFF));
        }
    }

    private void ClearDropHint()
    {
        if (_dropHintTarget is not null
            && _dropHintTarget.Template.FindName("item", _dropHintTarget) is System.Windows.Controls.Border bd)
        {
            bd.Background = Brushes.Transparent;
        }
        _dropHintTarget = null;
    }

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
