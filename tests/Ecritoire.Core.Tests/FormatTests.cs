using Ecritoire.Core;
using Xunit;

namespace Ecritoire.Core.Tests;

public class FormatTests
{
    [Fact]
    public void EnteteLectureEtReecriture()
    {
        var text = "---\ntitle: \"Chapitre \\\"un\\\"\"\nsynopsis: L'arrivée au village\nstatus: en cours\npublish: true\ntags: [roman, brouillon]\naliases:\n  - Ch1\n---\n\n# Le village\n\nIl pleuvait.";
        var doc = MarkdownDocument.Parse(text);
        Assert.Equal("Chapitre \"un\"", doc.Title);
        Assert.Equal("L'arrivée au village", doc.Synopsis);
        Assert.Equal("en cours", doc.Status);
        Assert.True(doc.Publish);
        Assert.Equal(new[] { "tags: [roman, brouillon]", "aliases:", "  - Ch1" }, doc.ExtraLines);
        Assert.Equal("# Le village\n\nIl pleuvait.", doc.Body);
        var again = MarkdownDocument.Parse(doc.Serialized());
        Assert.Equal(doc.Title, again.Title);
        Assert.Equal(doc.Body, again.Body);
        Assert.Equal(doc.ExtraLines, again.ExtraLines);
    }

    [Fact]
    public void DocumentSansEntete()
    {
        var doc = MarkdownDocument.Parse("Juste du texte.\n", "Note");
        Assert.Equal("Note", doc.Title);
        Assert.Equal("Juste du texte.\n", doc.Body);
        Assert.StartsWith("---\ntitle: \"Note\"\n", doc.Serialized());
    }

    [Fact]
    public void FinsDeLigneWindowsRameneesALF()
    {
        var doc = MarkdownDocument.Parse("---\r\ntitle: \"A\"\r\n---\r\n\r\nTexte\r\nsuite");
        Assert.Equal("A", doc.Title);
        Assert.Equal("Texte\nsuite", doc.Body);
    }

    [Fact]
    public void CompteurDeMots()
    {
        Assert.Equal(0, WordCounter.Count(""));
        Assert.Equal(6, WordCounter.Count("L'arbre — haut de 12 mètres.\n\n# Titre"));
    }

    [Fact]
    public void PoemeVersetsReferencesRenumerotation()
    {
        var header = "# Titre\n\nRévision 1. Note qui cite [3] sans être un verset.\n\n---\n\n";
        var text = header + "[1] Demain, dès l’aube, à l’heure où blanchit la campagne,\n\n[2] Je partirai. Vois-tu, je sais que tu m’attends.\n\n"
            + "[3] J’irai par la forêt, j’irai par la montagne.\n\n[4] Je ne puis demeurer loin de toi plus longtemps.\n";
        Assert.True(Poem.IsPoem(text));
        Assert.False(Poem.IsPoem("Un paragraphe.\n\n# Titre\n"));
        Assert.False(Poem.IsPoem("[1](https://example.org) Note.\n\n[2](https://example.org) Autre note.\n"));
        var verses = Poem.Verses(text);
        Assert.Equal(4, verses.Count);
        Assert.Equal("Demain, dès l’aube, à l’heure où blanchit la campagne,", verses[0].Text);
        Assert.Equal(text, Poem.Renumbered(text));
        int v2 = text.IndexOf("Je partirai", StringComparison.Ordinal);
        Assert.Equal("v. 2", Poem.Reference(v2, 10, text));
        int to = text.IndexOf("j’irai par la montagne", StringComparison.Ordinal) + "j’irai par la montagne".Length;
        Assert.Equal("v. 2–3", Poem.Reference(v2, to - v2, text));
        Assert.Null(Poem.Reference(3, 4, text));
        var poem = header + "[1] Un.\n\n[2] Deux.\n\n[3] Trois.\n\n[4] Quatre.\n";
        Assert.Equal(header + "[1] Un.\n\n[2] Nouveau.\n\n[3] Deux.\n\n[4] Trois.\n\n[5] Quatre.\n",
                     Poem.Renumbered(poem.Replace("[1] Un.\n", "[1] Un.\nNouveau.\n")));
        Assert.Equal(header + "[1] Un.\n\n[2] Deux. Trois.\n\n[3] Quatre.\n", Poem.Renumbered(poem.Replace("Deux.\n\n[3] ", "Deux.[3] ")));
        Assert.Equal(poem, Poem.Renumbered(poem.Replace("[3] Trois.", "[9] Trois.")));
        Assert.Equal(header.Length - 1, Poem.HeaderEnd(poem));
    }

