using System.Windows;
using System.Windows.Media;

namespace Ecritoire.App;

/// <summary>Couleurs et polices de l'app macOS (Theme.swift, MarkdownHighlighter.swift).</summary>
public static class Theme
{
    static SolidColorBrush Brush(uint rgb, byte alpha = 255)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush BodyText = Brush(0x333333);
    public static readonly SolidColorBrush QuoteText = Brush(0x6E6E6E);
    public static readonly SolidColorBrush Secondary = Brush(0x7A766A);
    public static readonly SolidColorBrush Accent = Brush(0x7B5FBF);
    public static readonly SolidColorBrush Marker = Brush(0x3D3929, 0x55);
    public static readonly SolidColorBrush QuoteRule = Brush(0x000000, 0x1A);
    public static readonly SolidColorBrush Wave = Brush(0xC9B8E8);
    public static readonly SolidColorBrush VerseNumber = Brush(0xB39EDB);
    public static readonly SolidColorBrush Link = Brush(0x7B5FBF);
    public static readonly SolidColorBrush Highlight = Brush(0xFFDB4D, 0x8C);

    static readonly Uri Base = new("pack://application:,,,/");

    /// <summary>Police embarquée si présente (Walbaum dans une construction personnelle), sinon la police libre de repli.</summary>
    static FontFamily Pick(params string[] names)
    {
        try
        {
            var embedded = Fonts.GetFontFamilies(new Uri(Base, "./Polices/"))
                .SelectMany(f => f.FamilyNames.Values.Append(f.Source.Split('#').Last())).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                if (embedded.Contains(name)) return new FontFamily(Base, "./Polices/#" + name);
        }
        catch (Exception e) { App.Log(e); }
        // Repli sûr : police libre embarquée, sinon police du système.
        try { return new FontFamily(Base, "./Polices/#" + names[^1] + ", Georgia"); } catch { return new FontFamily("Georgia"); }
    }

    /// <summary>Texte : Walbaum 10 XL (repli : Libre Bodoni).</summary>
    public static readonly FontFamily Serif = Pick("Walbaum 10 XL Pro", "Libre Bodoni");
    /// <summary>Grands titres : Walbaum 120 (repli : Libre Bodoni).</summary>
    public static readonly FontFamily Display = Pick("Walbaum 120 Pro", "Walbaum 10 XL Pro", "Libre Bodoni");
    /// <summary>Sous-titres, intro, citations : Walbaum Grotesk (repli : Hanken Grotesk).</summary>
    public static readonly FontFamily Grotesk = Pick("WalbaumGrotesk Book", "Hanken Grotesk");
    public static readonly FontFamily GroteskBlack = Pick("WalbaumGrotesk Black", "WalbaumGrotesk Book", "Hanken Grotesk");
    public static bool HasWalbaum => Serif.Source.Contains("Walbaum");

    /// <summary>Corps du texte (19 pt dans l'app macOS) et largeur de colonne (600, 540 pour un poème).</summary>
    public const double BaseFontSize = 19, BaseColumn = 600, BasePoemColumn = 540, BasePagePadding = 72;
}
