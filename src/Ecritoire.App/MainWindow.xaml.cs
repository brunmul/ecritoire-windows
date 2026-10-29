using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ecritoire.Core;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;

namespace Ecritoire.App;

/// <summary>
/// Fenêtre principale : classeur (arbre, ordre et icônes), éditeur mis en page, recherche, affichage poème,
/// enregistrement automatique 0,8 s après la dernière frappe (et en changeant de document ou en quittant).
/// </summary>
public partial class MainWindow : Window
{
    readonly Settings settings = Settings.Load();
    readonly MarkdownLook look;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(0.8) };
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer treeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    string? root;
    BinderNode? tree;
    DocumentFile? current;
    SearchIndex? index;
    FileSystemWatcher? watcher;
    CancellationTokenSource? searchCancel;
    bool dirty, loading, poemDisplay, selectingInTree, settingsLocked;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => TintTitleBar();
        look = new MarkdownLook(Editor);
        var o = Editor.Options;
        o.EnableHyperlinks = false;
        o.EnableEmailHyperlinks = false;
        o.HighlightCurrentLine = false;
        o.AllowScrollBelowDocument = true;
        o.EnableRectangularSelection = false;
        o.InheritWordWrapIndentation = false;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xD9, 0x40));
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionCornerRadius = 2;
        Editor.TextArea.Caret.CaretBrush = Theme.Accent;
        Editor.TextChanged += (_, _) => TextEdited();
        Editor.TextArea.TextView.LineTransformers.Add(new CommentMarks(this));
        Editor.TextArea.TextView.LineTransformers.Add(new DiffMarks(this));
        Editor.TextArea.Caret.PositionChanged += (_, _) => { CaretMovedForComments(); CaretMovedForReview(); };
        var commentItem = new MenuItem { Header = "Commenter", InputGestureText = "Ctrl+Maj+M" };
        commentItem.Click += (_, _) => StartComment();
        Editor.ContextMenu = new ContextMenu { Items = {
            commentItem, new Separator(),
            new MenuItem { Header = "Couper", Command = ApplicationCommands.Cut },
            new MenuItem { Header = "Copier", Command = ApplicationCommands.Copy },
            new MenuItem { Header = "Coller", Command = ApplicationCommands.Paste } } };
        Editor.ContextMenuOpening += (_, _) => commentItem.IsEnabled = current != null && review == null;
        saveTimer.Tick += (_, _) => Save();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RunSearch(); };
        treeTimer.Tick += (_, _) => { treeTimer.Stop(); ReloadTree(); };
        renameTimer.Tick += (_, _) => { renameTimer.Stop(); if (pendingRename is { } path) BeginRename(path); };
        WelcomeTitle.FontFamily = Theme.Display;
        // Police d'interface : Hanken Grotesk.
        FontFamily = Theme.Grotesk;
        EditorArea.SizeChanged += (_, _) => ApplyLayout();
        RightPanel.IsVisibleChanged += (_, _) => ApplyLayout();
        InitOutline();
        InitAi();
        PreviewKeyDown += Shortcuts;
        Loaded += (_, _) =>
        {
            SidebarColumn.Width = new GridLength(settings.SidebarWidth);
            ApplyLayout();
            UpdateRightPanel();
            if (ProbeBinder != null) { OpenBinder(ProbeBinder); RunProbe(); }
            else if (settings.LastBinder is { } last && Directory.Exists(last)) OpenBinder(last);
            if (ProbeBinder == null) CheckForUpdate();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Flush();
        if (!SidebarCollapsed) settings.SidebarWidth = SidebarColumn.ActualWidth;
        if (!settingsLocked) settings.Save();
        base.OnClosing(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        Flush(); // en changeant d'application, comme sur macOS
        base.OnDeactivated(e);
    }

    // MARK: Classeur

    void OpenBinderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choisir un classeur", Multiselect = false };
        if (root != null) dialog.InitialDirectory = Path.GetDirectoryName(root);
        if (dialog.ShowDialog(this) == true) OpenBinder(dialog.FolderName);
    }

    void OpenBinder(string path)
    {
        Flush();
        root = Path.GetFullPath(path).TrimEnd('\\', '/');
        settings.LastBinder = root;
        BinderName.Text = Path.GetFileName(root);
        Welcome.Visibility = Visibility.Collapsed;
        ReloadTree();
        // Index de recherche construit en arrière-plan.
        index = new SearchIndex(root);
        var building = index;
        Task.Run(() => { try { building.Build(); } catch (Exception ex) { App.Log(ex); } })
            .ContinueWith(_ => Dispatcher.BeginInvoke(() => { if (SearchPanel.Visibility == Visibility.Visible) RunSearch(); }));
        WatchBinder();
        var last = settings.LastDocument.GetValueOrDefault(root);
        var first = last != null && File.Exists(last) ? last : tree?.AllDescendants.FirstOrDefault(n => !n.IsFolder)?.Path;
        if (first != null) { OpenDocument(first); SelectInTree(first); }
        else CloseDocument();
    }

    /// <param name="from">Élément qui vient de changer de chemin (renommé, déplacé) : ses dossiers ouverts le restent.</param>
    void ReloadTree(string? from = null, string? to = null)
    {
        if (root == null) return;
        if (renameBox != null) { treeTimer.Stop(); treeTimer.Start(); return; } // pas pendant un renommage
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
                if (item.IsExpanded && item.Tag is BinderNode n)
                {
                    var path = n.Path;
                    if (from != null && to != null && (Binder.SamePath(path, from) || path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                        path = to + path[from.Length..];
                    expanded.Add(path);
                    Collect(item);
                }
        }
        Collect(Tree);
        tree = Binder.LoadTree(root, arranged: true);
        Tree.Items.Clear();
        foreach (var child in tree.Children) Tree.Items.Add(MakeItem(child, expanded));
        if (current != null) SelectInTree(current.Path);
    }

    TreeViewItem MakeItem(BinderNode node, HashSet<string>? expanded = null)
    {
        var (icon, color) = BinderIcons.For(node.Icon, node.IsFolder);
        var header = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        // Numéro : la position dans le dossier (documents d'abord), qui suit chaque réorganisation.
        int number = (node.Parent?.Children.IndexOf(node) ?? 0) + 1;
        header.Children.Add(new TextBlock
        {
            Text = number.ToString(), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, MinWidth = 15,
            TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0), Foreground = (Brush)FindResource("TexteSecondaire"),
        });
        if (icon != null)
        {
            var image = Icons.Make(icon, 17, color);
            image.Margin = new Thickness(0, 0, 9, 0);
            header.Children.Add(image);
        }
        // Tag « nom » : un clic dessus, sur l'élément déjà sélectionné, lance le renommage.
        header.Children.Add(new TextBlock { Text = node.DisplayName, Tag = "nom", VerticalAlignment = VerticalAlignment.Center, FontSize = 14.5, TextTrimming = TextTrimming.CharacterEllipsis });
        int depth = Depth(node);
        var item = new TreeViewItem
        {
            Header = header, Tag = node, Style = (Style)FindResource("LigneClasseur"),
            // Retrait par niveau (14 px, comme l'app macOS) ; la pastille de sélection garde toute la largeur.
            Padding = new Thickness(depth * 14, 0, 6, 0),
            ToolTip = node.DisplayName.Length > 34 ? node.DisplayName : null,
        };
        if (node.IsFolder && node.Children.Count > 0)
        {
            item.Items.Add(new TreeViewItem { Header = "…" }); // rempli à l'ouverture
            item.Expanded += (_, e) =>
            {
                if (e.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
                item.Items.Clear();
                foreach (var c in node.Children) item.Items.Add(MakeItem(c, expanded));
            };
            if (expanded?.Contains(node.Path) == true) item.IsExpanded = true;
        }
        return item;
    }

    void TreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (selectingInTree || e.NewValue is not TreeViewItem { Tag: BinderNode node } || node.IsFolder) return;
        if (current == null || !Binder.SamePath(current.Path, node.Path)) OpenDocument(node.Path);
    }

    /// <summary>Déplie l'arbre jusqu'au document et le sélectionne (sans le rouvrir).</summary>
    void SelectInTree(string path)
    {
        selectingInTree = true;
        try
        {
            ItemsControl parent = Tree;
            while (true)
            {
                var next = parent.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Tag is BinderNode n &&
                    (Binder.SamePath(n.Path, path) || path.StartsWith(n.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
                if (next == null) return;
                if (next.Tag is BinderNode n && Binder.SamePath(n.Path, path))
                {
                    next.IsSelected = true;
                    next.BringIntoView();
                    return;
                }
                next.IsExpanded = true;
                next.UpdateLayout();
                parent = next;
            }
        }
        finally { selectingInTree = false; }
    }

    // MARK: Document

    void OpenDocument(string path)
    {
        Flush();
        try
        {
            current = DocumentFile.Load(path);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SaveState.Text = "Lecture impossible : " + ex.Message;
            return;
        }
        review = null;
        Editor.IsReadOnly = false;
        poemDisplay = Sidecar.PoemDisplay(path, root, current.Body);
        look.DocumentFolder = Path.GetDirectoryName(path);
        ApplyLayout();
        SetEditorDocument(current.Body.Replace("\r\n", "\n"));
        Editor.Visibility = Visibility.Visible;
        Editor.ScrollToHome();
        Editor.TextArea.Caret.Offset = 0;
        dirty = false;
        if (root != null) settings.LastDocument[root] = path;
        DocumentTitle.Text = current.DisplayTitle;
        DocumentPath.Text = root != null ? Path.GetFileName(root) + " › " + Path.GetRelativePath(root, Path.GetDirectoryName(path)!).Replace("\\", " › ").TrimEnd('.', ' ', '›') : "";
        Title = current.DisplayTitle + " — Écritoire";
        SaveState.Text = "✓ Enregistré";
        UpdateWordCount();
        LoadComments(path, Editor.Text);
        pendingChanges = null;
        UpdateChangesUI();
        RefreshPendingChanges();
        UpdateInspector();
        RememberRecent(path);
        RebuildOutline();
        RefreshLinks(force: true);
        UpdateAiBar();
        Editor.TextArea.Focus();
    }

    /// <summary>Nouveau texte dans l'éditeur ; les plages des commentaires suivent ensuite chaque frappe.</summary>
    void SetEditorDocument(string text)
    {
        loading = true;
        var doc = new TextDocument(text);
        doc.Changed += (_, e) => { if (review == null) comments.TextEdited(e.Offset, e.RemovalLength, e.InsertionLength); };
        Editor.Document = doc;
        loading = false;
    }

    void TextEdited(bool inspector = false)
    {
        if ((loading && !inspector) || current == null || review != null) return;
        dirty = true;
        SaveState.Text = "● Modifié";
        saveTimer.Stop();
        saveTimer.Start();
    }

    /// <summary>Enregistre tout de suite s'il y a des modifications en attente.</summary>
    void Flush()
    {
        saveTimer.Stop();
        if (dirty) Save();
    }

    void Save()
    {
        saveTimer.Stop();
        if (current == null || !dirty || review != null) return;
        var text = Editor.Text;
        // Fins de ligne de l'app macOS (LF), et versets renumérotés d'après leur position.
        var wanted = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (poemDisplay && Poem.IsPoem(wanted)) wanted = Poem.Renumbered(wanted);
        if (wanted != text) ReplaceText(wanted);
        current.Body = wanted;
        try
        {
            if (current.Save()) index?.Update(current.Path);
            dirty = false;
            SaveState.Text = "✓ Enregistré";
            if (comments.All.Count > 0) comments.Persist(wanted);
            if (pendingChanges != null) RefreshPendingChanges();
            if (settings.InspectorVisible) ShowOutgoingLinks();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SaveState.Text = "Enregistrement impossible : " + ex.Message;
        }
        UpdateWordCount();
    }

    /// <summary>Remplace le texte de l'éditeur en ne touchant que la partie qui change (curseur et annulation conservés).</summary>
    void ReplaceText(string wanted)
    {
        var doc = Editor.Document;
        var old = doc.Text;
        int prefix = 0, max = Math.Min(old.Length, wanted.Length);
        while (prefix < max && old[prefix] == wanted[prefix]) prefix++;
        int suffix = 0;
        while (suffix < max - prefix && old[old.Length - 1 - suffix] == wanted[wanted.Length - 1 - suffix]) suffix++;
        loading = true;
        doc.Replace(prefix, old.Length - prefix - suffix, wanted.Substring(prefix, wanted.Length - prefix - suffix));
        loading = false;
    }

    void UpdateWordCount()
    {
        if (current == null) { WordCount.Text = ""; return; }
        var body = Editor.Text;
        if (poemDisplay && Poem.IsPoem(body))
        {
            var verses = Poem.Verses(body);
            WordCount.Text = $"{verses.Count} versets · {WordCounter.Count(body) - verses.Count(v => v.Marker != null)} mots";
        }
        else WordCount.Text = $"{WordCounter.Count(body):N0} mots".Replace(",", " ").Replace("\u00A0", " ").Replace("\u202F", " ");
        FileWordsText.Text = WordCount.Text;
    }

    // MARK: Affichage et zoom

    void ProseClick(object sender, RoutedEventArgs e) => SetPoemDisplay(false);
    void PoemClick(object sender, RoutedEventArgs e) => SetPoemDisplay(true);

    void SetPoemDisplay(bool on)
    {
        if (current == null) { ApplyLayout(); return; }
        poemDisplay = on;
        ApplyLayout();
        UpdateWordCount();
    }

    void ApplyLayout()
    {
        double zoom = settings.Zoom;
        double fontSize = Math.Round(Theme.BaseFontSize * zoom);
        double column = Math.Round((poemDisplay ? Theme.BasePoemColumn : Theme.BaseColumn) * zoom);
        double gutter = poemDisplay ? Math.Round(fontSize * 3.0) : Math.Round(fontSize * 1.4);
        look.Configure(poemDisplay, fontSize);
        // L'éditeur occupe toute la zone (barre de défilement au bord de la fenêtre) ; la colonne est centrée dans la
        // place laissée par le panneau de droite flottant.
        double width = EditorArea.ActualWidth > 0 ? EditorArea.ActualWidth : 900;
        double panel = RightPanel.Visibility == Visibility.Visible ? RightPanel.Width + 24 : 0;
        const double scrollbar = 10; // barre fine (App.xaml)
        double available = Math.Max(column + 48, width - panel - scrollbar);
        double left = Math.Max(24, Math.Round((available - column) / 2));
        double right = Math.Max(24, width - scrollbar - left - column);
        Editor.Padding = new Thickness(Math.Max(0, left - gutter), Math.Round(28 * zoom), right, 0);
        // Réglette à gauche de la colonne (et des numéros de versets) ; barre IA centrée sous la colonne.
        OutlineRuler.Margin = new Thickness(Math.Max(4, left - gutter - 58), 0, 0, 0);
        double bar = Math.Min(column + 40, available - 24);
        AiBar.Width = bar;
        AiBar.Margin = new Thickness(Math.Max(12, left + column / 2 - bar / 2), 0, 0, 18);
        ZoomLabel.Text = Math.Abs(zoom - 1) < 0.001 ? "" : $"{Math.Round(zoom * 100)} %";
        ProseButton.IsChecked = !poemDisplay;
        PoemButton.IsChecked = poemDisplay;
        Editor.TextArea.TextView.Redraw();
    }

    void SetZoom(double zoom)
    {
        settings.Zoom = Math.Clamp(Math.Round(zoom * 10) / 10, 0.5, 2.0);
        ApplyLayout();
    }

    void ZoomInClick(object sender, RoutedEventArgs e) => SetZoom(settings.Zoom + 0.1);
    void ZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(settings.Zoom - 0.1);
    void ZoomResetClick(object sender, MouseButtonEventArgs e) => SetZoom(1);

    void Shortcuts(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (review != null)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (e.Key == Key.Escape) { ExitReview(); e.Handled = true; return; }
            if (alt && !ctrl && key == Key.Up) { SelectChange(-1); e.Handled = true; return; }
            if (alt && !ctrl && key == Key.Down) { SelectChange(+1); e.Handled = true; return; }
        }
        if (!ctrl || alt) return; // Ctrl+Alt = AltGr : laisse taper @ # { [ €
        switch (e.Key)
        {
            case Key.M when shift: StartComment(); e.Handled = true; break;
            case Key.Oem5 or Key.OemBackslash: CollapseSidebarClick(this, e); e.Handled = true; break;
            case Key.C when shift: ToggleComments(); e.Handled = true; break;
            case Key.I when !shift: InspectorToggleClick(this, e); e.Handled = true; break;
            case Key.E when !shift: ExportClick(this, e); e.Handled = true; break;
            case Key.N: Run(() => { if (shift) NewFolder(null); else NewDocument(null); }); e.Handled = true; break;
            case Key.O: OpenBinderClick(this, e); e.Handled = true; break;
            case Key.S: Flush(); e.Handled = true; break;
            case Key.F when shift: ShowSearch(); e.Handled = true; break;
            case Key.OemPlus or Key.Add: SetZoom(settings.Zoom + 0.1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract: SetZoom(settings.Zoom - 0.1); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: SetZoom(1); e.Handled = true; break;
        }
    }

    // MARK: Changements sur le disque (autre application, synchronisation, IA…)

    void WatchBinder()
    {
        watcher?.Dispose();
        if (root == null) return;
        try
        {
            watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
            // Contenu modifié : le document ouvert est relu, l'index mis à jour. Structure (création, suppression,
            // renommage, .order, .icones) : l'arbre est relu.
            watcher.Changed += (_, e) => { if (!Directory.Exists(e.FullPath)) Dispatcher.BeginInvoke(() => DiskChanged(e.FullPath, false)); };
            watcher.Created += (_, e) => Dispatcher.BeginInvoke(() => DiskChanged(e.FullPath, true));
            watcher.Deleted += (_, e) => Dispatcher.BeginInvoke(() => DiskChanged(e.FullPath, true));
            watcher.Renamed += (_, e) => Dispatcher.BeginInvoke(() => { DiskChanged(e.OldFullPath, true); DiskChanged(e.FullPath, true); });
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { App.Log(ex); }
    }

    void DiskChanged(string path, bool structural)
    {
        if (root == null || path.Contains(Path.DirectorySeparatorChar + Sidecar.FolderName)) return;
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') && name.EndsWith(".tmp")) return; // nos propres écritures atomiques
        if (string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase)) index?.Update(path);
        if (current != null && Binder.SamePath(path, current.Path))
        {
            if (!dirty && File.Exists(path) && current.ChangedOnDisk())
            {
                // Modifié par un autre programme (Claude, synchronisation…) : la version d'avant devient l'instantané.
                KeepSnapshotOfPreviousVersion();
                current.Reload();
                if (review != null) EnterReview(review.Current);
                else
                {
                    int caret = Editor.TextArea.Caret.Offset;
                    loading = true;
                    ReplaceText(current.Body.Replace("\r\n", "\n"));
                    loading = false;
                    Editor.TextArea.Caret.Offset = Math.Min(caret, Editor.Document.TextLength);
                    comments.Reanchor(Editor.Text);
                    RefreshComments();
                    RefreshPendingChanges();
                }
                DocumentTitle.Text = current.DisplayTitle;
                UpdateWordCount();
                UpdateInspector();
            }
            if (!structural) return;
        }
        if (structural || name == Binder.OrderFileName || name == Binder.IconsFileName)
        {
            treeTimer.Stop();
            treeTimer.Start();
        }
    }

    // MARK: Recherche

    sealed class SearchRow
    {
        public string? Path { get; init; }
        public string Title { get; init; } = "";
        public string Folder { get; init; } = "";
        public string Label { get; init; } = "";
        public string Before { get; init; } = "";
        public string Match { get; init; } = "";
        public string After { get; init; } = "";
        public int Start { get; init; } = -1;
        public int Length { get; init; }
        public bool IsDocument { get; init; }
        public Visibility TitleVisibility => IsDocument ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HitVisibility => IsDocument ? Visibility.Collapsed : Visibility.Visible;
        public Visibility LabelVisibility => !IsDocument && Label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SearchClick(object sender, RoutedEventArgs e)
    {
        if (SearchPanel.Visibility == Visibility.Visible) HideSearch(); else ShowSearch();
    }

    void ShowSearch()
    {
        if (SidebarCollapsed) CollapseSidebarClick(this, new RoutedEventArgs());
        SearchPanel.Visibility = Visibility.Visible;
        UpdateRail();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    void HideSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        UpdateRail();
        Editor.TextArea.Focus();
    }

    void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { if (SearchBox.Text.Length > 0) SearchBox.Clear(); else HideSearch(); e.Handled = true; }
        else if (e.Key == Key.Down && SearchResults.Items.Count > 0) { SearchResults.SelectedIndex = Math.Min(SearchResults.Items.Count - 1, Math.Max(0, SearchResults.SelectedIndex + 1)); e.Handled = true; }
    }

    void SearchTextChanged(object sender, TextChangedEventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    void RunSearch()
    {
        var query = new SearchQuery(SearchBox.Text);
        searchCancel?.Cancel();
        if (query.IsEmpty || index == null)
        {
            SearchResults.ItemsSource = null;
            SearchStatus.Text = "Titres et texte, sans tenir compte des accents";
            return;
        }
        if (!index.IsReady) { SearchStatus.Text = "Indexation…"; return; }
        var cancel = searchCancel = new CancellationTokenSource();
        var searched = index;
        Task.Run(() => searched.Search(query, cancel: cancel.Token), cancel.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || t.IsFaulted || cancel.IsCancellationRequested) return;
            var r = t.Result;
            Dispatcher.BeginInvoke(() =>
            {
                if (cancel.IsCancellationRequested) return;
                var rows = new List<SearchRow>();
                foreach (var d in r.Documents)
                {
                    var folder = Path.GetDirectoryName(d.RelativePath.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar.ToString(), " › ");
                    rows.Add(new SearchRow { Path = d.Path, Title = d.Title, Folder = string.IsNullOrEmpty(folder) ? "—" : folder, IsDocument = true });
                    foreach (var h in d.Hits)
                        rows.Add(new SearchRow { Path = d.Path, Label = h.Verse ?? h.Heading ?? "", Before = h.Before, Match = h.Match, After = h.After, Start = h.Start, Length = h.Length });
                }
                SearchResults.ItemsSource = rows;
                SearchStatus.Text = r.TotalHits == 0 && r.Documents.Count == 0 ? "Aucun résultat"
                    : $"{(r.TotalHits >= SearchIndex.CountLimit ? "2 000+" : r.TotalHits.ToString("N0").Replace(",", " "))} occurrence{(r.TotalHits > 1 ? "s" : "")} · {r.Documents.Count} document{(r.Documents.Count > 1 ? "s" : "")} · {r.Milliseconds:0} ms";
            });
        }, TaskScheduler.Default);
    }

    void SearchResultChosen(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchRow { Path: { } path } row) return;
        if (current == null || !Binder.SamePath(current.Path, path)) { OpenDocument(path); SelectInTree(path); }
        if (row.Start >= 0 && row.Start + row.Length <= Editor.Document.TextLength)
        {
            Editor.Select(row.Start, row.Length);
            var loc = Editor.Document.GetLocation(row.Start);
            Editor.ScrollTo(loc.Line, loc.Column);
        }
    }

    // MARK: Rail et barre latérale

    bool SidebarCollapsed => SidebarColumn.Width.Value == 0;
    GridLength sidebarWidth = new(280);

    void CollapseSidebarClick(object sender, RoutedEventArgs e)
    {
        if (SidebarCollapsed)
        {
            SidebarColumn.MinWidth = 200;
            SidebarColumn.Width = sidebarWidth;
            ExpandSidebarButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            sidebarWidth = SidebarColumn.Width;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            ExpandSidebarButton.Visibility = Visibility.Visible;
        }
        UpdateRail();
    }

    void RailBinderClick(object sender, RoutedEventArgs e)
    {
        if (SidebarCollapsed) CollapseSidebarClick(sender, e);
        else if (SearchPanel.Visibility != Visibility.Visible) CollapseSidebarClick(sender, e);
        if (SearchPanel.Visibility == Visibility.Visible) HideSearch();
        UpdateRail();
    }

    void RailChangesClick(object sender, RoutedEventArgs e)
    {
        if (review != null) ExitReview();
        else if (pendingChanges is > 0) EnterReview();
        else SaveState.Text = pendingChanges == 0 ? "Instantané pris — aucune modification depuis." : "Pas d'instantané : « Envoyer à Claude » ou le menu … des commentaires en crée un.";
        UpdateRail();
    }

    void UpdateRail()
    {
        RailBinder.IsChecked = !SidebarCollapsed && SearchPanel.Visibility != Visibility.Visible;
        RailSearch.IsChecked = !SidebarCollapsed && SearchPanel.Visibility == Visibility.Visible;
        RailComments.IsChecked = CommentsButton.IsChecked;
        RailChanges.IsChecked = review != null;
    }

    void NewDocumentClick(object sender, RoutedEventArgs e) => Run(() => NewDocument(null));

    void MoreClick(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void ZoomResetMenuClick(object sender, RoutedEventArgs e) => SetZoom(1);
}
