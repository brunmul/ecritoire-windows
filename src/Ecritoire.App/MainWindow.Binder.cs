using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ecritoire.Core;
using Ecritoire.Pdf;
using Microsoft.Win32;

namespace Ecritoire.App;

/// <summary>
/// Classeur (Binder.swift) : créer, renommer, déplacer par glisser-déposer, icônes, mettre à la corbeille ; export
/// Markdown ou PDF ; inspecteur du document (titre, synopsis, statut, publication).
/// </summary>
public partial class MainWindow
{
    BinderNode? NodeOf(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TreeViewItem { Tag: BinderNode n }) return n;
        return null;
    }

    TreeViewItem? ItemOf(object? source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TreeViewItem { Tag: BinderNode } item) return item;
        return null;
    }

    BinderNode? SelectedNode => (Tree.SelectedItem as TreeViewItem)?.Tag as BinderNode;
    BinderNode? menuTarget;

    void TreeRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        menuTarget = NodeOf(e.OriginalSource);
    }

    // MARK: Menu contextuel

    void TreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (root == null) { e.Handled = true; return; }
        var node = menuTarget;
        var menu = Tree.ContextMenu!;
        menu.Items.Clear();
        MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true)
        {
            var m = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
            m.Click += (_, _) => Run(action);
            menu.Items.Add(m);
            return m;
        }
        Item("Nouveau document", () => NewDocument(node), "Ctrl+N");
        Item("Nouveau dossier", () => NewFolder(node), "Ctrl+Maj+N");
        if (node != null)
        {
            menu.Items.Add(new Separator());
            Item("Renommer…", () => RenameNode(node), "F2");
            var icons = new MenuItem { Header = "Icône" };
            var none = new MenuItem { Header = "Par défaut", IsChecked = node.Icon == null };
            none.Click += (_, _) => Run(() => SetIcon(node, null));
            icons.Items.Add(none);
            icons.Items.Add(new Separator());
            foreach (var (id, label) in BinderIcons.Labels)
            {
                var (iconName, color) = BinderIcons.For(id, false);
                var m = new MenuItem
                {
                    Header = label, IsChecked = node.Icon == id,
                    Icon = Icons.Make(iconName!, 16, color),
                };
                m.Click += (_, _) => Run(() => SetIcon(node, id));
                icons.Items.Add(m);
            }
            menu.Items.Add(icons);
            menu.Items.Add(new Separator());
            Item("Exporter…", () => Export(node.Path), "Ctrl+E");
            Item("Afficher dans l'Explorateur", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{node.Path}\""));
            menu.Items.Add(new Separator());
            Item("Mettre à la corbeille", () => TrashNode(node), "Suppr");
        }
        else
        {
            menu.Items.Add(new Separator());
            Item("Exporter tout le classeur…", () => Export(root));
        }
        menuTarget = null;
    }

    /// <summary>Exécute une opération du classeur ; une erreur est montrée sans fermer l'app.</summary>
    void Run(Action action)
    {
        try { action(); }
        catch (BinderException ex) { MessageBox.Show(this, ex.Message, "Écritoire", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { App.Log(ex); MessageBox.Show(this, ex.Message, "Écritoire", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void TreeKeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedNode is not { } node) return;
        if (e.Key == Key.F2) { Run(() => RenameNode(node)); e.Handled = true; }
        else if (e.Key == Key.Delete) { Run(() => TrashNode(node)); e.Handled = true; }
    }

    /// <summary>Dossier et position d'un nouvel élément : dans le dossier visé, ou juste après le document visé.</summary>
    (string Folder, int? Index) InsertionPoint(BinderNode? node)
    {
        if (node == null) return (root!, null);
        if (node.IsFolder) return (node.Path, null);
        var parent = Path.GetDirectoryName(node.Path)!;
        var names = Binder.OrderedEntries(parent);
        int i = names.FindIndex(p => Binder.SamePath(p, node.Path));
        return (parent, i < 0 ? null : i + 1);
    }

    void NewDocument(BinderNode? node)
    {
        if (root == null) return;
        Flush();
        var (folder, index) = InsertionPoint(node ?? SelectedNode);
        var path = BinderOperations.CreateDocument(folder, "Sans titre", index);
        ReloadTree();
        OpenDocument(path);
        SelectInTree(path);
    }

    void NewFolder(BinderNode? node)
    {
        if (root == null) return;
        var (folder, index) = InsertionPoint(node ?? SelectedNode);
        var path = BinderOperations.CreateFolder(folder, "Nouveau dossier", index);
        ReloadTree();
        SelectInTree(path);
        RenameNode(new BinderNodeRef(path, true));
    }

    /// <summary>Élément désigné par son chemin (avant que l'arbre soit relu).</summary>
    sealed record BinderNodeRef(string Path, bool IsFolder);

    void RenameNode(BinderNode node) => RenameNode(new BinderNodeRef(node.Path, node.IsFolder));

    void RenameNode(BinderNodeRef node)
    {
        if (root == null) return;
        var name = node.IsFolder ? Path.GetFileName(node.Path) : Path.GetFileNameWithoutExtension(node.Path);
        var answer = InputDialog.Ask(this, "Renommer", node.IsFolder ? "Nom du dossier" : "Nom du document", name);
        if (answer == null || answer.Trim() == name) return;
        Flush();
        var target = BinderOperations.Rename(node.Path, answer, root);
        Relocated(node.Path, target);
    }

    /// <summary>Un élément a changé de chemin : document ouvert, réglages et arbre suivent.</summary>
    void Relocated(string from, string to)
    {
        if (current != null && (Binder.SamePath(current.Path, from) || current.Path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            var newPath = to + current.Path[from.Length..];
            var caret = Editor.TextArea.Caret.Offset;
            OpenDocument(newPath);
            Editor.TextArea.Caret.Offset = Math.Min(caret, Editor.Document.TextLength);
        }
        if (root != null && settings.LastDocument.TryGetValue(root, out var last) && current != null) settings.LastDocument[root] = current.Path;
        RebuildIndex();
        ReloadTree();
        SelectInTree(current?.Path ?? to);
    }

    void SetIcon(BinderNode node, string? icon)
    {
        BinderOperations.SetIcon(node.Path, icon);
        ReloadTree();
    }

    void TrashNode(BinderNode node)
    {
        if (root == null) return;
        var name = node.DisplayName;
        if (MessageBox.Show(this, $"Mettre « {name} » à la corbeille ?{(node.IsFolder ? "\nLe dossier et tout son contenu iront dans la corbeille de Windows." : "")}",
                "Mettre à la corbeille", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Flush();
        bool closing = current != null && (Binder.SamePath(current.Path, node.Path) || current.Path.StartsWith(node.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (closing) CloseDocument();
        // Corbeille de Windows (jamais de suppression définitive).
        if (node.IsFolder)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(node.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(node.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        BinderOperations.Trashed(node.Path);
        RebuildIndex();
        ReloadTree();
    }

    void CloseDocument()
    {
        Flush();
        current = null;
        review = null;
        Editor.IsReadOnly = false;
        Editor.Visibility = Visibility.Collapsed;
        DocumentTitle.Text = "Écritoire";
        DocumentPath.Text = "";
        Title = "Écritoire";
        WordCount.Text = "";
        comments.Load(null, null, "");
        pendingChanges = null;
        UpdateChangesUI();
        UpdateInspector();
        RebuildOutline();
        RefreshRecents();
        UpdateAiBar();
    }

    void RebuildIndex()
    {
        var rebuilt = index;
        if (rebuilt != null) Task.Run(() => { try { rebuilt.Build(); } catch (Exception ex) { App.Log(ex); } });
    }

    // MARK: Glisser-déposer

    Point dragStart;
    BinderNode? dragged;

    void TreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(Tree);
        dragged = NodeOf(e.OriginalSource);
    }

    void TreeMouseMove(object sender, MouseEventArgs e)
    {
        if (dragged == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Tree);
        if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = dragged;
        dragged = null;
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(BinderNode), node), DragDropEffects.Move);
        ClearDropMarker();
    }

    TreeViewItem? markedItem;

    void ClearDropMarker()
    {
        if (markedItem?.Header is FrameworkElement h) { h.SetValue(Border.BorderThicknessProperty, new Thickness(0)); }
        if (markedItem != null) markedItem.Background = Brushes.Transparent;
        markedItem = null;
    }

    /// <summary>Où tomberait l'élément : dans un dossier (au milieu de sa ligne), sinon avant ou après l'élément survolé.</summary>
    (string Folder, int? Index, TreeViewItem? Item)? DropTarget(DragEventArgs e, BinderNode moving)
    {
        var item = ItemOf(e.OriginalSource);
        if (item?.Tag is not BinderNode target)
            return root == null ? null : (root, null, null);
        if (Binder.SamePath(target.Path, moving.Path)) return null;
        var header = item.Template.FindName("PART_Header", item) as FrameworkElement ?? item;
        double y = e.GetPosition(header).Y, h = Math.Max(1, header.ActualHeight);
        if (target.IsFolder && y > h * 0.25 && y < h * 0.75) return (target.Path, null, item);
        var parent = Path.GetDirectoryName(target.Path)!;
        int i = Binder.OrderedEntries(parent).FindIndex(p => Binder.SamePath(p, target.Path));
        return (parent, i < 0 ? null : (y >= h / 2 ? i + 1 : i), item);
    }

    void TreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(BinderNode)) is BinderNode moving && DropTarget(e, moving) is { } t)
        {
            e.Effects = DragDropEffects.Move;
            if (t.Item != markedItem)
            {
                ClearDropMarker();
                markedItem = t.Item;
                if (markedItem != null) markedItem.Background = new SolidColorBrush(Color.FromArgb(0x26, 0x7B, 0x5F, 0xBF));
            }
        }
        e.Handled = true;
    }

    void TreeDrop(object sender, DragEventArgs e)
    {
        ClearDropMarker();
        if (e.Data.GetData(typeof(BinderNode)) is not BinderNode moving || DropTarget(e, moving) is not { } t || root == null) return;
        Run(() =>
        {
            Flush();
            var target = BinderOperations.MoveItem(moving.Path, t.Folder, t.Index, root);
            if (!Binder.SamePath(target, moving.Path)) Relocated(moving.Path, target);
            else ReloadTree();
        });
        e.Handled = true;
    }

    // MARK: Export

    void ExportClick(object sender, RoutedEventArgs e)
    {
        var target = SelectedNode?.Path ?? current?.Path ?? root;
        if (target != null) Run(() => Export(target));
    }

    void Export(string path)
    {
        if (root == null) return;
        Flush();
        bool folder = Directory.Exists(path);
        string title;
        if (folder) title = Path.GetFileName(path.TrimEnd('\\', '/'));
        else
        {
            try { title = DocumentFile.Load(path).DisplayTitle; } catch (IOException) { title = Path.GetFileNameWithoutExtension(path); }
        }
        var dialog = new SaveFileDialog
        {
            Title = $"Exporter « {title} »",
            Filter = "PDF (*.pdf)|*.pdf|Markdown (*.md)|*.md",
            FileName = string.Concat(title.Select(ch => "/\\:*?\"<>|".Contains(ch) ? '-' : ch)).Trim(),
            AddExtension = true,
            InitialDirectory = settings.ExportFolder is { } f && Directory.Exists(f) ? f : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        var destination = dialog.FileName;
        var full = Path.GetFullPath(destination);
        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Choisissez un emplacement hors du classeur : l'export ne doit pas se mêler à vos documents.", "Exporter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        settings.ExportFolder = Path.GetDirectoryName(full);
        bool pdf = string.Equals(Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase);
        SaveState.Text = "Export en cours…";
        var binderRoot = root;
        if (pdf) PdfExport.RegisterFonts(OpenFont); // ressources de l'app : lues sur le fil de l'interface
        Task.Run(() =>
        {
            var sections = Core.Export.Sections(path, binderRoot);
            if (pdf) PdfExport.Write(sections, title, destination);
            else Core.Export.WriteMarkdown(sections, destination);
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.IsFaulted)
            {
                App.Log(t.Exception!);
                SaveState.Text = "Export impossible";
                MessageBox.Show(this, "L'export n'a pas pu être écrit : " + t.Exception!.InnerException?.Message, "Exporter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SaveState.Text = $"Exporté : {Path.GetFileName(destination)}";
        }), TaskScheduler.Default);
    }

    /// <summary>Polices embarquées dans l'app (Walbaum dans une construction personnelle, sinon polices libres).</summary>
    static Stream? OpenFont(string file)
    {
        // Polices personnelles (Walbaum) d'abord, puis celles embarquées dans l'app.
        var personal = Path.Combine(Theme.PersonalFonts, file);
        if (File.Exists(personal)) return File.OpenRead(personal);
        try { return Application.GetResourceStream(new Uri("pack://application:,,,/Polices/" + file))?.Stream; }
        catch (IOException) { return null; }
    }

    // MARK: Inspecteur

    bool fillingInspector;

    void InspectorToggleClick(object sender, RoutedEventArgs e)
    {
        settings.InspectorVisible = !settings.InspectorVisible;
        UpdateInspector();
        UpdateRightPanel();
    }

    void UpdateInspector()
    {
        UpdateRightPanel();
        if (current == null) return;
        fillingInspector = true;
        TitleBox.Text = current.Title;
        SynopsisBox.Text = current.Synopsis;
        StatusBox.Items.Clear();
        var choices = DocumentStatus.All.ToList();
        if (!choices.Contains(current.Status)) choices.Add(current.Status);
        foreach (var s in choices)
            StatusBox.Items.Add(new ComboBoxItem { Content = s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s, Tag = s, IsSelected = s == current.Status });
        PublishBox.IsChecked = current.Publish;
        FileNameText.Text = Path.GetFileName(current.Path);
        FileWordsText.Text = WordCount.Text;
        fillingInspector = false;
    }

    void InspectorChanged(object sender, RoutedEventArgs e)
    {
        if (fillingInspector || current == null || review != null) return;
        current.Title = TitleBox.Text;
        current.Synopsis = SynopsisBox.Text.Replace("\r\n", " ").Replace('\n', ' ');
        if (StatusBox.SelectedItem is ComboBoxItem { Tag: string status }) current.Status = status;
        current.Publish = PublishBox.IsChecked == true;
        DocumentTitle.Text = current.DisplayTitle;
        Title = current.DisplayTitle + " — Écritoire";
        TextEdited(true);
    }
}
