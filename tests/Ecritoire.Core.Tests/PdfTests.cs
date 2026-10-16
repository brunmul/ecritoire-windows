using System.Text;
using Ecritoire.Core;
using Ecritoire.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace Ecritoire.Core.Tests;

/// <summary>Export PDF : mise en forme dans un paragraphe, et rendu complet des exemples (pages en PNG dans
/// ECRITOIRE_PDF_OUT pour les comparer à l'app macOS ; polices dans ECRITOIRE_POLICES, séparées par « : »).</summary>
public class PdfTests
{
    readonly ITestOutputHelper output;
    public PdfTests(ITestOutputHelper output) { this.output = output; }

    [Fact]
    public void SignesMarkdownRetires()
    {
        var runs = Inline.Runs(@"Un **gras** et *italique* puis [[Cible|lien]], [texte](http://x) et \*étoile\*.");
        Assert.Equal("Un gras et italique puis lien, texte et *étoile*.", string.Concat(runs.Select(r => r.Text)));
        Assert.Contains(runs, r => r.Text == "gras" && r.Bold && !r.Italic);
        Assert.Contains(runs, r => r.Text == "italique" && r.Italic);
        Assert.Contains(runs, r => r.Text == "lien" && r.Link);
    }

    [Fact]
    public void ExportPdfDesExemples()
    {
        var corpus = Environment.GetEnvironmentVariable("ECRITOIRE_CORPUS");
        var outDir = Environment.GetEnvironmentVariable("ECRITOIRE_PDF_OUT");
        var fonts = Environment.GetEnvironmentVariable("ECRITOIRE_POLICES");
        if (corpus == null || outDir == null) return;
        PdfExport.RegisterFonts(name =>
        {
            foreach (var dir in (fonts ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
                if (File.Exists(Path.Combine(dir, name))) return File.OpenRead(Path.Combine(dir, name));
            return null;
        });
        output.WriteLine(PdfExport.FontSummary);
        var items = new[] { "Poèmes/poeme-et-nous-louions-la-puissance.md", "Exemple", "Cahier international 2/02. 🌀 Le dialectique" };
        for (int i = 0; i < items.Length; i++)
        {
            var source = Path.Combine(corpus, items[i]);
            var root = Path.Combine(corpus, items[i].Split('/')[0]);
            var sections = Export.Sections(source, root);
            var pdf = PdfExport.Render(sections, "Essai");
            File.WriteAllBytes(Path.Combine(outDir, $"{i}.pdf"), pdf);
            int n = 0;
            foreach (var png in PdfExport.RenderImages(sections, "Essai", 80).Take(4)) File.WriteAllBytes(Path.Combine(outDir, $"{i}-{++n}.png"), png);
            output.WriteLine($"{items[i]} : {pdf.Length} octets");
            Assert.True(pdf.Length > 1000);
        }
    }
}
