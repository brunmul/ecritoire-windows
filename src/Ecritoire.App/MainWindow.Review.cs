using System.Windows;
using System.Windows.Media;
using Ecritoire.Core;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Ecritoire.App;

/// <summary>
/// Relecture des modifications (Review.swift) : un instantané garde la version de référence d'un document (avant de
/// l'envoyer à Claude, ou quand un autre programme le modifie) ; le mode « Modifications » montre une vue fusionnée,
/// le texte supprimé barré en rouge à sa place, le texte ajouté en vert, et chaque modification s'accepte ou se refuse.
/// </summary>
public partial class MainWindow
{
    /// <summary>Vue fusionnée « avant / après » et position de chaque modification dans cette vue.</summary>
    sealed class ReviewState
    {
        public sealed record Change(DiffHunk Hunk, int DeletedStart, int DeletedLength, int InsertedStart, int InsertedLength)
        {
            public int Start => DeletedStart;
            public int End => InsertedStart + InsertedLength;
        }

        public string Base { get; }
        public string Merged { get; }
        public List<Change> Changes { get; } = new();
        public int Current { get; set; }

        public ReviewState(string @base, string current, List<DiffHunk> hunks)
        {
            Base = @base;
            var merged = new System.Text.StringBuilder();
            int cursor = 0;
            foreach (var h in hunks)
            {
                merged.Append(current, cursor, h.CurrentStart - cursor);
                int del = merged.Length;
                merged.Append(h.Deleted);
                int ins = merged.Length;
                merged.Append(h.Inserted);
                Changes.Add(new Change(h, del, h.Deleted.Length, ins, h.Inserted.Length));
                cursor = h.CurrentStart + h.CurrentLength;
            }
            merged.Append(current, cursor, current.Length - cursor);
            Merged = merged.ToString();
        }
    }

    ReviewState? review;
    int? pendingChanges;
    int diffGeneration;

    // MARK: Instantané

    string? SnapshotBody()
    {
        if (current == null || root == null) return null;
        var text = Sidecar.LoadSnapshot(current.Path, root);
        return text == null ? null : MarkdownDocument.Parse(text).Body;
    }

    /// <summary>Garde la version actuelle comme référence (avant de la confier à Claude, par exemple).</summary>
    void TakeSnapshot()
    {
        if (current == null || root == null) return;
        Flush();
        try
        {
            var text = current.LastWritten.Length > 0 ? current.LastWritten : DocumentFile.ReadText(current.Path);
            Sidecar.SaveSnapshot(text, current.Path, root);
            pendingChanges = 0;
        }
        catch (Exception ex) { App.Log(ex); }
        UpdateChangesUI();
    }

