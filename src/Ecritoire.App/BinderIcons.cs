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

    public static readonly SolidColorBrush FolderGray = Rgb(0x6B6B6B);
    public static readonly SolidColorBrush DocumentGray = Rgb(0x858585);

    /// <summary>Identifiant du classeur → icône du pack (Ressources/Icones) et couleur.</summary>
    static readonly Dictionary<string, (string Icon, SolidColorBrush Color)> Catalog = new()
    {
        ["carnet-rouge"] = ("book", Rgb(0xE5937D)), ["carnet-orange"] = ("book", Rgb(0xEDAA6E)), ["carnet-jaune"] = ("book", Rgb(0xE0B83A)),
        ["carnet-vert"] = ("book", Rgb(0x7DB86A)), ["carnet-bleu"] = ("book", Rgb(0x6FA3D9)), ["carnet-violet"] = ("book", Rgb(0x9467BD)),
        ["carnet-rose"] = ("book", Rgb(0xE07AA0)), ["carnet-brun"] = ("book", Rgb(0x9C6B45)), ["carnet-noir"] = ("book", Rgb(0x3D3929)),
        ["notes"] = ("note", Rgb(0x8A877C)), ["eprouvette"] = ("flask", Rgb(0xB48BDB)), ["crayon"] = ("pencil", Rgb(0xD9A441)),
        ["carte"] = ("map-pin", Rgb(0xD9776A)), ["classeur"] = ("tray", Rgb(0x9E9A90)), ["calendrier"] = ("calendar", Rgb(0xD9534F)),
        ["liminaire"] = ("copy", Rgb(0x8A877C)), ["pellicule"] = ("video-camera", Rgb(0x5B5B66)), ["spirale"] = ("books", Rgb(0x4A86C8)),
        ["personnage"] = ("id-card", Rgb(0xE8954A)), ["idee"] = ("lightbulb", Rgb(0xE6B83A)), ["etoile"] = ("star", Rgb(0xE6B83A)),
        ["drapeau"] = ("flag", Rgb(0xD9534F)), ["epingle"] = ("bookmark", Rgb(0xD9534F)), ["manuscrit"] = ("scroll", Rgb(0x7B5FBF)),
        ["recherche"] = ("binoculars", Rgb(0x4A86C8)),
    };

    /// <summary>Icônes proposées dans le menu du classeur, dans l'ordre de l'app macOS, avec leur nom.</summary>
    public static readonly (string Id, string Label)[] Labels =
    {
        ("carnet-rouge", "Carnet rouge"), ("carnet-orange", "Carnet orange"), ("carnet-jaune", "Carnet jaune"), ("carnet-vert", "Carnet vert"),
        ("carnet-bleu", "Carnet bleu"), ("carnet-violet", "Carnet violet"), ("carnet-rose", "Carnet rose"), ("carnet-brun", "Carnet brun"),
        ("carnet-noir", "Carnet noir"), ("notes", "Bloc-notes"), ("eprouvette", "Éprouvette"), ("crayon", "Crayon"), ("carte", "Carte"),
        ("classeur", "Classeur à tiroirs"), ("calendrier", "Calendrier"), ("liminaire", "Pages liminaires"), ("pellicule", "Pellicule"),
        ("spirale", "Carnet à spirale"), ("personnage", "Personnage"), ("idee", "Idée"), ("etoile", "Étoile"), ("drapeau", "Drapeau"),
        ("epingle", "Épingle"), ("manuscrit", "Manuscrit"), ("recherche", "Recherche"),
    };

    /// <summary>Icône d'un élément : celle choisie, sinon un dossier au trait (les documents n'en ont pas, comme sur Mac).</summary>
    public static (string? Icon, Brush Color) For(string? icon, bool isFolder)
    {
        if (icon != null && Catalog.TryGetValue(icon, out var hit)) return hit;
        return isFolder ? ("folder", FolderGray) : (null, DocumentGray);
    }
}
