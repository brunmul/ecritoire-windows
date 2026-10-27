using System.Windows.Markup;
using System.Windows.Media;

namespace Ecritoire.App;

/// <summary>Dessin d'icône pour le XAML : <c>&lt;Image Source="{local:Icon search, Size=16}" Width="16" Height="16"/&gt;</c>.
/// Size règle l'épaisseur du trait ; couleur par défaut : gris foncé des icônes de l'app macOS.</summary>
[MarkupExtensionReturnType(typeof(DrawingImage))]
public sealed class IconExtension : MarkupExtension
{
    public IconExtension() { }
    public IconExtension(string name) { Name = name; }
    public string Name { get; set; } = "";
    public double Size { get; set; } = 18;
    public string Color { get; set; } = "#5C5C5C";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Color));
        brush.Freeze();
        return Icons.Drawing(Name, brush, Icons.StrokeFor(Size));
    }
}
