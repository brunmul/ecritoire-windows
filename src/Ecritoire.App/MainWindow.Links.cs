using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ecritoire.Core;

namespace Ecritoire.App;

/// <summary>
/// Panneau de droite (carte compacte de l'interface v2) : liens [[…]] du document et rétroliens (documents du classeur
/// qui pointent vers lui) ; barre latérale : documents récents du classeur.
/// </summary>
public partial class MainWindow
{
    static readonly Regex WikiLinkRE = new(@"(?<!!)\[\[([^\]\n|#]+)(?:#[^\]\n|]*)?(?:\|[^\]\n]*)?\]\]", RegexOptions.Compiled);
    const int ShortList = 5;
    bool linksAll, backlinksAll;
    string? linksFor;
    CancellationTokenSource? backlinksCancel;
    /// <summary>Cibles des liens de chaque fichier, relues seulement quand le fichier change.</summary>
    readonly Dictionary<string, (DateTime Written, HashSet<string> Targets)> linkCache = new(StringComparer.OrdinalIgnoreCase);

    static string LinkKey(string name)
    {
        var last = name.Replace('\\', '/').Split('/').Last().Trim();
        if (last.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) last = last[..^3];
        return last.Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    static List<string> LinkTargets(string text) =>
        WikiLinkRE.Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(t => t.Length > 0)
            .DistinctBy(LinkKey).ToList();

    BinderNode? ResolveLink(string target)
    {
        if (tree == null) return null;
        var key = LinkKey(target);
        return tree.AllDescendants.FirstOrDefault(n => !n.IsFolder && LinkKey(n.DisplayName) == key);
    }

    /// <summary>Remplit les listes si le panneau est visible et que le document a changé (ou sur demande).</summary>
    void RefreshLinks(bool force = false)
    {
        if (!settings.InspectorVisible || current == null) return;
        if (!force && linksFor != null && Binder.SamePath(linksFor, current.Path)) return;
        if (linksFor == null || !Binder.SamePath(linksFor, current.Path)) { linksAll = false; backlinksAll = false; }
        linksFor = current.Path;
        ShowOutgoingLinks();
        FindBacklinks();
    }

    void ShowOutgoingLinks()
    {
        LinksList.Children.Clear();
        var targets = LinkTargets(Editor.Text);
        if (targets.Count == 0) LinksList.Children.Add(EmptyRow("Aucun lien. Écrivez [[titre]] pour en créer un."));
        foreach (var t in linksAll ? targets : targets.Take(ShortList))
        {
            var node = ResolveLink(t);
            LinksList.Children.Add(ListRow("file", node?.DisplayName ?? t, node?.Path, node == null ? "Document introuvable dans le classeur" : null));
        }
        MoreLink(LinksMore, targets.Count, linksAll);
    }

    void FindBacklinks()
    {
        backlinksCancel?.Cancel();
        BacklinksList.Children.Clear();
        BacklinksMore.Visibility = Visibility.Collapsed;
        if (tree == null || current == null) return;
        BacklinksList.Children.Add(EmptyRow("Recherche…"));
        var cancel = backlinksCancel = new CancellationTokenSource();
        var self = current.Path;
        var keys = new HashSet<string> { LinkKey(Path.GetFileNameWithoutExtension(self)) };
        if (current.Title.Length > 0) keys.Add(LinkKey(current.Title));
        var files = tree.AllDescendants.Where(n => !n.IsFolder && !Binder.SamePath(n.Path, self)).Select(n => n.Path).ToList();
        Task.Run(() =>
        {
            var hits = new List<string>();
            foreach (var file in files)
            {
                if (cancel.IsCancellationRequested) return hits;
                try
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    HashSet<string> targets;
                    lock (linkCache)
                        if (linkCache.TryGetValue(file, out var c) && c.Written == written) targets = c.Targets;
                        else targets = null!;
                    if (targets == null)
                    {
                        targets = LinkTargets(File.ReadAllText(file)).Select(LinkKey).ToHashSet();
                        lock (linkCache) linkCache[file] = (written, targets);
                    }
                    if (targets.Overlaps(keys)) hits.Add(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return hits;
        }, cancel.Token).ContinueWith(t =>
        {
            if (t.IsCanceled || t.IsFaulted || cancel.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (cancel.IsCancellationRequested) return;
                BacklinksList.Children.Clear();
                var hits = t.Result;
                if (hits.Count == 0) BacklinksList.Children.Add(EmptyRow("Aucun document ne renvoie ici."));
                foreach (var file in backlinksAll ? hits : hits.Take(ShortList))
                    BacklinksList.Children.Add(ListRow("file", Path.GetFileNameWithoutExtension(file), file, RelativeFolder(file)));
                MoreLink(BacklinksMore, hits.Count, backlinksAll);
            });
        }, TaskScheduler.Default);
    }

    string? RelativeFolder(string file)
    {
        if (root == null) return null;
        var folder = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
        return folder == "." ? null : folder.Replace("\\", " › ");
    }

    static void MoreLink(TextBlock more, int count, bool all)
    {
        more.Visibility = count > ShortList ? Visibility.Visible : Visibility.Collapsed;
        more.Text = all ? "Afficher moins" : $"Tout afficher ({count})";
    }

    void LinksMoreClick(object sender, MouseButtonEventArgs e) { linksAll = !linksAll; ShowOutgoingLinks(); }
    void BacklinksMoreClick(object sender, MouseButtonEventArgs e) { backlinksAll = !backlinksAll; FindBacklinks(); }

    TextBlock EmptyRow(string text) => new()
    {
        Text = text, Foreground = Theme.Secondary, FontSize = 12.5, Margin = new Thickness(8, 2, 8, 4), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Ligne de liste : petite icône, titre sur une ligne ; un clic ouvre le document.</summary>
    Button ListRow(string? icon, string title, string? path, string? tip, double fontSize = 13.5)
    {
        var content = new DockPanel();
        if (icon != null)
        {
            var image = Icons.Make(icon, 15, path == null ? Theme.Secondary : IconBrush);
            image.Margin = new Thickness(0, 0, 10, 0);
            content.Children.Add(image);
        }
        content.Children.Add(new TextBlock
        {
            Text = title, FontSize = fontSize, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Foreground = path == null ? Theme.Secondary : (Brush)FindResource("Texte"), FontStyle = path == null ? FontStyles.Italic : FontStyles.Normal,
        });
        var row = new Button { Content = content, Style = (Style)FindResource("LigneListe"), ToolTip = tip, Padding = new Thickness(8, 0, 8, 0), MinHeight = 30 };
        if (path == null) row.Cursor = Cursors.Arrow;
        else row.Click += (_, _) => Run(() => { if (File.Exists(path)) { OpenDocument(path); SelectInTree(path); } });
        return row;
    }

    /// <summary>« + » des liens : insère [[]] au curseur, le curseur entre les crochets.</summary>
    void AddLinkClick(object sender, RoutedEventArgs e)
    {
        if (current == null || review != null) return;
        int at = Editor.TextArea.Caret.Offset;
        var selected = Editor.SelectionLength > 0 ? Editor.SelectedText : "";
        if (selected.Length > 0) at = Editor.SelectionStart;
        Editor.Document.Replace(at, selected.Length, "[[" + selected + "]]");
        Editor.TextArea.Caret.Offset = at + 2 + selected.Length;
        Editor.TextArea.Focus();
    }
}
