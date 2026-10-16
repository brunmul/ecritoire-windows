using System.Text;
using Ecritoire.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ecritoire.Core.Tests;

/// <summary>
/// Compatibilité avec l'app macOS, sur des COPIES de classeurs réels : les résultats attendus sont produits par le code
/// Swift de l'app (programme de référence). ECRITOIRE_CORPUS = dossier des copies, ECRITOIRE_ATTENDU = résultats Swift.
/// Sans ces variables, ces tests ne vérifient rien (ils ne s'exécutent que sur la machine de développement).
/// </summary>
public class CompatTests
{
    readonly ITestOutputHelper output;
    public CompatTests(ITestOutputHelper output) { this.output = output; }

    static string? Corpus => Environment.GetEnvironmentVariable("ECRITOIRE_CORPUS");
    static string? Expected => Environment.GetEnvironmentVariable("ECRITOIRE_ATTENDU");
    static readonly UTF8Encoding Utf8 = new(false);

    static IEnumerable<string> Binders() =>
        Corpus == null ? Array.Empty<string>() : Directory.GetDirectories(Corpus).Select(Path.GetFileName)!;

    [Fact]
    public void ArbreIdentiqueAMacOS()
    {
        foreach (var binder in Binders())
        {
            var sb = new StringBuilder();
            void Walk(BinderNode n, int depth)
            {
                foreach (var c in n.Children)
                {
                    sb.Append(new string(' ', depth * 2)).Append(c.IsFolder ? "[D] " : "")
                      .Append(Path.GetFileName(c.Path).Normalize(NormalizationForm.FormC)).Append(c.Icon != null ? $" {{{c.Icon}}}" : "").Append('\n');
                    Walk(c, depth + 1);
                }
            }
            Walk(Binder.LoadTree(Path.Combine(Corpus!, binder)), 0);
            var expected = File.ReadAllText(Path.Combine(Expected!, binder, "arbre.txt"), Utf8);
            var actual = sb.ToString();
            if (actual != expected)
            {
                var a = actual.Split('\n'); var e = expected.Split('\n');
                int i = 0; while (i < Math.Min(a.Length, e.Length) && a[i] == e[i]) i++;
                output.WriteLine($"{binder} : ligne {i + 1}\n  Windows : {a.ElementAtOrDefault(i)}\n  macOS   : {e.ElementAtOrDefault(i)}");
            }
            Assert.Equal(expected, actual);
            output.WriteLine($"{binder} : arbre identique ({expected.Count(c => c == '\n')} éléments)");
        }
    }

    [Fact]
    public void DocumentsEnregistresOctetParOctetCommeMacOS()
    {
        foreach (var binder in Binders())
        {
            var root = Path.Combine(Corpus!, binder);
            var exp = Path.Combine(Expected!, binder);
            int count = 0;
            foreach (var line in File.ReadAllLines(Path.Combine(exp, "index.txt"), Utf8))
            {
                var parts = line.Split('\t');
                var path = FindFile(root, parts[1]);
                var file = DocumentFile.Load(path);
                // Sans modification : même texte que macOS, et rien n'est écrit.
                Assert.Equal(File.ReadAllText(Path.Combine(exp, parts[0] + ".inchange"), Utf8), file.TextToWrite());
                // Corps modifié : même texte que macOS.
                file.Body += "\nAjout depuis Windows.";
                Assert.Equal(File.ReadAllText(Path.Combine(exp, parts[0] + ".modifie"), Utf8), file.TextToWrite());
                // En-tête : mêmes valeurs lues.
                var doc = MarkdownDocument.Parse(DocumentFile.ReadText(path), Path.GetFileNameWithoutExtension(path));
                Assert.Equal(File.ReadAllText(Path.Combine(exp, parts[0] + ".entete"), Utf8),
                    $"{doc.Title}\n{doc.Synopsis}\n{doc.Status}\n{(doc.Publish ? "true" : "false")}\n{string.Join("\n", doc.ExtraLines)}\n");
                // Versets : mêmes positions (UTF-16) que macOS.
                var verses = Poem.Verses(doc.Body).Select(v => $"{v.Number}\t{v.Start}\t{v.Length}\t{(v.Marker is { } m ? $"{m.Start},{m.Length}" : "-")}");
                Assert.Equal(File.ReadAllText(Path.Combine(exp, parts[0] + ".versets"), Utf8),
                    $"{(Poem.IsPoem(doc.Body) ? "true" : "false")}\t{Poem.HeaderEnd(doc.Body)}\n{string.Join("\n", verses)}\n");
                count++;
            }
            output.WriteLine($"{binder} : {count} documents identiques à macOS");
            Assert.True(count > 0);
        }
    }

    /// <summary>Chemin réel d'un document (les noms écrits sur macOS peuvent être en Unicode décomposé).</summary>
    static string FindFile(string root, string relative)
    {
        var direct = Path.Combine(root, relative);
        if (File.Exists(direct)) return direct;
        var want = relative.Normalize(NormalizationForm.FormC);
        return Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .First(f => Path.GetRelativePath(root, f).Replace('\\', '/').Normalize(NormalizationForm.FormC) == want);
    }
}
