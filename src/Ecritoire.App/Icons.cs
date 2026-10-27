using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace Ecritoire.App;

/// <summary>
/// Icônes au trait (pack Claude du répertoire de ressources, et quelques icônes d'interface dessinées dans le même
/// style : grille 24, trait 1,5, bouts ronds). Les SVG sont embarqués dans Ressources/Icones et dessinés en vectoriel.
/// </summary>
public static class Icons
{
    static readonly Dictionary<string, List<(Geometry Geometry, bool Filled, double[]? Dash)>> Cache = new();

    static List<(Geometry, bool, double[]?)> Load(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;
        var parts = new List<(Geometry, bool, double[]?)>();
        try
        {
            var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Ressources/Icones/{name}.svg"));
            if (info != null)
            {
                using var stream = info.Stream;
                var svg = XDocument.Load(stream).Root!;
                foreach (var e in svg.Elements())
                {
                    double A(string n) => double.Parse((string?)e.Attribute(n) ?? "0", CultureInfo.InvariantCulture);
                    Geometry? g;
                    try
                    {
                    g = e.Name.LocalName switch
                    {
                        "path" => Geometry.Parse((string)e.Attribute("d")!),
                        "circle" => new EllipseGeometry(new Point(A("cx"), A("cy")), A("r"), A("r")),
                        "ellipse" => new EllipseGeometry(new Point(A("cx"), A("cy")), A("rx"), A("ry")),
                        "rect" => new RectangleGeometry(new Rect(A("x"), A("y"), A("width"), A("height")), A("rx"), e.Attribute("ry") != null ? A("ry") : A("rx")),
                        _ => null,
                    };
                    }
                    catch (FormatException ex) { App.Log(new Exception($"Icône {name} : {ex.Message}")); continue; }
                    if (g == null) continue;
                    g.Freeze();
                    bool filled = (string?)e.Attribute("fill") == "currentColor";
                    var dash = ((string?)e.Attribute("stroke-dasharray"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => double.Parse(s, CultureInfo.InvariantCulture) / 1.5).ToArray();
                    parts.Add((g, filled, dash));
                }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        Cache[name] = parts;
        return parts;
    }

    /// <summary>Dessin de l'icône, dans la couleur demandée.</summary>
    public static DrawingImage Drawing(string name, Brush brush, double strokeWidth = 1.5)
    {
        var group = new DrawingGroup();
        // Cadre invisible 24 × 24 : toutes les icônes gardent la même taille et le même centrage.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        foreach (var (g, filled, dash) in Load(name))
        {
            Pen? pen = null;
            if (!filled)
            {
                pen = new Pen(brush, strokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (dash != null) { pen.DashStyle = new DashStyle(dash, 0); pen.DashCap = PenLineCap.Round; }
            }
            group.Children.Add(new GeometryDrawing(filled ? brush : null, pen, g));
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>Icône prête à poser (taille en pixels logiques), trait d'environ 1,3 px à l'écran.</summary>
    public static Image Make(string name, double size, Brush brush) => new()
    {
        Source = Drawing(name, brush, StrokeFor(size)),
        Width = size, Height = size, SnapsToDevicePixels = true, Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Épaisseur du trait dans la grille 24 pour garder un trait net à toutes les tailles.</summary>
    public static double StrokeFor(double size) => Math.Max(1.5, 1.3 * 24 / Math.Max(8, size));
}
