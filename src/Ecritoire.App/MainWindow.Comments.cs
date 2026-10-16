using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ecritoire.Core;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.Win32;

namespace Ecritoire.App;

/// <summary>
/// Commentaires (CommentsPanel.swift, CommentStore.swift) : passages surlignés dans le texte, volet de droite avec les
/// fils, et le flux de l'app macOS : on accumule des commentaires, on les envoie à Claude en un seul prompt (copié ou
/// enregistré dans .ecritoire/prompts), un instantané du texte est gardé, et les commentaires envoyés passent en
/// archive — on ne les revoit que sur demande. Le volet ne s'ouvre jamais tout seul quand il est vide.
/// </summary>
public partial class MainWindow
{
    readonly CommentSession comments = new();
    bool showArchive, commentsForced;
    Guid? selectedComment, replyingTo;
    (int Start, int Length)? draft;
    int? lastSentCount;
    TextBox? draftBox, replyBox;

    bool CommentsShown => settings.CommentsPanelVisible && current != null
        && (comments.OpenCount > 0 || draft != null || commentsForced);

    void LoadComments(string path, string text)
    {
        comments.Load(path, root, text);
        showArchive = false; commentsForced = false; draft = null; replyingTo = null; selectedComment = null; lastSentCount = null;
        RefreshComments();
    }

    /// <summary>Volet de droite : l'inspecteur s'il est demandé, sinon les commentaires s'il y a lieu.</summary>
    void UpdateRightPanel()
    {
        bool inspector = settings.InspectorVisible;
        bool commentsVisible = !inspector && CommentsShown;
        RightPanel.Visibility = inspector || commentsVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = inspector && current != null ? Visibility.Visible : Visibility.Collapsed;
        InspectorEmpty.Visibility = inspector && current == null ? Visibility.Visible : Visibility.Collapsed;
        CommentsPanel.Visibility = commentsVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorButton.IsChecked = inspector;
        CommentsButton.IsChecked = commentsVisible;
        CommentsButton.IsEnabled = current != null;
        CommentsButton.ToolTip = commentsVisible ? "Masquer les commentaires (Ctrl+Maj+C)" : "Afficher les commentaires (Ctrl+Maj+C)";
        int open = comments.OpenCount;
        CommentsBadge.Visibility = open > 0 && current != null ? Visibility.Visible : Visibility.Collapsed;
        CommentsBadgeText.Text = open.ToString();
    }

    void CommentsToggleClick(object sender, RoutedEventArgs e) => ToggleComments();

    void ToggleComments()
    {
        if (current == null) return;
        if (CommentsShown && !settings.InspectorVisible)
        {
            settings.CommentsPanelVisible = false;
            commentsForced = false;
        }
        else RevealComments();
        UpdateRightPanel();
    }

    void RevealComments()
    {
        settings.InspectorVisible = false;
        settings.CommentsPanelVisible = true;
        commentsForced = true;
        RefreshComments();
    }

    // MARK: Volet

