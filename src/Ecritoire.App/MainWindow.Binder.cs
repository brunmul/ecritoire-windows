using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        if (renameBox != null || SelectedNode is not { } node) return; // touches du champ de renommage
        if (e.Key == Key.F2) { BeginRename(node.Path); e.Handled = true; }
        else if (e.Key == Key.Delete) { Run(() => TrashNode(node)); e.Handled = true; }
    }

    /// <summary>Dossier et position (dans l'ordre affiché) d'un nouvel élément : dans le dossier visé, ou juste après
    /// le document visé.</summary>
    (string Folder, int? Index) InsertionPoint(BinderNode? node)
    {
        if (node == null) return (root!, null);
        if (node.IsFolder) return (node.Path, null);
        var parent = Path.GetDirectoryName(node.Path)!;
        int i = Binder.Arranged(parent).FindIndex(p => Binder.SamePath(p, node.Path));
        return (parent, i < 0 ? null : i + 1);
    }

    void NewDocument(BinderNode? node)
    {
        if (root == null) return;
        Flush();
        var (folder, index) = InsertionPoint(node ?? SelectedNode);
        var path = BinderOperations.CreateDocument(folder, "Sans titre");
        BinderOperations.Place(path, folder, index);
        ReloadTree();
        OpenDocument(path);
        SelectInTree(path);
    }

    void NewFolder(BinderNode? node)
    {
        if (root == null) return;
        var (folder, index) = InsertionPoint(node ?? SelectedNode);
        var path = BinderOperations.CreateFolder(folder, "Nouveau dossier");
        BinderOperations.Place(path, folder, index);
        ReloadTree();
        BeginRename(path);
    }

    void RenameNode(BinderNode node) => BeginRename(node.Path);

    // MARK: Renommage sur place

    TextBox? renameBox;
    string? pendingRename;
    readonly System.Windows.Threading.DispatcherTimer renameTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    TreeViewItem? ItemFor(string path)
    {
        TreeViewItem? Search(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is not BinderNode n) continue;
                if (Binder.SamePath(n.Path, path)) return item;
                if (item.IsExpanded && Search(item) is { } hit) return hit;
            }
            return null;
        }
        return Search(Tree);
    }

    /// <summary>Le nom de l'élément devient un champ éditable : Entrée valide, Échap annule, cliquer ailleurs valide.</summary>
    void BeginRename(string path)
    {
        renameTimer.Stop();
        pendingRename = null;
        if (root == null || renameBox != null) return;
        var item = ItemFor(path);
        if (item == null) { SelectInTree(path); item = ItemFor(path); }
        if (item is not { Tag: BinderNode node, Header: DockPanel header } || header.Children.OfType<TextBlock>().LastOrDefault() is not { } label) return;
        item.BringIntoView();
        var name = node.DisplayName;
        var box = new TextBox
        {
            Style = (Style)FindResource("ChampNu"), Text = name, FontSize = label.FontSize, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 1, 2, 1), Margin = new Thickness(-3, 0, 0, 0), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("Accent"), Background = Brushes.White,
        };
        int at = header.Children.IndexOf(label);
        header.Children.RemoveAt(at);
        header.Children.Insert(at, box);
        renameBox = box;
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            var answer = box.Text.Trim();
            renameBox = null;
            header.Children.Remove(box);
            header.Children.Insert(at, label);
            if (!commit || answer.Length == 0 || answer == name) { item.Focus(); return; }
            Run(() =>
            {
                Flush();
                var target = BinderOperations.Rename(node.Path, answer, root);
                Relocated(node.Path, target);
            });
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Tab) e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
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
        ReloadTree(from, to);
        if (current != null) SelectInTree(current.Path);
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

    static bool Within<T>(object? source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    void TreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragged = null;
        renameTimer.Stop();
        pendingRename = null;
        // Champ de renommage (sélection du texte à la souris) et chevron des dossiers : pas de glisser.
        if (Within<TextBox>(e.OriginalSource) || Within<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource)) return;
        dragStart = e.GetPosition(Tree);
        dragged = NodeOf(e.OriginalSource);
        // Clic sur le nom d'un élément déjà sélectionné : renommage, sauf si un double-clic ou un glisser suit.
        if (e.ClickCount == 1 && dragged != null && e.OriginalSource is TextBlock { Tag: "nom" } && ItemOf(e.OriginalSource) is { IsSelected: true })
        {
            pendingRename = dragged.Path;
            renameTimer.Start();
        }
    }

    void TreeMouseMove(object sender, MouseEventArgs e)
    {
        if (dragged == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Tree);
        if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = dragged;
        dragged = null;
        renameTimer.Stop();
        pendingRename = null;
        try { DragDrop.DoDragDrop(Tree, new DataObject(typeof(BinderNode), node), DragDropEffects.Move); }
        finally { ClearDropMarker(); }
    }

    /// <summary>Repère du dépôt, dessiné par-dessus l'arbre : un trait d'insertion entre deux lignes, ou un cadre
    /// autour du dossier qui recevra l'élément.</summary>
    sealed class DropMarker : Adorner
    {
        readonly Pen line, frame;
        readonly Brush fill;
        public Rect? Box;
        public Point? From;
        public double To;

        public DropMarker(UIElement tree, Brush accent) : base(tree)
        {
            IsHitTestVisible = false;
            line = new Pen(accent, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            frame = new Pen(accent, 1.5);
            var c = (accent as SolidColorBrush)?.Color ?? Colors.SlateBlue;
            fill = new SolidColorBrush(Color.FromArgb(0x1F, c.R, c.G, c.B));
            line.Freeze(); frame.Freeze(); fill.Freeze();
        }

        protected override void OnRender(DrawingContext dc) => Draw(dc);

        public void Draw(DrawingContext dc)
        {
            if (Box is { } b) dc.DrawRoundedRectangle(fill, frame, b, 8, 8);
            if (From is { } p)
            {
                dc.DrawEllipse(Brushes.White, line, p, 3.5, 3.5);
                dc.DrawLine(line, new Point(p.X + 3.5, p.Y), new Point(To, p.Y));
            }
        }
    }

    DropMarker? dropMarker;

    void ShowDropMarker(Rect? box, Point? from)
    {
        if (dropMarker == null)
        {
            if (AdornerLayer.GetAdornerLayer(Tree) is not { } layer) return;
            dropMarker = new DropMarker(Tree, (Brush)FindResource("Accent"));
            layer.Add(dropMarker);
        }
        if (dropMarker.Box == box && dropMarker.From == from) return;
        dropMarker.Box = box;
        dropMarker.From = from;
        dropMarker.To = Tree.ActualWidth - 12;
        dropMarker.InvalidateVisual();
    }

    void ClearDropMarker()
    {
        if (dropMarker != null) AdornerLayer.GetAdornerLayer(Tree)?.Remove(dropMarker);
        dropMarker = null;
    }

    /// <summary>Lignes visibles de l'arbre, de haut en bas, avec leur rectangle (coordonnées de l'arbre).</summary>
    List<(TreeViewItem Item, BinderNode Node, Rect Bounds)> VisibleRows()
    {
        var rows = new List<(TreeViewItem, BinderNode, Rect)>();
        void Walk(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is not BinderNode node || item.Template?.FindName("Ligne", item) is not FrameworkElement line || !line.IsVisible) continue;
                rows.Add((item, node, new Rect(line.TranslatePoint(new Point(0, 0), Tree), new Size(line.ActualWidth, line.ActualHeight))));
                if (item.IsExpanded) Walk(item);
            }
        }
        Walk(Tree);
        return rows;
    }

    static int Depth(BinderNode node)
    {
        int depth = 0;
        for (var p = node.Parent; p?.Parent != null; p = p.Parent) depth++;
        return depth;
    }

    /// <summary>
    /// Où tomberait l'élément. Au milieu de la ligne d'un dossier : dedans, à la fin de sa partie. Sur la moitié haute
    /// d'une ligne : juste avant elle ; sur la moitié basse : juste après (ou en tête d'un dossier ouvert). La position
    /// est ramenée comme le fera <see cref="BinderOperations.Place"/> (documents d'abord), et le repère montre
    /// l'endroit réel.
    /// </summary>
    (string Folder, int Index, Rect? Box, Point? Line)? DropTarget(Point p, BinderNode moving)
    {
        if (root == null) return null;
        var rows = VisibleRows();
        string folder;
        int? index = null;
        BinderNode? before = null, after = null;
        int hit = rows.FindIndex(r => p.Y >= r.Bounds.Top && p.Y < r.Bounds.Bottom);
        if (hit < 0) folder = root; // sous la dernière ligne : à la fin du classeur
        else
        {
            var (item, node, bounds) = rows[hit];
            double y = p.Y - bounds.Top, h = Math.Max(1, bounds.Height);
            if (node.IsFolder && y > h * 0.25 && y < h * 0.75)
            {
                if (Binder.SamePath(node.Path, moving.Path)) return null;
                folder = node.Path;
            }
            else if (y < h / 2) { folder = Path.GetDirectoryName(node.Path)!; before = node; }
            else if (node.IsFolder && item.IsExpanded && node.Children.Count > 0) { folder = node.Path; index = 0; }
            else { folder = Path.GetDirectoryName(node.Path)!; after = node; }
        }
        // Un dossier ne va ni en lui-même ni dans ses sous-dossiers.
        if (moving.IsFolder && (Binder.SamePath(folder, moving.Path) || folder.StartsWith(moving.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return null;
        var others = Binder.Arranged(folder).Where(x => !Binder.SamePath(x, moving.Path)).ToList();
        if (before != null) index = others.FindIndex(x => Binder.SamePath(x, before.Path));
        if (after != null) index = others.FindIndex(x => Binder.SamePath(x, after.Path)) + 1;
        if (index < 0) return null; // survol de l'élément déplacé lui-même
        int documents = others.Count(x => !Directory.Exists(x));
        int position = Math.Clamp(index ?? others.Count, moving.IsFolder ? documents : 0, moving.IsFolder ? others.Count : documents);

        var folderRow = rows.FirstOrDefault(r => Binder.SamePath(r.Node.Path, folder));
        bool open = Binder.SamePath(folder, root) || folderRow.Item?.IsExpanded == true;
        if (!open) return (folder, position, folderRow.Bounds, null); // dossier fermé : il s'encadre
        double x = 6 + 22 + (Binder.SamePath(folder, root) ? 0 : (Depth(folderRow.Node) + 1) * 14);
        double lineY;
        if (position < others.Count)
        {
            var next = rows.FirstOrDefault(r => Binder.SamePath(r.Node.Path, others[position]));
            if (next.Item == null) return (folder, position, folderRow.Item == null ? null : folderRow.Bounds, null);
            lineY = next.Bounds.Top;
        }
        else if (others.Count > 0)
        {
            // Après le dernier élément, sous tout son contenu déplié.
            var last = others[^1];
            var below = rows.LastOrDefault(r => Binder.SamePath(r.Node.Path, last) || r.Node.Path.StartsWith(last + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (below.Item == null) return (folder, position, folderRow.Item == null ? null : folderRow.Bounds, null);
            lineY = below.Bounds.Bottom;
        }
        else lineY = folderRow.Item != null ? folderRow.Bounds.Bottom : 0;
        return (folder, position, null, new Point(x, Math.Max(1, lineY)));
    }

    void TreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        var p = e.GetPosition(Tree);
        AutoScroll(p);
        if (e.Data.GetData(typeof(BinderNode)) is BinderNode moving && DropTarget(p, moving) is { } t)
        {
            e.Effects = DragDropEffects.Move;
            ShowDropMarker(t.Box, t.Line);
        }
        else ClearDropMarker();
        e.Handled = true;
    }

    void TreeDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Tree);
        if (p.X < 0 || p.Y < 0 || p.X >= Tree.ActualWidth || p.Y >= Tree.ActualHeight) ClearDropMarker();
    }

    /// <summary>Près du haut ou du bas de l'arbre pendant un glisser : la liste défile.</summary>
    void AutoScroll(Point p)
    {
        if (FindChild<ScrollViewer>(Tree) is not { } scroll) return;
        if (p.Y < 24) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 8);
        else if (p.Y > Tree.ActualHeight - 24) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 8);
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    void TreeDrop(object sender, DragEventArgs e)
    {
        ClearDropMarker();
        e.Handled = true;
        if (e.Data.GetData(typeof(BinderNode)) is not BinderNode moving || DropTarget(e.GetPosition(Tree), moving) is not { } t || root == null) return;
        Run(() =>
        {
            Flush();
            var target = BinderOperations.Place(moving.Path, t.Folder, t.Index, root);
            if (!Binder.SamePath(target, moving.Path)) Relocated(moving.Path, target);
            else ReloadTree();
            // L'élément reste visible là où il est tombé (son dossier s'ouvre), sans rien ouvrir d'autre.
            if (ItemFor(target) == null && !Binder.SamePath(t.Folder, root)) RevealInTree(target);
            ItemFor(target)?.BringIntoView();
        });
    }

    /// <summary>Déplie les dossiers jusqu'à l'élément, sans changer la sélection.</summary>
    void RevealInTree(string path)
    {
        ItemsControl parent = Tree;
        while (parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is BinderNode n &&
                   path.StartsWith(n.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) is { } next)
        {
            next.IsExpanded = true;
            next.UpdateLayout();
            parent = next;
        }
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

    /// <summary>Polices libres embarquées dans l'app.</summary>
    static Stream? OpenFont(string file)
    {
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
