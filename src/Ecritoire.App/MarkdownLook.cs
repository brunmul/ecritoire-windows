using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using System.Windows.Shapes;
using Ecritoire.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace Ecritoire.App;

/// <summary>Nature d'un paragraphe (une ligne logique de l'éditeur).</summary>
public enum LineKind { Body, Empty, Heading, Quote, Intro, CalloutHeader, Image, List, Fence, Rule, PoemTitle, PoemNote, PoemOrnament, Verse }

/// <summary>
/// Mise en forme du Markdown façon traitement de texte, comme l'app macOS (MarkdownHighlighter.swift) : titres, corps,
/// intro, citations en Grotesk grise avec un filet, gras, italique, liens, surlignages. Les signes Markdown sont masqués,
/// sauf dans le paragraphe du curseur où ils apparaissent estompés. En affichage poème : bloc titre centré, versets en
/// retrait suspendu, numéros « [n] » masqués et dessinés dans la marge.
/// </summary>
public sealed class MarkdownLook
{
    public TextEditor Editor { get; }
    public bool PoemMode { get; private set; }
    public double FontSize { get; private set; } = Theme.BaseFontSize;
    /// <summary>Dossier du document (images « ![[…]] »).</summary>
    public string? DocumentFolder { get; set; }
    LineKind[] kinds = Array.Empty<LineKind>();
    int[] headingLevels = Array.Empty<int>();
    int activeLine = -1;
    readonly Gutter gutter;

    public MarkdownLook(TextEditor editor)
    {
        Editor = editor;
        editor.TextArea.TextView.LineTransformers.Add(new Colorizer(this));
        editor.TextArea.TextView.ElementGenerators.Add(new MarkerHider(this));
        editor.TextArea.TextView.BackgroundRenderers.Add(new QuoteRuleRenderer(this));
        gutter = new Gutter(this);
        editor.TextArea.LeftMargins.Insert(0, gutter);
        editor.TextArea.Caret.PositionChanged += (_, _) => CaretMoved();
        editor.Document.TextChanged += (_, _) => Reclassify();
        editor.DocumentChanged += (_, _) =>
        {
            editor.Document.TextChanged += (_, _) => Reclassify();
            activeLine = -1;
            Reclassify();
        };
    }

    public void Configure(bool poem, double fontSize)
    {
        PoemMode = poem;
        FontSize = fontSize;
        // Police par défaut = celle des lignes vides (les lignes de texte reçoivent la leur du Colorizer).
        Editor.FontFamily = Theme.ParagraphGap;
        Editor.FontSize = poem ? Math.Round(fontSize * 1.1) : fontSize;
        // Retrait suspendu des versets : lignes suivantes rentrées de 1,5 em.
        Editor.Options.WordWrapIndentation = poem ? Math.Round(Editor.FontSize * 1.5) : 0;
        gutter.InvalidateMeasure();
        Reclassify();
    }

    public LineKind KindOf(int lineNumber) => lineNumber - 1 < kinds.Length && lineNumber >= 1 ? kinds[lineNumber - 1] : LineKind.Body;
    public int HeadingLevel(int lineNumber) => lineNumber - 1 < headingLevels.Length && lineNumber >= 1 ? headingLevels[lineNumber - 1] : 0;
    public bool IsActive(int lineNumber) => lineNumber == activeLine;

    static readonly Regex HeadingRE = new(@"^(#{1,6})[ \t]+", RegexOptions.Compiled);
    static readonly Regex CalloutRE = new(@"^>[ \t]?\[!([A-Za-z-]+)\]", RegexOptions.Compiled);
    static readonly Regex ImageRE = new(@"^!\[\[([^\]\n]+)\]\]\s*$", RegexOptions.Compiled);
    static readonly Regex ListRE = new(@"^([ \t]*)([-*+]|\d+[.)])[ \t]", RegexOptions.Compiled);
    static readonly Regex FenceRE = new(@"^(?:```|~~~)", RegexOptions.Compiled);
    static readonly Regex RuleRE = new(@"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$", RegexOptions.Compiled);

