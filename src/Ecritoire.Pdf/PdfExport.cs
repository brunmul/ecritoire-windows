using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ecritoire.Core;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Ecritoire.Pdf;

/// <summary>
/// Export PDF (PDFExport.swift) : la page d'Écritoire en mode clair, sans les signes Markdown. A4, colonne aux
/// proportions de l'éditeur (395 pt, 355 pour un poème, corps 12,5), numéros de page centrés en bas ; un titre ne
/// reste jamais seul en bas de page. Mêmes polices, tailles, interlignes et espacements que l'app macOS.
/// </summary>
public static class PdfExport
{
    public const float FontSize = 12.5f;
    const float PageWidth = 595.28f, PageHeight = 841.89f, TopMargin = 70, BottomMargin = 76, FolioBaseline = 40;
    static float ProseColumn => R(FontSize * 600 / 19);
    static float PoemColumn => R(FontSize * 540 / 19);

    /// <summary>Arrondi comme Swift (.rounded() : moitié vers l'extérieur).</summary>
    static float R(double v) => (float)Math.Round(v, MidpointRounding.AwayFromZero);
    static float Size(double scale) => R(FontSize * scale);

    const string BodyText = "#333333", QuoteText = "#6E6E6E", Secondary = "#7A766A", Tertiary = "#B3B3B3",
        QuoteRule = "#E0E0E0", Wave = "#C9B8E8", Link = "#7B5FBF";

    // MARK: Polices

    static readonly object FontLock = new();
    static bool fontsReady;
    static string serif = "Libre Bodoni", display = "Libre Bodoni", grotesk = "Hanken Grotesk", groteskBlack = "Hanken Grotesk";

    /// <summary>Fichiers de polices embarqués (polices libres SIL OFL).</summary>
    public static readonly string[] FontFiles =
    {
        "LibreBodoni.ttf", "LibreBodoni-Italic.ttf", "HankenGrotesk.ttf", "HankenGrotesk-Italic.ttf",
    };

    /// <summary>Enregistre les polices une fois ; <paramref name="open"/> ouvre un fichier de <see cref="FontFiles"/> (null s'il manque).</summary>
    public static void RegisterFonts(Func<string, Stream?> open)
    {
        lock (FontLock)
        {
            if (fontsReady) return;
            QuestPDF.Settings.License = LicenseType.Community;
            // Polices du système : seulement en repli (emoji, symboles comme ⌘ absents des polices embarquées).
            QuestPDF.Settings.UseSystemFonts = true;
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
            foreach (var file in FontFiles)
            {
                using var s = open(file);
                if (s == null) continue;
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                FontManager.RegisterFontFromBinaryData(copy.ToArray());
            }
            var families = FontManager.GetRegisteredFonts().Select(f => f.FamilyName).Distinct().ToList();
            string? Find(string part) =>
                families.FirstOrDefault(f => f.Replace(" ", "").Contains(part, StringComparison.OrdinalIgnoreCase));
            serif = display = Find("LibreBodoni") ?? serif;
            grotesk = groteskBlack = Find("HankenGrotesk") ?? grotesk;
            fontsReady = true;
        }
    }

    /// <summary>Familles retenues (vérifications).</summary>
    public static string FontSummary => $"{serif} / {display} / {grotesk} / {groteskBlack}";

    enum Family { Serif, Display, Grotesk, GroteskBlack }

    static string[] FamilyName(Family f) => Fallback(f switch { Family.Serif => serif, Family.Display => display, Family.Grotesk => grotesk, _ => groteskBlack });

    /// <summary>Police puis replis pour les caractères absents (emoji, symboles).</summary>
    static string[] Fallback(string family) => new[] { family, "Segoe UI Symbol", "Apple Symbols", "Segoe UI Emoji", "Apple Color Emoji", "Arial Unicode MS" };

    // MARK: Document