    [Fact]
    public void EnregistrementCommeMacOS()
    {
        var dir = Directory.CreateTempSubdirectory("ecritoire-");
        try
        {
            var path = Path.Combine(dir.FullName, "Poème.md");
            var poem = "# Titre\n\n---\n\n[1] Un.\n\n[2] Deux.\n";
            File.WriteAllText(path, poem);
            var file = DocumentFile.Load(path);
            Assert.False(file.Save()); // rien n'a changé : rien n'est écrit
            file.Body += "\n[3] Trois.";
            Assert.True(file.Save());
            Assert.Equal(poem + "\n[3] Trois.", File.ReadAllText(path)); // poème sans en-tête : texte seul
            var note = Path.Combine(dir.FullName, "Note.md");
            File.WriteAllText(note, "---\ntitle: \"Note\"\nsynopsis: \"\"\nstatus: \"brouillon\"\npublish: false\n---\n\nTexte.");
            var n = DocumentFile.Load(note);
            n.Body = "Texte\r\nmodifié.";
            n.Save();
            var bytes = File.ReadAllBytes(note);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF); // UTF-8 sans BOM
            Assert.Equal("---\ntitle: \"Note\"\nsynopsis: \"\"\nstatus: \"brouillon\"\npublish: false\n---\n\nTexte\nmodifié.", File.ReadAllText(note));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void RechercheNormalisationTermesContexte()
    {
        Assert.Equal("ecole", SearchFold.Query("École"));
        Assert.Equal("oeuvre", SearchFold.Query("Œuvre"));
        Assert.Equal("l'homme", SearchFold.Query("l’homme"));
        Assert.Equal("forme marchandise", SearchFold.Query("forme marchandise"));
        var dir = Directory.CreateTempSubdirectory("ecritoire-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "Partie"));
            File.WriteAllText(Path.Combine(dir.FullName, "Partie", "Méthode.md"),
                "---\ntitle: \"La méthode\"\n---\n\n# Le cœur\n\nÉlise lit l’œuvre de Hegel. La **forme** marchandise apparaît ici.\n");
            File.WriteAllText(Path.Combine(dir.FullName, "Poème.md"),
                "# Titre\n\n---\n\n[1] Demain, dès l’aube, à l’heure où blanchit la campagne,\n\n[2] J’irai par la forêt, j’irai par la montagne.\n");
            var index = new SearchIndex(dir.FullName);
            index.Build();
            Assert.Equal(2, index.DocumentCount);
            var r = index.Search(new SearchQuery("\"forme marchandise\""));
            Assert.Single(r.Documents);
            var hit = r.Documents[0].Hits[0];
            Assert.Equal("forme** marchandise", hit.Match.Length > 0 ? File.ReadAllText(Path.Combine(dir.FullName, "Partie", "Méthode.md")).Substring(
                File.ReadAllText(Path.Combine(dir.FullName, "Partie", "Méthode.md")).IndexOf("forme**", StringComparison.Ordinal), 19) : "");
            Assert.Equal("Le cœur", hit.Heading);
            Assert.Empty(index.Search(new SearchQuery("\"marchandise forme\"")).Documents);
            Assert.Single(index.Search(new SearchQuery("marchandise forme")).Documents);
            Assert.Single(index.Search(new SearchQuery("oeuvre")).Documents);   // œ = oe
            Assert.Single(index.Search(new SearchQuery("ELISE")).Documents);    // casse et accents
            Assert.Single(index.Search(new SearchQuery("march")).Documents);    // début de mot
            Assert.Empty(index.Search(new SearchQuery("archandise")).Documents); // pas en milieu de mot
            var poem = index.Search(new SearchQuery("montagne")).Documents[0].Hits[0];
            Assert.Equal("v. 2", poem.Verse);
            Assert.Single(index.Search(new SearchQuery("méthode")).Documents.Where(d => d.TitleMatches));
        }
        finally { dir.Delete(true); }
    }
}