    /// <summary>Recalcule la nature de chaque paragraphe (le contexte compte : un encadré « intro » s'étend sur plusieurs lignes).</summary>
    void Reclassify()
    {
        var doc = Editor.Document;
        int n = doc.LineCount;
        kinds = new LineKind[n];
        headingLevels = new int[n];
        var text = doc.Text;
        int headerEnd = PoemMode ? Poem.HeaderEnd(text) : 0;
        bool intro = false;
        for (int i = 1; i <= n; i++)
        {
            var line = doc.GetLineByNumber(i);
            var s = doc.GetText(line);
            LineKind k;
            if (s.Trim().Length == 0) k = LineKind.Empty;
            else if (PoemMode && line.Offset < headerEnd)
            {
                if (HeadingRE.IsMatch(s)) { k = LineKind.PoemTitle; headingLevels[i - 1] = HeadingRE.Match(s).Groups[1].Length; }
                else k = RuleRE.IsMatch(s) ? LineKind.PoemOrnament : LineKind.PoemNote;
            }
            else if (HeadingRE.Match(s) is { Success: true } h) { k = LineKind.Heading; headingLevels[i - 1] = h.Groups[1].Length; }
            else if (CalloutRE.Match(s) is { Success: true } c) { k = LineKind.CalloutHeader; intro = c.Groups[1].Value.Equals("intro", StringComparison.OrdinalIgnoreCase); }
            else if (s.StartsWith('>')) k = intro ? LineKind.Intro : LineKind.Quote;
            else if (ImageRE.IsMatch(s)) k = LineKind.Image;
            else if (FenceRE.IsMatch(s)) k = LineKind.Fence;
            else if (RuleRE.IsMatch(s)) k = LineKind.Rule;
            else if (PoemMode && Poem.Marker(s) != null) k = LineKind.Verse;
            else if (ListRE.IsMatch(s)) k = LineKind.List;
            else k = PoemMode && line.Offset >= headerEnd && headerEnd > 0 ? LineKind.Verse : LineKind.Body;
            if (!s.StartsWith('>')) intro = false;
            kinds[i - 1] = k;
        }
        gutter.InvalidateVisual();
    }

    void CaretMoved()
    {
        int line = Editor.TextArea.Caret.Line;
        if (line == activeLine) return;
        var view = Editor.TextArea.TextView;
        var doc = Editor.Document;
        if (activeLine >= 1 && activeLine <= doc.LineCount) view.Redraw(doc.GetLineByNumber(activeLine));
        activeLine = line;
        if (line >= 1 && line <= doc.LineCount) view.Redraw(doc.GetLineByNumber(line));
        gutter.InvalidateVisual();
    }

    /// <summary>Largeur de la colonne de texte.</summary>
    public double ColumnWidth => Math.Max(100, Editor.TextArea.TextView.ActualWidth);

    // MARK: Polices

