using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace Ecritoire.App;

/// <summary>
/// Barre de titre de Windows teintée comme le rail d'icônes (ressource « Cadre ») : les deux forment un seul cadre
/// vert-gris pâle, façon ChatGPT, autour de la barre latérale plus claire et de la page blanche (Windows 11 seulement).
/// </summary>
public partial class MainWindow
{
    const int CaptionColorAttribute = 35, TextColorAttribute = 36, BorderColorAttribute = 34;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    void TintTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var frame = ((SolidColorBrush)FindResource("Cadre")).Color;
            var text = ((SolidColorBrush)FindResource("Texte")).Color;
            int caption = ColorRef(frame), border = caption, ink = ColorRef(text);
            DwmSetWindowAttribute(hwnd, CaptionColorAttribute, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, BorderColorAttribute, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, TextColorAttribute, ref ink, sizeof(int));
        }
        catch (Exception e) { App.Log(e); }
    }
}