    void RefreshComments()
    {
        UpdateRightPanel();
        Editor.TextArea.TextView.Redraw();
        int open = comments.OpenCount, archived = comments.ArchivedCount;
        ArchiveBack.Visibility = showArchive ? Visibility.Visible : Visibility.Collapsed;
        CommentsHeaderIcon.Text = showArchive ? "" : "";
        CommentsHeader.Text = showArchive ? "Archives" : "Commentaires";
        int count = showArchive ? archived : open;
        CommentsCountPill.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CommentsCount.Text = count.ToString();
        ClaudeCard.Visibility = showArchive ? Visibility.Collapsed : Visibility.Visible;
        SendButton.Content = open > 0 ? $"✦  Envoyer à Claude ({open})" : "✦  Envoyer à Claude";
        SendButton.IsEnabled = open > 0;
        CopyPromptItem.IsEnabled = SavePromptItem.IsEnabled = open > 0;
        SentNote.Visibility = lastSentCount != null ? Visibility.Visible : Visibility.Collapsed;
        if (lastSentCount is { } sent)
            SentText.Text = $"{sent} commentaire{S(sent)} envoyé{S(sent)} à Claude, rangé{S(sent)} dans les archives.";
        SendHint.Visibility = lastSentCount == null && open > 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingNote.Visibility = pendingChanges != null ? Visibility.Visible : Visibility.Collapsed;
        if (pendingChanges is { } n)
        {
            PendingText.Text = n > 0 ? $"{n} modification{S(n)} depuis l'instantané" : "Instantané pris — en attente de modifications";
            PendingLink.Visibility = n > 0 && review == null ? Visibility.Visible : Visibility.Collapsed;
        }
        if (showArchive)
        {
            CommentsFooter.Text = "Envoyés ou résolus";
            ArchiveLink.Text = $"En cours ({open})";
            ArchiveLink.Visibility = Visibility.Visible;
        }
        else
        {
            CommentsFooter.Text = open == 0 ? "Aucun commentaire en cours" : $"{open} en cours";
            ArchiveLink.Text = $"Archives ({archived})";
            ArchiveLink.Visibility = archived > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        CommentsList.Children.Clear();
        if (draft != null && !showArchive) CommentsList.Children.Add(DraftCard());
        var visible = comments.Visible(showArchive);
        foreach (var c in visible) CommentsList.Children.Add(Card(c));
        if (visible.Count == 0 && draft == null)
        {
            var empty = new StackPanel { Margin = new Thickness(20, 40, 20, 0) };
            empty.Children.Add(new TextBlock { Text = "", FontFamily = Glyphs, FontSize = 22, Foreground = Theme.Secondary, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock
            {
                Text = showArchive ? "Aucun commentaire archivé." : "Aucun commentaire en cours.\nSélectionnez un passage puis « Commenter » (Ctrl+Maj+M).",
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Theme.Secondary, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
            });
            CommentsList.Children.Add(empty);
        }
    }

    static string S(int n) => n > 1 ? "s" : "";
    FontFamily Glyphs => (FontFamily)FindResource("Glyphes");
    static readonly SolidColorBrush CardBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xE3, 0xDE, 0xEA)));
    static readonly SolidColorBrush SelectedCard = Freeze(new SolidColorBrush(Color.FromRgb(0xE9, 0xE2, 0xF7)));
    static readonly SolidColorBrush DeletedText = Freeze(new SolidColorBrush(Color.FromRgb(0xB3, 0x2E, 0x2E)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    static Border CardFrame(UIElement content, bool selected = false, bool accent = false) => new()
    {
        Child = content, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(8),
        Background = selected ? SelectedCard : Brushes.White, BorderBrush = accent ? Theme.Accent : CardBorder, BorderThickness = new Thickness(1),
    };

    static UIElement Pin(int number, bool archived) => new Border
    {
        Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = archived ? Theme.Secondary : Theme.Accent,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0),
        Child = new TextBlock { Text = number == 0 ? "+" : number.ToString(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    TextBlock Quote(string text) => new()
    {
        Text = text.Replace("\n", " "), FontFamily = Theme.Serif, FontStyle = FontStyles.Italic, FontSize = 13, Foreground = Theme.Secondary,
        TextWrapping = TextWrapping.Wrap, MaxHeight = 36, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    Button SmallIcon(string glyph, string tip, Action action)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Style = (Style)FindResource("BoutonIcone"), Width = 26, Height = 24, FontSize = 12 };
        b.Click += (_, e) => { e.Handled = true; action(); };
        return b;
    }

    UIElement DraftCard()
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(Pin(0, false));
        var (s, l) = draft!.Value;
        head.Children.Add(Quote(s + l <= Editor.Document.TextLength ? Editor.Document.GetText(s, l) : ""));
        panel.Children.Add(head);
        draftBox = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, MinHeight = 44, MaxHeight = 160, Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 8), BorderBrush = Theme.Accent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var commit = new Button { Content = "Commenter", Padding = new Thickness(10, 3, 10, 3), IsEnabled = false, Background = Theme.Accent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        draftBox.TextChanged += (_, _) => commit.IsEnabled = draftBox.Text.Trim().Length > 0;
        draftBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { CommitDraft(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelDraft(); e.Handled = true; }
        };
        panel.Children.Add(draftBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
        cancel.Click += (_, _) => CancelDraft();
        commit.Click += (_, _) => CommitDraft();
        buttons.Children.Add(cancel);
        buttons.Children.Add(commit);
        panel.Children.Add(buttons);
        var box = draftBox;
        Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        return CardFrame(panel, accent: true);
    }

    UIElement Card(DocumentComment c)
    {
        bool selected = selectedComment == c.Id;
        bool anchored = comments.Ranges.ContainsKey(c.Id);
        var panel = new StackPanel();
        var head = new DockPanel();
        head.Children.Add(Pin(comments.Number(c.Id), c.IsArchived));
        var info = new StackPanel();
        if (anchored) info.Children.Add(Quote(c.Quote));
        else info.Children.Add(new TextBlock { Text = $"Passage introuvable — « {c.Quote} »", FontStyle = FontStyles.Italic, FontSize = 11.5, Foreground = DeletedText, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        if (poemDisplay && comments.Ranges.TryGetValue(c.Id, out var r) && Poem.IsPoem(Editor.Text) && Poem.Reference(r.Start, r.Length, Editor.Text) is { } reference)
            meta.Children.Add(new TextBlock { Text = reference, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Accent, Margin = new Thickness(0, 0, 6, 0) });
        meta.Children.Add(new TextBlock { Text = ArchiveStatus(c) ?? Relative(c.Date), FontSize = 10.5, Foreground = Theme.Secondary });
        info.Children.Add(meta);
        head.Children.Add(info);
        panel.Children.Add(head);
        panel.Children.Add(new TextBox
        {
            Text = c.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Texte"), Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(-2, 0, 0, 0), Cursor = Cursors.Arrow,
        });
        foreach (var reply in c.Replies)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(new Border { Width = 2, Background = CardBorder, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = reply.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            panel.Children.Add(row);
        }
        if (replyingTo == c.Id)
        {
            replyBox = new TextBox { TextWrapping = TextWrapping.Wrap, Padding = new Thickness(5), Margin = new Thickness(0, 7, 0, 0), MaxHeight = 120 };
            var box = replyBox;
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { CommitReply(); e.Handled = true; }
                else if (e.Key == Key.Escape) { replyingTo = null; RefreshComments(); e.Handled = true; }
            };
            panel.Children.Add(box);
            Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        var tools = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var trash = SmallIcon("", "Supprimer", () => { comments.Delete(c.Id, Editor.Text); RefreshComments(); });
        DockPanel.SetDock(trash, Dock.Right);
        tools.Children.Add(trash);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (c.IsArchived)
            left.Children.Add(SmallIcon("", "Restaurer : redevient un commentaire en cours", () => { comments.Restore(c.Id, Editor.Text); RefreshComments(); }));
        else
        {
            left.Children.Add(SmallIcon("", "Répondre", () => { replyingTo = c.Id; selectedComment = c.Id; RefreshComments(); }));
            left.Children.Add(SmallIcon("", "Résolu : ranger dans les archives", () => { comments.ToggleResolved(c.Id, Editor.Text); RefreshComments(); }));
        }
        tools.Children.Add(left);
        panel.Children.Add(tools);
        var card = CardFrame(panel, selected);
        card.Opacity = c.IsArchived ? 0.85 : 1;
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not TextBox) SelectComment(c.Id, true); };
        if (selected) Dispatcher.BeginInvoke(() => card.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        return card;
    }

    static string? ArchiveStatus(DocumentComment c) =>
        c.Sent is { } sent ? "Envoyé à Claude " + Relative(sent) : c.Resolved ? "Résolu" : null;

    /// <summary>Date relative à la française : « à l'instant », « il y a 5 min », « hier », « le 9 oct. à 16:40 ».</summary>
    static string Relative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = DateTime.Now - local;
        if (delta.TotalMinutes < 1) return "à l'instant";
        if (delta.TotalHours < 1) return $"il y a {(int)delta.TotalMinutes} min";
        if (local.Date == DateTime.Today) return $"il y a {(int)delta.TotalHours} h";
        if (local.Date == DateTime.Today.AddDays(-1)) return "hier";
        return local.ToString("'le' d MMM 'à' HH:mm", new System.Globalization.CultureInfo("fr-FR"));
    }

    void SelectComment(Guid id, bool reveal)
    {
        selectedComment = id;
        if (reveal && comments.Ranges.TryGetValue(id, out var r) && r.Start + r.Length <= Editor.Document.TextLength && review == null)
        {
            Editor.Select(r.Start, r.Length);
            var loc = Editor.Document.GetLocation(r.Start);
            Editor.ScrollTo(loc.Line, loc.Column);
        }
        RefreshComments();
    }

    void ShowOpenCommentsClick(object sender, RoutedEventArgs e) { showArchive = false; RefreshComments(); }

    void ArchiveLinkClick(object sender, MouseButtonEventArgs e)
    {
        showArchive = !showArchive || sender is TextBlock { Text: "Voir" };
        commentsForced = true;
        RefreshComments();
    }

    // MARK: Écrire un commentaire

    void StartComment()
    {
        if (current == null || review != null) return;
        int start = Editor.SelectionStart, length = Editor.SelectionLength;
        if (length == 0)
        {
            // Sans sélection : le mot sous le curseur.
            var text = Editor.Text;
            int a = start, b = start;
            while (a > 0 && char.IsLetterOrDigit(text[a - 1])) a--;
            while (b < text.Length && char.IsLetterOrDigit(text[b])) b++;
            if (b == a) { SaveState.Text = "Sélectionnez un passage à commenter."; return; }
            start = a; length = b - a;
        }
        draft = (start, length);
        showArchive = false;
        settings.InspectorVisible = false;
        settings.CommentsPanelVisible = true;
        RefreshComments();
    }

    void CommitDraft()
    {
        if (draft is not { } d || draftBox == null || draftBox.Text.Trim().Length == 0) return;
        Flush();
        var text = Editor.Text;
        if (d.Start + d.Length > text.Length) { CancelDraft(); return; }
        var c = comments.Add(d.Start, d.Length, text, draftBox.Text);
        draft = null;
        selectedComment = c.Id;
        lastSentCount = null;
        RefreshComments();
        Editor.TextArea.Focus();
    }

    void CancelDraft()
    {
        draft = null;
        RefreshComments();
        Editor.TextArea.Focus();
    }

    void CommitReply()
    {
        if (replyingTo is not { } id || replyBox == null) return;
        comments.Reply(id, replyBox.Text, Editor.Text);
        replyingTo = null;
        RefreshComments();
    }

    // MARK: Envoyer à Claude

    string ClaudePrompt()
    {
        Flush();
        var body = Editor.Text;
        string? file = null;
        try { file = DocumentFile.ReadText(current!.Path); } catch (IOException) { }
        return comments.ClaudePrompt(current!.Title, current.Path, body, file, poemDisplay && Poem.IsPoem(body));
    }

    void CopyPromptClick(object sender, RoutedEventArgs e)
    {
        if (current == null || comments.OpenCount == 0) return;
        SendToClaude(prompt =>
        {
            Clipboard.SetText(prompt);
            SaveState.Text = "Prompt copié : collez-le dans Claude.";
        });
    }

    void SavePromptClick(object sender, RoutedEventArgs e)
    {
        if (current == null || root == null || comments.OpenCount == 0) return;
        var path = current.Path;
        SendToClaude(prompt =>
        {
            var file = CommentSession.PromptFile(path, root, DateTime.Now);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            DocumentFile.WriteAtomic(file, prompt);
            SaveState.Text = "Prompt enregistré dans .ecritoire\\prompts";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); } catch (Exception ex) { App.Log(ex); }
        });
    }

    /// <summary>Le prompt est livré (presse-papiers, fichier), un instantané est gardé pour voir ensuite ce que Claude
    /// aura modifié, et les commentaires envoyés passent en archive.</summary>
    void SendToClaude(Action<string> deliver)
    {
        var sent = comments.Active.Select(c => c.Id).ToList();
        var prompt = ClaudePrompt();
        try { deliver(prompt); }
        catch (Exception ex) { App.Log(ex); SaveState.Text = "Envoi impossible : " + ex.Message; return; }
        TakeSnapshot();
        commentsForced = true; // le volet reste ouvert sur la confirmation
        comments.ArchiveSent(sent, Editor.Text);
        lastSentCount = sent.Count;
        RefreshComments();
    }

    void ClaudeMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = ClaudeMenuButton.ContextMenu;
        menu.PlacementTarget = ClaudeMenuButton;
        menu.IsOpen = true;
    }

    void TakeSnapshotClick(object sender, RoutedEventArgs e) => TakeSnapshot();
    void PendingLinkClick(object sender, MouseButtonEventArgs e) => EnterReview();

    // MARK: Passages commentés dans le texte

    /// <summary>Surlignage lavande des passages commentés (plus soutenu pour le commentaire choisi).</summary>
    sealed class CommentMarks : DocumentColorizingTransformer
    {
        readonly MainWindow window;
        static readonly SolidColorBrush Open = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x7B, 0x5F, 0xBF)));
        static readonly SolidColorBrush Chosen = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0x7B, 0x5F, 0xBF)));
        public CommentMarks(MainWindow window) { this.window = window; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (window.review != null) return;
            int lineStart = line.Offset, lineEnd = line.EndOffset;
            foreach (var (id, r) in window.comments.Ranges)
            {
                if (window.comments.All.FirstOrDefault(c => c.Id == id) is not { IsArchived: false }) continue;
                int a = Math.Max(lineStart, r.Start), b = Math.Min(lineEnd, r.Start + r.Length);
                if (b <= a) continue;
                var brush = window.selectedComment == id ? Chosen : Open;
                ChangeLinePart(a, b, e => e.TextRunProperties.SetBackgroundBrush(brush));
            }
            if (window.draft is { } d)
            {
                int a = Math.Max(lineStart, d.Start), b = Math.Min(lineEnd, d.Start + d.Length);
                if (b > a) ChangeLinePart(a, b, e => e.TextRunProperties.SetBackgroundBrush(Chosen));
            }
        }
    }

    /// <summary>Le curseur entre dans un passage commenté : son commentaire est choisi dans le volet.</summary>
    void CaretMovedForComments()
    {
        if (review != null || current == null) return;
        var id = comments.At(Editor.TextArea.Caret.Offset);
        if (id != null && id != selectedComment && CommentsShown) { selectedComment = id; RefreshComments(); }
    }
}
