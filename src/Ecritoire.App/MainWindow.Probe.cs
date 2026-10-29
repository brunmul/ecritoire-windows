using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ecritoire.Core;

namespace Ecritoire.App;

/// <summary>
/// Vérification automatique (comme les sondes de l'app macOS) :
/// <c>Ecritoire.exe --captures dossier --classeur chemin</c> ouvre le classeur, enregistre des captures de
/// l'interface dans plusieurs états (vue générale, image, inspecteur, commentaires, recherche, poème), puis quitte.
/// Les réglages de l'utilisateur ne sont pas modifiés.
/// </summary>
public partial class MainWindow
{
    public static string? ProbeFolder, ProbeBinder;

    void RunProbe()
    {
        if (ProbeFolder == null || ProbeBinder == null) return;
        settingsLocked = true; // les réglages de l'utilisateur ne sont jamais réécrits par une vérification
        Directory.CreateDirectory(ProbeFolder);
        var steps = new List<(string Name, Action Act)>
        {
            ("01-vue-generale", () => OpenProbeDocument("À propos.md")),
            ("02-image", () => { OpenProbeDocument("Discours de la méthode.md"); Editor.ScrollToEnd(); }),
            ("03-inspecteur", () => { OpenProbeDocument("Personnages.md"); settings.InspectorVisible = true; UpdateInspector(); }),
            ("03b-reglette", () =>
            {
                settings.InspectorVisible = false; UpdateInspector();
                OpenProbeDocument("Discours de la méthode.md");
                Dispatcher.BeginInvoke(() =>
                {
                    if (outline.Count > 2) ScrollToLine(outline[1].Line);
                    if (OutlineRuler.Children.Count > 2) ShowOutlinePreview(2, (FrameworkElement)OutlineRuler.Children[2]);
                }, DispatcherPriority.ApplicationIdle);
            }),
            ("03c-barre-ia", () => { OutlinePopup.IsOpen = false; Editor.Select(60, 80); AiBox.Text = "Resserre ce passage sans changer le sens"; }),
            ("04-commentaires", () =>
            {
                settings.InspectorVisible = false;
                OpenProbeDocument("Discours de la méthode.md");
                Editor.Select(Math.Min(40, Editor.Document.TextLength), Math.Min(20, Math.Max(0, Editor.Document.TextLength - 40)));
                StartComment();
            }),
            ("05-recherche", () => { CancelDraft(); ShowSearch(); SearchBox.Text = "forêt"; }),
            ("06-poeme", () => { HideSearch(); OpenProbeDocument("Demain, dès l’aube.md"); }),
            ("06b-renommage", () => { if (current != null) BeginRename(current.Path); }),
            ("06c-glisser-entre", () => ProbeDrop(0.1)),
            ("06d-glisser-dans-dossier", () => ProbeDrop(0.5)),
            ("06e-fin", ClearDropMarker),
            ("07-barre-masquee", () => CollapseSidebarClick(this, new RoutedEventArgs())),
        };
        int i = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            if (i > 0) Capture(steps[i - 1].Name);
            if (i == steps.Count) { timer.Stop(); Close(); return; }
            try { steps[i].Act(); } catch (Exception ex) { App.Log(ex); File.AppendAllText(Path.Combine(ProbeFolder, "erreurs.txt"), $"{steps[i].Name} : {ex}\n"); }
            i++;
        };
        timer.Start();
    }

    /// <summary>Repère de dépôt tel qu'il s'afficherait en glissant le document ouvert sur la ligne du premier dossier
    /// (<paramref name="fraction"/> : hauteur dans la ligne ; 0,1 = juste avant, 0,5 = dedans).</summary>
    void ProbeDrop(double fraction)
    {
        Tree.Focus(); // termine un renommage en cours
        if (current == null || tree?.Find(current.Path) is not { } moving) return;
        var row = VisibleRows().FirstOrDefault(r => r.Node.IsFolder && !Binder.SamePath(r.Node.Path, Path.GetDirectoryName(current.Path)!));
        if (row.Item == null) return;
        var p = new Point(row.Bounds.Left + 40, row.Bounds.Top + row.Bounds.Height * fraction);
        if (DropTarget(p, moving) is { } t) ShowDropMarker(t.Box, t.Line); else ClearDropMarker();
    }

    void OpenProbeDocument(string name)
    {
        var path = Directory.EnumerateFiles(ProbeBinder!, "*.md", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Normalize() == name.Normalize());
        if (path == null) return;
        OpenDocument(path);
        SelectInTree(path);
    }

    void Capture(string name)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                // Les fenêtres surgissantes (aperçu de la réglette) ne font pas partie du contenu : on les ajoute à leur place.
                if (OutlinePopup.IsOpen && OutlinePopup.Child is FrameworkElement card && card.IsVisible)
                {
                    var at = card.PointToScreen(new Point(0, 0)) - content.PointToScreen(new Point(0, 0));
                    dc.DrawRectangle(new VisualBrush(card), null, new Rect(at.X / dpi.DpiScaleX, at.Y / dpi.DpiScaleY, card.ActualWidth, card.ActualHeight));
                }
                // Le repère de dépôt vit dans la couche d'ornements de la fenêtre, hors du contenu.
                if (dropMarker != null)
                {
                    var at = Tree.TranslatePoint(new Point(0, 0), content);
                    dc.PushTransform(new TranslateTransform(at.X, at.Y));
                    dropMarker.Draw(dc);
                    dc.Pop();
                }
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(ProbeFolder!, name + ".png"));
            encoder.Save(file);
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
