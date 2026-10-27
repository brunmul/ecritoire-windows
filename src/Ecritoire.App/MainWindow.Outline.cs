using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ecritoire.App;

/// <summary>
/// Réglette de navigation (interface v2 de l'app macOS) : à gauche de la colonne, un petit trait par titre du document ;
/// le trait de la section courante est plus long et noir. Au survol, une carte montre le titre et le début de la
/// section ; un clic y fait défiler le texte en douceur. Le trait courant suit le défilement.
/// </summary>
public partial class MainWindow
{
    sealed record OutlineEntry(int Line, int Level, string Title, string Excerpt);

    static readonly Regex OutlineHeadingRE = new(@"^(#{1,6})[ \t]+(.+?)[ \t#]*$", RegexOptions.Compiled);
    static readonly Regex OutlineFenceRE = new(@"^\s*(```|~~~)", RegexOptions.Compiled);
    static readonly Regex OutlineInlineRE = new(@"%%.*?%%|<!--.*?-->|<[^>]+>|!\[\[[^\]]*\]\]|\[\[(?:[^\]|]*\|)?([^\]]*)\]\]|\[([^\]]*)\]\([^)]*\)|[*_=`]|^\s*>\s?|^\s*\[\d+\]\s*", RegexOptions.Compiled);
    static readonly SolidColorBrush TickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
    static readonly SolidColorBrush TickHover = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));
    static readonly SolidColorBrush TickCurrent = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));

    readonly DispatcherTimer outlineTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly List<OutlineEntry> outline = new();
    readonly List<Border> outlineBars = new();
    int outlineCurrent = -2;
    DispatcherTimer? scrollAnimation;

    void InitOutline()
    {
        outlineTimer.Tick += (_, _) => { outlineTimer.Stop(); RebuildOutline(); };
        // Frappe : réglette relue après une pause (à l'ouverture d'un document, OpenDocument s'en charge).
        Editor.TextChanged += (_, _) => { if (loading) return; outlineTimer.Stop(); outlineTimer.Start(); };
        // Différé : pendant un changement de document, la vue n'est pas encore prête à situer les lignes.
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Dispatcher.BeginInvoke(UpdateOutlineCurrent, DispatcherPriority.Background);
        OutlineItem.IsChecked = settings.OutlineVisible;
    }

    void OutlineToggleClick(object sender, RoutedEventArgs e)
    {
        settings.OutlineVisible = !settings.OutlineVisible;
        OutlineItem.IsChecked = settings.OutlineVisible;
        RebuildOutline();
    }

    /// <summary>Titres du document (hors blocs de code), avec un court extrait de chaque section.</summary>
    void RebuildOutline()
    {
        outline.Clear();
        if (current != null && Editor.Visibility == Visibility.Visible)
        {
            var doc = Editor.Document;
            bool fence = false;
            var found = new List<(int Line, int Level, string Title)>();
            for (int i = 1; i <= doc.LineCount; i++)
            {
                var s = doc.GetText(doc.GetLineByNumber(i));
                if (OutlineFenceRE.IsMatch(s)) { fence = !fence; continue; }
                if (!fence && OutlineHeadingRE.Match(s) is { Success: true } m)
                    found.Add((i, m.Groups[1].Length, Plain(m.Groups[2].Value)));
            }
            for (int k = 0; k < found.Count; k++)
            {
                var (line, level, title) = found[k];
                int end = k + 1 < found.Count ? found[k + 1].Line : doc.LineCount + 1;
                var excerpt = new System.Text.StringBuilder();
                for (int i = line + 1; i < end && excerpt.Length < 220; i++)
                {
                    var s = Plain(doc.GetText(doc.GetLineByNumber(i)));
                    if (s.Length == 0 || s.StartsWith("---")) continue;
                    excerpt.Append(excerpt.Length > 0 ? " " : "").Append(s);
                }
                var text = excerpt.ToString();
                outline.Add(new OutlineEntry(line, level, title.Length > 0 ? title : "Sans titre", text.Length > 200 ? text[..200].TrimEnd() + "…" : text));
            }
        }

        OutlineRuler.Children.Clear();
        outlineBars.Clear();
        outlineCurrent = -2;
        OutlinePopup.IsOpen = false;
        bool show = settings.OutlineVisible && outline.Count >= 2 && review == null;
        OutlineRuler.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        // Espacement régulier ; resserré quand les titres sont nombreux, pour tenir dans la hauteur visible.
        double available = Math.Max(120, EditorArea.ActualHeight - 160);
        double pitch = Math.Clamp(available / outline.Count, 5, 14);
        for (int k = 0; k < outline.Count; k++)
        {
            var entry = outline[k];
            var bar = new Border
            {
                Height = pitch >= 8 ? 2 : 1.5, Width = entry.Level >= 3 ? 8 : 12, CornerRadius = new CornerRadius(1),
                Background = TickBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            var hit = new Border { Height = pitch, Width = 44, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = bar, Padding = new Thickness(10, 0, 0, 0) };
            int index = k;
            hit.MouseEnter += (_, _) => ShowOutlinePreview(index, hit);
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; OutlinePopup.IsOpen = false; ScrollToLine(outline[index].Line); };
            outlineBars.Add(bar);
            OutlineRuler.Children.Add(hit);
        }
        UpdateOutlineCurrent();
    }

    static string Plain(string s) =>
        Regex.Replace(OutlineInlineRE.Replace(s, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : ""), @"\s+", " ").Trim();

    void ShowOutlinePreview(int index, FrameworkElement target)
    {
        if (index >= outlineBars.Count) return;
        for (int k = 0; k < outlineBars.Count; k++)
            if (k != outlineCurrent) outlineBars[k].Background = k == index ? TickHover : TickBrush;
        OutlinePopupTitle.Text = outline[index].Title;
        OutlinePopupExcerpt.Text = outline[index].Excerpt;
        OutlinePopupExcerpt.Visibility = outline[index].Excerpt.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OutlinePopup.PlacementTarget = target;
        OutlinePopup.VerticalOffset = -22;
        OutlinePopup.IsOpen = true;
    }

    void OutlineMouseLeave(object sender, MouseEventArgs e)
    {
        OutlinePopup.IsOpen = false;
        for (int k = 0; k < outlineBars.Count; k++) if (k != outlineCurrent) outlineBars[k].Background = TickBrush;
    }

    /// <summary>Section courante : le dernier titre passé en haut de la fenêtre.</summary>
    void UpdateOutlineCurrent()
    {
        var view = Editor.TextArea.TextView;
        if (outlineBars.Count == 0 || view.Document == null || view.Document != Editor.Document) return;
        double top = view.VerticalOffset + 48;
        int now = -1;
        try
        {
            for (int k = 0; k < outline.Count; k++)
            {
                if (outline[k].Line > Editor.Document.LineCount) break;
                if (view.GetVisualTopByDocumentLine(outline[k].Line) <= top) now = k; else break;
            }
        }
        catch (InvalidOperationException) { return; }
        if (now == outlineCurrent) return;
        outlineCurrent = now;
        for (int k = 0; k < outlineBars.Count; k++)
        {
            bool cur = k == now;
            outlineBars[k].Background = cur ? TickCurrent : TickBrush;
            outlineBars[k].Width = cur ? 24 : outline[k].Level >= 3 ? 8 : 12;
        }
    }

    /// <summary>Défilement animé (un quart de seconde, ralenti à l'arrivée) jusqu'à une ligne du document.</summary>
    void ScrollToLine(int line)
    {
        scrollAnimation?.Stop();
        var view = Editor.TextArea.TextView;
        double from = view.VerticalOffset;
        double to = Math.Max(0, view.GetVisualTopByDocumentLine(line) - Math.Round(16 * settings.Zoom));
        var started = DateTime.Now;
        const double duration = 260;
        scrollAnimation = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        scrollAnimation.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - started).TotalMilliseconds / duration);
            double eased = 1 - Math.Pow(1 - t, 3);
            Editor.ScrollToVerticalOffset(from + (to - from) * eased);
            if (t < 1) return;
            scrollAnimation?.Stop();
            // Les hauteurs des lignes pas encore affichées sont estimées : une fois arrivé, on recale sur la vraie position.
            void Settle()
            {
                double exact = Math.Max(0, view.GetVisualTopByDocumentLine(line) - Math.Round(16 * settings.Zoom));
                if (Math.Abs(exact - view.VerticalOffset) > 1) Editor.ScrollToVerticalOffset(exact);
            }
            Settle();
            Dispatcher.BeginInvoke(Settle, DispatcherPriority.Background);
        };
        scrollAnimation.Start();
    }
}