    public Typeface Face(FontFamily family, bool bold = false, bool italic = false) =>
        new(family, italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    /// <summary>Police et taille d'un paragraphe selon sa nature (mêmes proportions que l'app macOS).</summary>
    public (FontFamily Family, double Size, bool Bold, Brush Color) Style(LineKind kind, int level)
    {
        double f = FontSize;
        return kind switch
        {
            LineKind.Heading => level switch
            {
                1 => (Theme.Display, Math.Round(f * 2.1), true, Theme.BodyText),
                2 => (Theme.Grotesk, Math.Round(f * 1.45), true, Theme.BodyText),
                3 => (Theme.GroteskBlack, Math.Round(f * 1.1), !Theme.GroteskBlack.Source.Contains("Black"), Theme.BodyText),
                _ => (Theme.Grotesk, f, true, Theme.BodyText),
            },
            LineKind.Intro => (Theme.Grotesk, Math.Round(f * 1.15), false, Theme.BodyText),
            LineKind.Quote => (Theme.Grotesk, Math.Round(f * 0.97), false, Theme.QuoteText),
            LineKind.PoemTitle => (Theme.Display, Math.Round(f * 2.0), false, Theme.BodyText),
            LineKind.PoemNote => (Theme.Grotesk, Math.Round(f * 0.74), false, Theme.Secondary),
            LineKind.Verse => (Theme.Serif, Math.Round(f * 1.1), false, Theme.BodyText),
            LineKind.Fence or LineKind.Rule or LineKind.Image or LineKind.CalloutHeader => (new FontFamily("Consolas"), Math.Round(f * 0.8), false, Theme.Marker),
            _ => (Theme.Serif, f, false, Theme.BodyText),
        };
    }

    // MARK: Signes Markdown d'un paragraphe

    public record struct Span(int Start, int Length, SpanKind Kind, string? Extra = null);
    public enum SpanKind { Marker, Bold, Italic, BoldItalic, Code, Link, Wiki, Highlight, Comment }

    static readonly Regex BothRE = new(@"(?<![*\\])(\*\*\*|___)(?=[^\s*_])(.+?)(?<=[^\s*_\\])\1(?![*_])", RegexOptions.Compiled);
    static readonly Regex BoldRE = new(@"(?<!\\)(\*\*|__)(?=\S)(.+?)(?<=\S)(?<!\\)\1", RegexOptions.Compiled);
    static readonly Regex ItalicRE = new(@"(?<![*_\\\w])([*_])(?=[^\s*_])(.+?)(?<=[^\s*_\\])\1(?![*_\w])", RegexOptions.Compiled);
    static readonly Regex CodeRE = new(@"`[^`\n]+`", RegexOptions.Compiled);
    static readonly Regex WikiRE = new(@"(?<!!)\[\[([^\]\n|]+)(?:\|([^\]\n]+))?\]\]", RegexOptions.Compiled);
    static readonly Regex LinkRE = new(@"(?<!!)\[([^\]\n]+)\]\(([^)\n]+)\)", RegexOptions.Compiled);
    static readonly Regex CommentRE = new(@"%%.*?%%|<!--.*?-->", RegexOptions.Compiled);
    static readonly Regex HighlightRE = new(@"(?<!=)==(?=[^\s=])(.+?)(?<=[^\s=])==(?!=)", RegexOptions.Compiled);
    static readonly Regex MarkRE = new(@"<mark class=""([a-zA-Zéè-]+)"">(.+?)</mark>", RegexOptions.Compiled);
    static readonly Regex EscapeRE = new(@"\\([\\`*_{}\[\]()#+\-.!>])", RegexOptions.Compiled);
    static readonly Regex QuoteMarkerRE = new(@"^>[ \t]?", RegexOptions.Compiled);

    /// <summary>Signes et mises en forme d'un paragraphe, positions relatives au début du paragraphe.</summary>
    public List<Span> Spans(string s, LineKind kind)
    {
        var spans = new List<Span>();
        void Marker(int start, int length) { if (length > 0) spans.Add(new Span(start, length, SpanKind.Marker)); }
        switch (kind)
        {
            case LineKind.Heading or LineKind.PoemTitle: if (HeadingRE.Match(s) is { Success: true } h) Marker(0, h.Length); break;
            case LineKind.Quote or LineKind.Intro: if (QuoteMarkerRE.Match(s) is { Success: true } q) Marker(0, q.Length); break;
            case LineKind.Verse: if (Poem.Marker(s) is { } v) spans.Add(new Span(v.Start, v.Length, SpanKind.Marker, "verset")); break;
            case LineKind.CalloutHeader or LineKind.Image or LineKind.Fence or LineKind.Rule or LineKind.PoemOrnament: return spans;
        }
        // Gras italique d'abord ; ses signes sont ensuite neutralisés pour le gras et l'italique.
        var plain = s.ToCharArray();
        foreach (Match m in BothRE.Matches(s))
        {
            Marker(m.Index, 3); Marker(m.Index + m.Length - 3, 3);
            spans.Add(new Span(m.Index + 3, m.Length - 6, SpanKind.BoldItalic));
            for (int k = 0; k < 3; k++) { plain[m.Index + k] = ' '; plain[m.Index + m.Length - 1 - k] = ' '; }
        }
        var p = new string(plain);
        foreach (Match m in BoldRE.Matches(p))
        {
            int len = m.Groups[1].Length;
            Marker(m.Index, len); Marker(m.Index + m.Length - len, len);
            spans.Add(new Span(m.Index + len, m.Length - 2 * len, SpanKind.Bold));
        }
        foreach (Match m in ItalicRE.Matches(p))
        {
            Marker(m.Index, 1); Marker(m.Index + m.Length - 1, 1);
            spans.Add(new Span(m.Index + 1, m.Length - 2, SpanKind.Italic));
        }
        foreach (Match m in CodeRE.Matches(s)) spans.Add(new Span(m.Index, m.Length, SpanKind.Code));
        foreach (Match m in WikiRE.Matches(s))
        {
            spans.Add(new Span(m.Index, m.Length, SpanKind.Wiki));
            Marker(m.Index, 2); Marker(m.Index + m.Length - 2, 2);
            if (m.Groups[2].Success) Marker(m.Groups[1].Index, m.Groups[2].Index - m.Groups[1].Index);
        }
        foreach (Match m in LinkRE.Matches(s))
        {
            spans.Add(new Span(m.Groups[1].Index, m.Groups[1].Length, SpanKind.Link));
            Marker(m.Index, 1);
            int tail = m.Groups[1].Index + m.Groups[1].Length;
            Marker(tail, m.Index + m.Length - tail);
        }
        foreach (Match m in CommentRE.Matches(s)) spans.Add(new Span(m.Index, m.Length, SpanKind.Comment));
        foreach (Match m in HighlightRE.Matches(s))
        {
            spans.Add(new Span(m.Groups[1].Index, m.Groups[1].Length, SpanKind.Highlight));
            Marker(m.Index, 2); Marker(m.Index + m.Length - 2, 2);
        }
        foreach (Match m in MarkRE.Matches(s))
        {
            spans.Add(new Span(m.Groups[2].Index, m.Groups[2].Length, SpanKind.Highlight, m.Groups[1].Value));
            Marker(m.Index, m.Groups[2].Index - m.Index);
            int close = m.Groups[2].Index + m.Groups[2].Length;
            Marker(close, m.Index + m.Length - close);
        }
        foreach (Match m in EscapeRE.Matches(s)) Marker(m.Index, 1);
        return spans;
    }

    readonly Dictionary<string, ImageSource> images = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Image « ![[nom]] » à côté du document (chargée une fois).</summary>
    public ImageSource? Image(string line)
    {
        var m = ImageRE.Match(line);
        if (!m.Success || DocumentFolder == null) return null;
        var name = m.Groups[1].Value.Split('|')[0].Trim();
        var path = System.IO.Path.Combine(DocumentFolder, name);
        if (images.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            images[path] = image;
            return image;
        }
        catch { return null; }
    }

    // MARK: Mise en forme

    sealed class Colorizer : DocumentColorizingTransformer
    {
        readonly MarkdownLook look;
        public Colorizer(MarkdownLook look) { this.look = look; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            var kind = look.KindOf(line.LineNumber);
            var (family, size, bold, color) = look.Style(kind, look.HeadingLevel(line.LineNumber));
            int start = line.Offset, end = line.EndOffset;
            ChangeLinePart(start, end, e =>
            {
                e.TextRunProperties.SetTypeface(look.Face(family, bold));
                e.TextRunProperties.SetFontRenderingEmSize(size);
                e.TextRunProperties.SetForegroundBrush(color);
            });
            var text = CurrentContext.Document.GetText(line);
            foreach (var span in look.Spans(text, kind))
            {
                int a = start + span.Start, b = Math.Min(end, a + span.Length);
                if (b <= a) continue;
                switch (span.Kind)
                {
                    case SpanKind.Marker:
                        ChangeLinePart(a, b, e => e.TextRunProperties.SetForegroundBrush(Theme.Marker));
                        break;
                    case SpanKind.Bold or SpanKind.Italic or SpanKind.BoldItalic:
                        bool isBold = span.Kind != SpanKind.Italic, isItalic = span.Kind != SpanKind.Bold;
                        ChangeLinePart(a, b, e =>
                        {
                            var f = e.TextRunProperties.Typeface;
                            bool alreadyBold = f.Weight == FontWeights.Bold, alreadyItalic = f.Style == FontStyles.Italic;
                            e.TextRunProperties.SetTypeface(new Typeface(f.FontFamily, isItalic || alreadyItalic ? FontStyles.Italic : FontStyles.Normal,
                                isBold || alreadyBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal));
                        });
                        break;
                    case SpanKind.Code:
                        ChangeLinePart(a, b, e => { e.TextRunProperties.SetTypeface(new Typeface("Consolas")); e.TextRunProperties.SetFontRenderingEmSize(Math.Round(size * 0.85)); e.TextRunProperties.SetForegroundBrush(Theme.Secondary); });
                        break;
                    case SpanKind.Link or SpanKind.Wiki:
                        ChangeLinePart(a, b, e => e.TextRunProperties.SetForegroundBrush(Theme.Link));
                        if (span.Kind == SpanKind.Link) ChangeLinePart(a, b, e => e.TextRunProperties.SetTextDecorations(TextDecorations.Underline));
                        break;
                    case SpanKind.Highlight:
                        var brush = span.Extra switch
                        {
                            "vert" => new SolidColorBrush(Color.FromArgb(0x8C, 0x8C, 0xDB, 0x8C)),
                            "rose" => new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0x99, 0xB3)),
                            "bleu" => new SolidColorBrush(Color.FromArgb(0x8C, 0x8C, 0xC7, 0xFF)),
                            _ => Theme.Highlight,
                        };
                        ChangeLinePart(a, b, e => e.TextRunProperties.SetBackgroundBrush(brush));
                        break;
                    case SpanKind.Comment:
                        ChangeLinePart(a, b, e => e.TextRunProperties.SetForegroundBrush(Theme.Marker));
                        break;
                }
            }
        }
    }

    /// <summary>Remplace les signes Markdown par des éléments invisibles hors du paragraphe du curseur ; centre l'en-tête
    /// d'un poème ; remplace les lignes d'image par l'image et l'ornement « --- » d'un poème par une petite vague.</summary>
    sealed class MarkerHider : VisualLineElementGenerator
    {
        readonly MarkdownLook look;
        readonly List<(int Start, int Length, Func<VisualLineElement> Make)> pending = new();
        public MarkerHider(MarkdownLook look) { this.look = look; }

        public override void StartGeneration(ITextRunConstructionContext context)
        {
            base.StartGeneration(context);
            pending.Clear();
            var line = context.VisualLine.FirstDocumentLine;
            if (line.Length == 0) return;
            var kind = look.KindOf(line.LineNumber);
            bool active = look.IsActive(line.LineNumber);
            var text = context.Document.GetText(line);
            int offset = line.Offset;
            double column = look.ColumnWidth;
            var (family, size, bold, _) = look.Style(kind, look.HeadingLevel(line.LineNumber));

            if (kind == LineKind.Image && !active && look.Image(text) is { } image)
            {
                // Taille en pixels de l'image (pas en pouces : une image à 300 ppp paraîtrait minuscule), limitée à la
                // colonne et à une hauteur d'écran, comme l'app macOS.
                double pw = image is BitmapSource b ? b.PixelWidth : image.Width, ph = image is BitmapSource b2 ? b2.PixelHeight : image.Height;
                double width = Math.Min(column, pw), height = ph * width / Math.Max(1, pw);
                double maxHeight = Math.Round(900 * look.FontSize / Theme.BaseFontSize);
                if (height > maxHeight) { width *= maxHeight / height; height = maxHeight; }
                pending.Add((offset, line.Length, () => new InlineObjectElement(line.Length, new Image { Source = image, Width = width, Height = height, Stretch = Stretch.Uniform, Margin = new Thickness(0, Math.Round(look.FontSize * 0.6), 0, Math.Round(look.FontSize * 0.9)) })));
                return;
            }
            if (kind == LineKind.PoemOrnament && !active)
            {
                pending.Add((offset, line.Length, () => new InlineObjectElement(line.Length, Wave(column, look.FontSize))));
                return;
            }
            if ((kind == LineKind.CalloutHeader) && !active)
            {
                pending.Add((offset, line.Length, () => new HiddenElement(line.Length)));
                return;
            }
            var spans = look.Spans(text, kind).Where(s => s.Kind == SpanKind.Marker && (!active || s.Extra == "verset")).OrderBy(s => s.Start).ToList();
            // En-tête d'un poème : bloc centré (un espace invisible de la bonne largeur avant le texte).
            if ((kind == LineKind.PoemTitle || kind == LineKind.PoemNote) && !active)
            {
                var visible = text;
                foreach (var s in spans.OrderByDescending(s => s.Start)) visible = visible.Remove(s.Start, s.Length);
                var measured = new FormattedText(visible, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, look.Face(family, bold), size, Brushes.Black,
                                                 VisualTreeHelper.GetDpi(look.Editor).PixelsPerDip).WidthIncludingTrailingWhitespace;
                double pad = Math.Max(0, (column - measured) / 2 - 2);
                if (spans.Count > 0 && spans[0].Start == 0)
                {
                    var first = spans[0];
                    spans.RemoveAt(0);
                    pending.Add((offset, first.Length, () => new InlineObjectElement(first.Length, new Border { Width = pad, Height = 1 })));
                }
                else if (pad > 0 && text.Length > 0)
                {
                    // Pas de signe en tête : le premier caractère est dessiné après l'espace.
                    var ch = text[..(char.IsHighSurrogate(text[0]) && text.Length > 1 ? 2 : 1)];
                    var face = look.Face(family, bold);
                    // Ligne de base du caractère : sans elle, l'élément s'aligne par son bas et la lettre remonte.
                    double baseline = new FormattedText(ch, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black,
                                                        VisualTreeHelper.GetDpi(look.Editor).PixelsPerDip).Baseline;
                    pending.Add((offset, ch.Length, () =>
                    {
                        var first = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Children = { new Border { Width = pad }, new TextBlock { Text = ch, FontFamily = family, FontSize = size, Foreground = Theme.Secondary, FontWeight = face.Weight } },
                        };
                        TextBlock.SetBaselineOffset(first, baseline);
                        return new InlineObjectElement(ch.Length, first);
                    }));
                    spans = spans.Where(s => s.Start >= ch.Length).ToList();
                }
            }
            // Citation : le signe « > » laisse place à un petit retrait, à côté du filet. L'intro n'a pas de filet : son
            // signe est simplement masqué, sans retrait sur la première ligne seule.
            foreach (var s in spans)
            {
                int a = offset + s.Start, len = s.Length;
                if (kind == LineKind.Quote && s.Start == 0)
                {
                    double indent = Math.Round(look.FontSize * 0.9);
                    pending.Add((a, len, () => new InlineObjectElement(len, new Border { Width = indent, Height = 1 })));
                }
                else pending.Add((a, len, () => new HiddenElement(len)));
            }
            pending.Sort((x, y) => x.Start.CompareTo(y.Start));
            // Pas de chevauchement : on garde le premier.
            for (int i = pending.Count - 1; i > 0; i--)
                if (pending[i].Start < pending[i - 1].Start + pending[i - 1].Length) pending.RemoveAt(i);
        }

        public override int GetFirstInterestedOffset(int startOffset)
        {
            foreach (var p in pending) if (p.Start >= startOffset) return p.Start;
            return -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            foreach (var p in pending) if (p.Start == offset) return p.Make();
            return null;
        }

        static FrameworkElement Wave(double column, double fontSize)
        {
            double scale = fontSize / 19, wavelength = 12 * scale, amplitude = 2.2 * scale;
            var geometry = new StreamGeometry();
            double total = wavelength / 2 * 6, x = (column - total) / 2, y = fontSize;
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x, y), false, false);
                for (int i = 0; i < 6; i++)
                {
                    double step = wavelength / 2, peak = i % 2 == 0 ? -amplitude : amplitude;
                    ctx.BezierTo(new Point(x + step * 0.3642, y + peak * 1.3), new Point(x + step * 0.6358, y + peak * 1.3), new Point(x + step, y), true, true);
                    x += step;
                }
            }
            return new System.Windows.Shapes.Path { Data = geometry, Stroke = Theme.Wave, StrokeThickness = 1.3, Width = column, Height = fontSize * 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        }
    }

    /// <summary>Texte présent dans le document mais invisible (signe Markdown masqué).</summary>
    sealed class HiddenElement : VisualLineElement
    {
        public HiddenElement(int documentLength) : base(1, documentLength) { }
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new TextHidden(1);
    }

    /// <summary>Filet vertical à gauche des citations (comme l'app macOS).</summary>
    sealed class QuoteRuleRenderer : IBackgroundRenderer
    {
        readonly MarkdownLook look;
        public QuoteRuleRenderer(MarkdownLook look) { this.look = look; }
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            foreach (var vl in view.VisualLines)
            {
                var kind = look.KindOf(vl.FirstDocumentLine.LineNumber);
                if (kind != LineKind.Quote) continue;
                double top = vl.VisualTop - view.VerticalOffset + 3, height = vl.Height - 6;
                if (height <= 0) continue;
                dc.DrawRoundedRectangle(Theme.QuoteRule, null, new Rect(1, top, 2.5, height), 1.25, 1.25);
            }
        }
    }

    /// <summary>Marge de gauche : filet lavande du paragraphe du curseur, et numéros de versets d'un poème.</summary>
    sealed class Gutter : AbstractMargin
    {
        readonly MarkdownLook look;
        public Gutter(MarkdownLook look) { this.look = look; }

        protected override Size MeasureOverride(Size availableSize) =>
            new(look.PoemMode ? Math.Round(look.FontSize * 3.0) : Math.Round(look.FontSize * 1.4), 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView != null) { oldTextView.VisualLinesChanged -= Redraw; oldTextView.ScrollOffsetChanged -= Redraw; }
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) { newTextView.VisualLinesChanged += Redraw; newTextView.ScrollOffsetChanged += Redraw; }
        }

        void Redraw(object? sender, EventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            var view = TextView;
            if (view == null || !view.VisualLinesValid) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var doc = look.Editor.Document;
            foreach (var vl in view.VisualLines)
            {
                var line = vl.FirstDocumentLine;
                double top = vl.VisualTop - view.VerticalOffset;
                bool active = look.IsActive(line.LineNumber) && line.Length > 0;
                if (active)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xB3, 0x7B, 0x5F, 0xBF)), null,
                        new Rect(ActualWidth - 7, top + 3, 3, Math.Max(0, vl.Height - 6)), 1.5, 1.5);
                if (look.PoemMode && look.KindOf(line.LineNumber) == LineKind.Verse && Poem.Marker(doc.GetText(line)) is { } marker)
                {
                    var label = new FormattedText(marker.Number.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        look.Face(Theme.Grotesk), Math.Round(look.FontSize * 0.7), active ? Theme.Accent : Theme.VerseNumber, dpi);
                    double baseline = vl.TextLines.Count > 0 ? vl.TextLines[0].Baseline : vl.Height * 0.75;
                    dc.DrawText(label, new Point(ActualWidth - 14 - label.Width, top + baseline - label.Baseline));
                }
            }
        }
    }
}