    public static void Write(List<Export.Section> sections, string title, string destination)
    {
        var bytes = Render(sections, title);
        var temp = destination + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, destination, true);
    }

    public static byte[] Render(List<Export.Section> sections, string title) => Build(sections, title).GeneratePdf();

    /// <summary>Pages en images PNG (vérifications).</summary>
    public static IEnumerable<byte[]> RenderImages(List<Export.Section> sections, string title, int dpi = 110) =>
        Build(sections, title).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi });

    static IDocument Build(List<Export.Section> sections, string title)
    {
        if (!fontsReady) RegisterFonts(_ => null);
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(new PageSize(PageWidth, PageHeight));
                page.MarginTop(TopMargin);
                page.MarginBottom(FolioBaseline - 4);
                page.MarginHorizontal(0);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(s => s.FontFamily(Fallback(serif)).FontSize(Size(1)).FontColor(BodyText));
                page.Content().PaddingBottom(BottomMargin - FolioBaseline + 4).Column(column =>
                {
                    for (int i = 0; i < sections.Count; i++)
                    {
                        var section = sections[i];
                        var width = section.Poem ? PoemColumn : ProseColumn;
                        column.Item().PaddingTop(i == 0 ? 0 : FontSize * 1.5f).AlignCenter().Width(width).Column(c => Section(c, section, width));
                    }
                });
                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontFamily(Fallback(grotesk)).FontSize(Size(0.72)).FontColor(Secondary));
                    t.CurrentPageNumber();
                });
            });
        }).WithMetadata(new DocumentMetadata { Title = title, Creator = "Écritoire", Producer = "Écritoire" });
    }

    // MARK: Une section

    static readonly Regex HeadingRE = new(@"^(#{1,6})[ \t]+", RegexOptions.Compiled);
    static readonly Regex CalloutRE = new(@"^>[ \t]?\[!([A-Za-z-]+)\][^\n]*", RegexOptions.Compiled);
    static readonly Regex ImageRE = new(@"^!\[\[([^\]\n]+)\]\]", RegexOptions.Compiled);
    static readonly Regex ListRE = new(@"^([ \t]*)([-*+]|\d+[.)])[ \t]", RegexOptions.Compiled);
    static readonly Regex FenceRE = new(@"^(?:```|~~~)", RegexOptions.Compiled);
    static readonly Regex RuleRE = new(@"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$", RegexOptions.Compiled);
    static readonly Regex QuoteMarkerRE = new(@"^>[ \t]?", RegexOptions.Compiled);

    enum Kind { Empty, Heading, CalloutHeader, Intro, Quote, Image, Fence, Rule, List, Body }

    static (Kind Kind, int Level) KindOf(string line, bool inIntro)
    {
        if (line.Trim(' ', '\t').Length == 0) return (Kind.Empty, 0);
        if (HeadingRE.Match(line) is { Success: true } h) return (Kind.Heading, h.Groups[1].Length);
        if (CalloutRE.IsMatch(line)) return (Kind.CalloutHeader, 0);
        if (line.StartsWith('>')) return (inIntro ? Kind.Intro : Kind.Quote, 0);
        if (ImageRE.IsMatch(line)) return (Kind.Image, 0);
        if (FenceRE.IsMatch(line)) return (Kind.Fence, 0);
        if (RuleRE.IsMatch(line)) return (Kind.Rule, 0);
        if (ListRE.IsMatch(line)) return (Kind.List, 0);
        return (Kind.Body, 0);
    }

    static void Section(ColumnDescriptor column, Export.Section section, float width)
    {
        var text = Export.TextOf(section);
        if (section.Poem) text = Export.RemoveVerseNumbers(text);
        var folder = section.IsFolder ? section.Source : Path.GetDirectoryName(section.Source)!;
        int poemHeaderEnd = section.Poem ? Poem.HeaderEnd(text) : 0;
        // Séparateur ondulé avant chaque partie (le titre de la section n'en est pas une).
        var headings = Text.Paragraphs(text).Select(p => (p.Start, Line: text.Substring(p.Start, p.Length)))
            .Where(p => HeadingRE.IsMatch(p.Line)).Select(p => (p.Start, Level: HeadingRE.Match(p.Line).Groups[1].Length)).Skip(1).ToList();
        int partLevel = headings.Count > 0 ? headings.Min(h => h.Level) : 1;
        int? firstHeading = headings.Count > 0 ? headings[0].Start : null;

        bool inIntro = false;
        bool first = true;
        foreach (var (start, length, _) in Text.Paragraphs(text))
        {
            var line = text.Substring(start, length);
            if (line.StartsWith('>'))
            {
                if (CalloutRE.Match(line) is { Success: true } m) inIntro = m.Groups[1].Value.Equals("intro", StringComparison.OrdinalIgnoreCase);
            }
            else inIntro = false;
            float before = 0;
            if (section.Poem && PoemParagraph(column, line, start < poemHeaderEnd, folder, width, ref first)) continue;
            var (kind, level) = KindOf(line, inIntro);
            var p = new Para();
            switch (kind)
            {
                case Kind.Empty or Kind.CalloutHeader:
                    continue;
                case Kind.Heading:
                    line = line[HeadingRE.Match(line).Length..];
                    switch (level)
                    {
                        case 1: p.Family = Family.Display; p.Scale = 2.1; p.Bold = true; before = FontSize * 2.0f; p.After = FontSize * 0.35f; p.LineHeight = 1.05f; break;
                        case 2: p.Family = Family.Grotesk; p.Scale = 1.45; p.Bold = true; before = FontSize * 1.6f; p.After = FontSize * 0.6f; p.LineHeight = 1.1f; break;
                        case 3: p.Family = Family.GroteskBlack; p.Scale = 1.1; before = FontSize * 1.1f; p.After = FontSize * 0.4f; break;
                        default: p.Family = Family.Grotesk; p.Bold = true; before = FontSize * 0.8f; p.After = FontSize * 0.3f; break;
                    }
                    p.KeepWithNext = true;
                    break;
                case Kind.Intro:
                    line = QuoteMarkerRE.Replace(line, "", 1);
                    p.Family = Family.Grotesk; p.Scale = 1.15; p.After = FontSize * 0.6f;
                    break;
                case Kind.Quote:
                    line = QuoteMarkerRE.Replace(line, "", 1);
                    p.Family = Family.Grotesk; p.Scale = 0.97; p.Color = QuoteText; p.Justify = true; p.LineHeight = 1.2f;
                    before = FontSize * 0.5f; p.After = FontSize * 1.0f; p.QuoteRule = true;
                    break;
                case Kind.List:
                    p.Indent = FontSize * 1.5f; p.After = FontSize * 0.25f; p.Exact = true;
                    break;
                case Kind.Fence or Kind.Rule:
                    p.Color = Tertiary;
                    break;
                case Kind.Image:
                    if (Image(column, ImageRE.Match(line).Groups[1].Value, folder, width, ref first)) continue;
                    p.Color = Tertiary;
                    break;
                default:
                    p.Exact = true;
                    break;
            }
            if (kind == Kind.Heading && level == partLevel && firstHeading is { } fh && start > fh)
            {
                // Nouvelle partie : grand blanc avant le titre, avec la vague au milieu.
                float space = FontSize * 3.6f;
                if (!first) column.Item().PaddingTop(space * 0.45f - 4).Height(8).Svg(WaveSvg(width, space));
                before = first ? 0 : space * 0.55f - 4;
            }
            p.Before = first ? 0 : before;
            first = false;
            p.Text = line;
            Emit(column, p);
        }
    }

    /// <summary>Paragraphe d'un poème : en-tête centré (titre, notes en petites capitales, ornement) ou verset.</summary>
    static bool PoemParagraph(ColumnDescriptor column, string line, bool inHeader, string folder, float width, ref bool first)
    {
        var (kind, _) = KindOf(line, false);
        if (kind == Kind.Empty) return true;
        if (inHeader)
        {
            if (kind == Kind.Rule)
            {
                float h = R(FontSize * 2.6);
                column.Item().Height(h).AlignMiddle().AlignCenter().Width(80).Height(8).Svg(OrnamentSvg());
                first = false;
                return true;
            }
            var p = new Para { Center = true };
            if (kind == Kind.Heading)
            {
                p.Text = line[HeadingRE.Match(line).Length..];
                p.Family = Family.Display; p.Scale = 2.0; p.LineHeight = 1.05f;
                p.Before = first ? 0 : FontSize * 1.2f; p.After = FontSize * 0.9f;
            }
            else
            {
                p.Text = line;
                p.Family = Family.Grotesk; p.Scale = 0.74; p.Color = Secondary; p.SmallCaps = true; p.LetterSpacing = 0.4f;
                p.Inset = R(width * 0.1); p.After = FontSize * 0.55f;
            }
            first = false;
            Emit(column, p);
            return true;
        }
        if (kind != Kind.Body && kind != Kind.List) return false;
        float size = Size(1.1), lineHeight = R(size * 1.5);
        Emit(column, new Para
        {
            Text = line, Scale = 1.1, ExactHeight = lineHeight, After = lineHeight,
        });
        first = false;
        return true;
    }

    static bool Image(ColumnDescriptor column, string spec, string folder, float width, ref bool first)
    {
        var name = spec.Split('|')[0].Trim(' ', '\t');
        var file = Path.Combine(folder, name);
        if (!File.Exists(file)) return false;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); } catch (IOException) { return false; }
        if (ImageSize.Of(bytes) is not { } px || px.Width <= 0 || px.Height <= 0) return false;
        double w = Math.Min(width, px.Width), h = w * px.Height / px.Width;
        double maxH = PageHeight - TopMargin - BottomMargin - FontSize * 3;
        if (h > maxH) h = maxH;
        w = R(h * px.Width / px.Height); h = R(h);
        column.Item().PaddingTop(first ? 0 : FontSize * 0.9f).PaddingBottom(FontSize * 0.9f).AlignCenter()
            .Width((float)w).Height((float)h).Image(bytes).FitArea();
        first = false;
        return true;
    }

    // MARK: Paragraphes

    sealed class Para
    {
        public string Text = "";
        public Family Family = Family.Serif;
        public double Scale = 1;
        public bool Bold, Justify, Center, QuoteRule, KeepWithNext, SmallCaps, Exact;
        public string Color = BodyText;
        public float Before, After, Indent, Inset, LetterSpacing;
        public float? LineHeight;
        public float? ExactHeight;
    }

    /// <summary>Hauteur de ligne naturelle approximative des polices (TextKit multiplie celle-ci).</summary>
    const float NaturalLine = 1.2f;

    static void Emit(ColumnDescriptor column, Para p)
    {
        float size = Size(p.Scale);
        IContainer item = column.Item();
        if (p.Before > 0) item = item.PaddingTop(p.Before);
        if (p.After > 0) item = item.PaddingBottom(p.After);
        if (p.KeepWithNext) item = item.EnsureSpace(FontSize * 7);
        if (p.Inset > 0) item = item.PaddingHorizontal(p.Inset);
        if (p.Indent > 0 && ListRE.Match(p.Text) is { Success: true } bullet)
        {
            // Liste : la puce dans une colonne de 1,5 em, les lignes suivantes alignées sur le texte (retrait suspendu).
            var marker = bullet.Value.TrimEnd();
            var rest = p.Text[bullet.Length..];
            item.Row(row =>
            {
                row.ConstantItem(p.Indent).Text(t => { t.Span(marker.TrimStart()); t.DefaultTextStyle(s => s.FontFamily(Fallback(serif)).FontSize(R(FontSize)).FontColor(p.Color).LineHeight(R(FontSize * 1.4) / R(FontSize))); });
                var inner = new Para { Text = rest, Exact = true, Color = p.Color };
                Paragraph(row.RelativeItem(), inner, R(FontSize));
            });
            return;
        }
        if (p.QuoteRule)
            item = item.PaddingLeft(FontSize * 0.2f).BorderLeft(2.5f).BorderColor(QuoteRule).PaddingLeft(FontSize * 0.7f - 2.5f);
        Paragraph(item, p, size);
    }

    static void Paragraph(IContainer item, Para p, float size)
    {
        // Interligne : exact (corps, listes : 1,4 × le corps ; versets) ou multiple de la hauteur naturelle.
        float lineHeight = p.ExactHeight is { } e ? e / size
            : p.Exact ? R(FontSize * 1.4) / size
            : (p.LineHeight ?? 1.3f) * NaturalLine;
        item.Text(t =>
        {
            if (p.Justify) t.Justify();
            else if (p.Center) t.AlignCenter();
            else t.AlignLeft();
            t.DefaultTextStyle(s =>
            {
                s = s.FontFamily(FamilyName(p.Family)).FontSize(size).FontColor(p.Color).LineHeight(lineHeight);
                if (p.Bold) s = p.Family == Family.GroteskBlack ? s : s.Bold();
                if (p.Family == Family.GroteskBlack) s = s.Black();
                if (p.SmallCaps) s = s.EnableFontFeature("smcp");
                if (p.LetterSpacing > 0) s = s.LetterSpacing(p.LetterSpacing / size);
                return s;
            });
            foreach (var run in Inline.Runs(p.Text))
            {
                if (run.Text.Length == 0) continue;
                var span = t.Span(run.Text);
                if (run.Bold && !p.Bold) span.Bold();
                if (run.Italic) span.Italic();
                if (run.Code) span.FontFamily("Courier New", "Courier").FontSize(R(size * 0.85)).FontColor(Secondary);
                if (run.Link) span.FontColor(Link);
            }
        });
    }

    // MARK: Ornements

    static string Wavy(double left, double y, double width, double wavelength, double amplitude, int count, double strokeWidth, double height)
    {
        var sb = new StringBuilder();
        double step = width / count;
        sb.Append(CultureInfo.InvariantCulture, $"M{left:0.###},{y:0.###}");
        double x = left; bool up = true;
        for (int i = 0; i < count; i++)
        {
            double peak = up ? -amplitude : amplitude;
            sb.Append(CultureInfo.InvariantCulture,
                $" C{x + step * 0.3642:0.###},{y + peak * 1.3:0.###} {x + step * 0.6358:0.###},{y + peak * 1.3:0.###} {x + step:0.###},{y:0.###}");
            x += step; up = !up;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{left * 2 + width:0.###}\" height=\"{height:0.###}\" viewBox=\"0 0 {left * 2 + width:0.###} {height:0.###}\"><path d=\"{sb}\" fill=\"none\" stroke=\"{Wave}\" stroke-width=\"{strokeWidth:0.###}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>");
    }

    /// <summary>Séparateur de parties : vague pleine sur toute la colonne (MarkdownHighlighter.swift : drawSeparator).</summary>
    static string WaveSvg(float width, float space)
    {
        double scale = Math.Max(0.6, space / (17 * 3.6));
        double wavelength = 15 * scale, amplitude = 2 * scale;
        int count = Math.Max(1, (int)Math.Floor(width / (wavelength / 2)));
        return Wavy(1.5, 4, width - 3, wavelength, amplitude, count, 1.25 * Math.Max(1, scale * 0.9), 8);
    }

    /// <summary>Ligne « --- » d'un poème : petite vague centrée (drawOrnament).</summary>
    static string OrnamentSvg()
    {
        double scale = FontSize / 19.0;
        double wavelength = 12 * scale, amplitude = 2.2 * scale;
        double total = wavelength / 2 * 6;
        return Wavy((80 - total) / 2, 4, total, wavelength, amplitude, 6, 1.3 * Math.Max(1, scale * 0.9), 8);
    }
}
