using System.Windows.Media;

namespace Ecritoire.App;

/// <summary>Catalogue des icônes du classeur (BinderIcons.swift) : identifiant → glyphe Segoe Fluent Icons et couleur.</summary>
public static class BinderIcons
{
    static SolidColorBrush Rgb(uint v)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush FolderBlue = Rgb(0x8DBEEB);
    public static readonly SolidColorBrush DocumentGray = Rgb(0x858585);
    const string Book = "", Folder = "", Document = "";

    static readonly Dictionary<string, (string Glyph, SolidColorBrush Color)> Catalog = new()
    {
        ["carnet-rouge"] = (Book, Rgb(0xE5937D)), ["carnet-orange"] = (Book, Rgb(0xEDAA6E)), ["carnet-jaune"] = (Book, Rgb(0xEFCD62)),
        ["carnet-vert"] = (Book, Rgb(0x8CC57A)), ["carnet-bleu"] = (Book, Rgb(0x86B4E3)), ["carnet-violet"] = (Book, Rgb(0x9467BD)),
        ["carnet-rose"] = (Book, Rgb(0xE07AA0)), ["carnet-brun"] = (Book, Rgb(0x9C6B45)), ["carnet-noir"] = (Book, Rgb(0x3D3929)),
        ["notes"] = ("", Rgb(0x8A877C)), ["eprouvette"] = ("", Rgb(0xB48BDB)), ["crayon"] = ("", Rgb(0xD9A441)),
        ["carte"] = ("", Rgb(0xD9776A)), ["classeur"] = ("", Rgb(0x9E9A90)), ["calendrier"] = ("", Rgb(0xD9534F)),
        ["liminaire"] = ("", Rgb(0x8A877C)), ["pellicule"] = ("", Rgb(0x5B5B66)), ["spirale"] = (Book, Rgb(0x4A86C8)),
        ["personnage"] = ("", Rgb(0xE8954A)), ["idee"] = ("", Rgb(0xE6B83A)), ["etoile"] = ("", Rgb(0xE6B83A)),
        ["drapeau"] = ("", Rgb(0xD9534F)), ["epingle"] = ("", Rgb(0xD9534F)), ["manuscrit"] = (Book, Rgb(0x7B5FBF)),
        ["recherche"] = ("", Rgb(0x4A86C8)),
    };

    public static (string Glyph, Brush Color) For(string? icon, bool isFolder)
    {
        if (icon != null && Catalog.TryGetValue(icon, out var hit)) return hit;
        return isFolder ? (Folder, FolderBlue) : (Document, DocumentGray);
    }
}
