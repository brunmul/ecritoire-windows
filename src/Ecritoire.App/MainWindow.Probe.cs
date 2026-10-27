using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
            ("03-inspecteur", () => { settings.InspectorVisible = true; UpdateInspector(); }),
            ("04-commentaires", () =>
            {
                settings.InspectorVisible = false;
                OpenProbeDocument("Discours de la méthode.md");
                Editor.Select(Math.Min(40, Editor.Document.TextLength), Math.Min(20, Math.Max(0, Editor.Document.TextLength - 40)));
                StartComment();
            }),
            ("05-recherche", () => { CancelDraft(); ShowSearch(); SearchBox.Text = "forêt"; }),
            ("06-poeme", () => { HideSearch(); OpenProbeDocument("Demain, dès l’aube.md"); }),
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