    /// <summary>Un autre programme vient de modifier le fichier : sans instantané, on garde la version d'avant.</summary>
    void KeepSnapshotOfPreviousVersion()
    {
        if (current == null || root == null || current.LastWritten.Length == 0 || Sidecar.LoadSnapshot(current.Path, root) != null) return;
        try { Sidecar.SaveSnapshot(current.LastWritten, current.Path, root); } catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Oublie l'instantané : la version actuelle devient la référence.</summary>
    void DiscardSnapshot()
    {
        if (current != null && root != null)
            try { Sidecar.SaveSnapshot(null, current.Path, root); } catch (Exception ex) { App.Log(ex); }
        pendingChanges = null;
        if (review != null) ExitReview();
        UpdateChangesUI();
    }

    /// <summary>Compte les modifications depuis l'instantané, en arrière-plan.</summary>
    void RefreshPendingChanges()
    {
        var @base = SnapshotBody();
        int generation = ++diffGeneration;
        if (@base == null) { pendingChanges = null; UpdateChangesUI(); return; }
        var text = review != null ? current!.Body : Editor.Text;
        Task.Run(() => TextDiff.Hunks(@base, text).Count).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (generation != diffGeneration) return;
                pendingChanges = t.Result;
                UpdateChangesUI();
            });
        }, TaskScheduler.Default);
    }

    void UpdateChangesUI()
    {
        bool banner = pendingChanges is > 0 && review == null && current != null;
        ChangesBanner.Visibility = banner ? Visibility.Visible : Visibility.Collapsed;
        if (pendingChanges is { } n)
            ChangesText.Text = $"Ce document a été modifié : {n} modification{S(n)} depuis l'instantané.";
        ReviewBar.Visibility = review != null ? Visibility.Visible : Visibility.Collapsed;
        if (review != null) ReviewText.Text = $"Modification {review.Current + 1} sur {review.Changes.Count}";
        RefreshComments();
    }

    // MARK: Mode « Modifications »

    void EnterReviewClick(object sender, RoutedEventArgs e) => EnterReview();
    void DiscardSnapshotClick(object sender, RoutedEventArgs e) => DiscardSnapshot();
    void ExitReviewClick(object sender, RoutedEventArgs e) => ExitReview();
    void PreviousChangeClick(object sender, RoutedEventArgs e) => SelectChange(-1);
    void NextChangeClick(object sender, RoutedEventArgs e) => SelectChange(+1);
    void AcceptChangeClick(object sender, RoutedEventArgs e) => AcceptCurrentChange();
    void RejectChangeClick(object sender, RoutedEventArgs e) => RejectCurrentChange();
    void AcceptAllClick(object sender, RoutedEventArgs e) => DiscardSnapshot();

    void RejectAllClick(object sender, RoutedEventArgs e)
    {
        if (SnapshotBody() is not { } @base || current == null) return;
        if (MessageBox.Show(this, "Revenir partout au texte de l'instantané ?", "Tout refuser", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetBodyAndSave(@base);
        DiscardSnapshot();
    }

    void EnterReview(int keeping = 0)
    {
        if (current == null || SnapshotBody() is not { } @base) return;
        if (review == null) Flush();
        var text = review != null ? current.Body : Editor.Text;
        int generation = ++diffGeneration;
        Task.Run(() => TextDiff.Hunks(@base, text)).ContinueWith(t =>
        {
            if (t.IsFaulted) { App.Log(t.Exception!); return; }
            Dispatcher.BeginInvoke(() =>
            {
                if (generation != diffGeneration || current == null) return;
                var hunks = t.Result;
                pendingChanges = hunks.Count;
                if (hunks.Count == 0) { DiscardSnapshot(); return; }
                review = new ReviewState(@base, text, hunks) { Current = Math.Min(keeping, hunks.Count - 1) };
                ShowReview();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>L'éditeur montre la vue fusionnée, en lecture seule.</summary>
    void ShowReview()
    {
        if (review == null) return;
        loading = true;
        Editor.Document = new TextDocument(review.Merged);
        loading = false;
        Editor.IsReadOnly = true;
        look.DocumentFolder = System.IO.Path.GetDirectoryName(current!.Path);
        UpdateChangesUI();
        RevealChange();
    }

    void RevealChange()
    {
        if (review == null || review.Changes.Count == 0) return;
        var c = review.Changes[review.Current];
        if (c.Start <= Editor.Document.TextLength)
        {
            var loc = Editor.Document.GetLocation(c.Start);
            Editor.ScrollTo(loc.Line, loc.Column);
        }
        Editor.TextArea.TextView.Redraw();
    }

    void SelectChange(int delta)
    {
        if (review == null) return;
        review.Current = Math.Clamp(review.Current + delta, 0, review.Changes.Count - 1);
        UpdateChangesUI();
        RevealChange();
    }

    void ExitReview()
    {
        if (review == null) return;
        review = null;
        Editor.IsReadOnly = false;
        if (current != null)
        {
            int caret = Math.Min(Editor.TextArea.Caret.Offset, current.Body.Length);
            SetEditorDocument(current.Body.Replace("\r\n", "\n"));
            Editor.TextArea.Caret.Offset = Math.Min(caret, Editor.Document.TextLength);
            comments.Reanchor(Editor.Text);
        }
        UpdateChangesUI();
        RefreshPendingChanges();
    }

    void AcceptCurrentChange()
    {
        if (review == null || current == null || root == null) return;
        var change = review.Changes[review.Current];
        var newBase = TextDiff.Accept(change.Hunk, review.Base);
        // L'instantané garde l'en-tête de la version de référence ; seul le corps change.
        var doc = MarkdownDocument.Parse(Sidecar.LoadSnapshot(current.Path, root) ?? "");
        doc.Body = newBase;
        try { Sidecar.SaveSnapshot(doc.Serialized(), current.Path, root); } catch (Exception ex) { App.Log(ex); }
        EnterReview(review.Current);
    }

    void RejectCurrentChange()
    {
        if (review == null || current == null) return;
        var change = review.Changes[review.Current];
        SetBodyAndSave(TextDiff.Reject(change.Hunk, current.Body));
        EnterReview(review.Current);
    }

    /// <summary>Remplace le texte du document (refus d'une modification) et l'enregistre.</summary>
    void SetBodyAndSave(string body)
    {
        if (current == null) return;
        current.Body = body;
        try { if (current.Save()) index?.Update(current.Path); } catch (Exception ex) { App.Log(ex); SaveState.Text = "Enregistrement impossible : " + ex.Message; }
        comments.Reanchor(body);
        if (review == null)
        {
            int caret = Editor.TextArea.Caret.Offset;
            SetEditorDocument(body);
            Editor.TextArea.Caret.Offset = Math.Min(caret, Editor.Document.TextLength);
        }
        UpdateWordCount();
    }

    /// <summary>Couleurs de la vue fusionnée : supprimé barré en rouge, ajouté en vert ; la modification choisie plus marquée.</summary>
    sealed class DiffMarks : DocumentColorizingTransformer
    {
        readonly MainWindow window;
        static readonly SolidColorBrush Inserted = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0x33, 0xA8, 0x5C)));
        static readonly SolidColorBrush InsertedCurrent = Freeze(new SolidColorBrush(Color.FromArgb(0x5C, 0x33, 0xA8, 0x5C)));
        static readonly SolidColorBrush Deleted = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xDB, 0x3D, 0x3D)));
        static readonly SolidColorBrush DeletedCurrent = Freeze(new SolidColorBrush(Color.FromArgb(0x47, 0xDB, 0x3D, 0x3D)));
        static readonly TextDecorationCollection Strike = MakeStrike();

        static TextDecorationCollection MakeStrike()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xDB, 0x3D, 0x3D)), 1.2);
            pen.Freeze();
            var d = new TextDecorationCollection { new TextDecoration(TextDecorationLocation.Strikethrough, pen, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended) };
            d.Freeze();
            return d;
        }

        public DiffMarks(MainWindow window) { this.window = window; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (window.review is not { } r) return;
            int lineStart = line.Offset, lineEnd = line.EndOffset;
            // Recherche dichotomique de la première modification qui peut toucher la ligne.
            int low = 0, high = r.Changes.Count;
            while (low < high) { int mid = (low + high) / 2; if (r.Changes[mid].End < lineStart) low = mid + 1; else high = mid; }
            for (int i = low; i < r.Changes.Count && r.Changes[i].Start <= lineEnd; i++)
            {
                var c = r.Changes[i];
                bool cur = i == r.Current;
                int a = Math.Max(lineStart, c.DeletedStart), b = Math.Min(lineEnd, c.DeletedStart + c.DeletedLength);
                if (b > a) ChangeLinePart(a, b, e =>
                {
                    e.TextRunProperties.SetBackgroundBrush(cur ? DeletedCurrent : Deleted);
                    e.TextRunProperties.SetForegroundBrush(DeletedText);
                    e.TextRunProperties.SetTextDecorations(Strike);
                });
                a = Math.Max(lineStart, c.InsertedStart); b = Math.Min(lineEnd, c.InsertedStart + c.InsertedLength);
                if (b > a) ChangeLinePart(a, b, e => e.TextRunProperties.SetBackgroundBrush(cur ? InsertedCurrent : Inserted));
            }
        }
    }

    /// <summary>Clic dans une modification : elle devient la modification choisie.</summary>
    void CaretMovedForReview()
    {
        if (review == null) return;
        int at = Editor.TextArea.Caret.Offset;
        int i = review.Changes.FindIndex(c => at >= c.Start && at <= c.End && c.End > c.Start);
        if (i >= 0 && i != review.Current) { review.Current = i; UpdateChangesUI(); Editor.TextArea.TextView.Redraw(); }
    }
}
