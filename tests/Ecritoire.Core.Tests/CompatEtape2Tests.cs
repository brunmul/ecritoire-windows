using System.Text;
using Ecritoire.Core;
using Xunit;
using Xunit.Abstractions;

namespace Ecritoire.Core.Tests;

/// <summary>
/// Étape 2 : commentaires (JSON), différences entre versions, export Markdown et opérations du classeur, comparés au
/// résultat du code Swift de l'app macOS (programme de référence) sur des copies. Variables : ECRITOIRE_CORPUS,
/// ECRITOIRE_REF2 (références Swift), ECRITOIRE_OPS_SWIFT (classeur d'exemple après les opérations faites en Swift).
/// </summary>
public class CompatEtape2Tests
{
    readonly ITestOutputHelper output;
    public CompatEtape2Tests(ITestOutputHelper output) { this.output = output; }

    static string? Corpus => Environment.GetEnvironmentVariable("ECRITOIRE_CORPUS");
    static string? Ref => Environment.GetEnvironmentVariable("ECRITOIRE_REF2");
    static string? OpsSwift => Environment.GetEnvironmentVariable("ECRITOIRE_OPS_SWIFT");
    static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");
    static DateTime At(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    [Fact]
    public void CommentairesJsonIdentiqueASwift()
    {
        if (Ref == null) return;
        var swift = Directory.EnumerateFiles(Path.Combine(Ref, "json", ".ecritoire"), "*.json", SearchOption.AllDirectories).Single();
        var expected = File.ReadAllText(swift, new UTF8Encoding(false));
        var c1 = new DocumentComment { Id = Id(1), Date = At(1_790_000_000), Quote = "le \"roseau\" pensant", Prefix = "L’homme n’est qu’un\n", Suffix = "\tpensant / la nature \\ fin", Text = "Garder ? 🌿 Oui.\nDeux lignes" };
        var c2 = new DocumentComment { Id = Id(2), Date = At(1_790_000_100), Quote = "dignité", Text = "Envoyé \u001F contrôle", Sent = At(1_790_000_200), Resolved = true };
        c2.Replies.Add(new DocumentComment.Reply { Id = Id(3), Date = At(1_790_000_150), Text = "Précision « ici »" });
        var actual = Comments.Encode(new() { c1, c2 });
        if (actual != expected) output.WriteLine("Windows :\n" + actual + "\nmacOS :\n" + expected);
        Assert.Equal(expected, actual);
        // Relu puis réécrit : identique.
        var root = Path.Combine(Ref, "json");
        var reloaded = Comments.Load(Path.Combine(root, "Dossier", "Doc é.md"), root);
        Assert.Equal(2, reloaded.Count);
        Assert.Equal(expected, Comments.Encode(reloaded));
    }

    [Fact]
    public void DifferencesIdentiquesASwift()
    {
        if (Ref == null) return;
        var dir = Path.Combine(Ref, "paires");
        var sb = new StringBuilder();
        int i = 0;
        for (; File.Exists(Path.Combine(dir, $"{i}.a")); i++)
        {
            var a = File.ReadAllText(Path.Combine(dir, $"{i}.a"), new UTF8Encoding(false));
            var b = File.ReadAllText(Path.Combine(dir, $"{i}.b"), new UTF8Encoding(false));
            var hunks = TextDiff.Hunks(a, b);
            foreach (var h in hunks) sb.Append($"{i}\t{h.BaseStart}\t{h.BaseLength}\t{h.CurrentStart}\t{h.CurrentLength}\n");
            // Accepter tout = version actuelle ; refuser tout = version de référence.
            var accepted = a;
            foreach (var h in hunks.AsEnumerable().Reverse()) accepted = TextDiff.Accept(h, accepted);
            Assert.Equal(b, accepted);
            var rejected = b;
            foreach (var h in hunks.AsEnumerable().Reverse()) rejected = TextDiff.Reject(h, rejected);
            Assert.Equal(a, rejected);
        }
        var expected = File.ReadAllText(Path.Combine(Ref, "diff-swift.txt"), new UTF8Encoding(false));
        var actual = sb.ToString();
        if (actual != expected)
        {
            var x = actual.Split('\n'); var y = expected.Split('\n');
            int k = 0; while (k < Math.Min(x.Length, y.Length) && x[k] == y[k]) k++;
            output.WriteLine($"ligne {k + 1} — Windows : {x.ElementAtOrDefault(k)} | macOS : {y.ElementAtOrDefault(k)}");
        }
        Assert.Equal(expected, actual);
        output.WriteLine($"{i} paires, différences identiques à macOS");
    }

    [Fact]
    public void ExportMarkdownIdentiqueASwift()
    {
        if (Ref == null || Corpus == null) return;
        foreach (var line in File.ReadAllLines(Path.Combine(Ref, "export", "index.txt"), new UTF8Encoding(false)))
        {
            var parts = line.Split('\t');
            var source = Path.Combine(Corpus, parts[1]);
            var root = Path.Combine(Corpus, parts[1].Split('/')[0]);
            var swiftDir = Path.Combine(Ref, "export", parts[0]);
            var temp = Directory.CreateTempSubdirectory("ecritoire-export-");
            try
            {
                Export.WriteMarkdown(Export.Sections(source, root), Path.Combine(temp.FullName, "export.md"));
                var expectedFiles = Directory.GetFiles(swiftDir).Select(f => Path.GetFileName(f).Normalize(NormalizationForm.FormC)).OrderBy(f => f, StringComparer.Ordinal).ToList();
                var actualFiles = Directory.GetFiles(temp.FullName).Select(f => Path.GetFileName(f).Normalize(NormalizationForm.FormC)).OrderBy(f => f, StringComparer.Ordinal).ToList();
                Assert.Equal(expectedFiles, actualFiles);
                var expected = File.ReadAllText(Path.Combine(swiftDir, "export.md"), new UTF8Encoding(false));
                var actual = File.ReadAllText(Path.Combine(temp.FullName, "export.md"), new UTF8Encoding(false));
                if (actual != expected)
                {
                    var x = actual.Split('\n'); var y = expected.Split('\n');
                    int k = 0; while (k < Math.Min(x.Length, y.Length) && x[k] == y[k]) k++;
                    output.WriteLine($"{parts[1]} ligne {k + 1}\n  Windows : {x.ElementAtOrDefault(k)}\n  macOS   : {y.ElementAtOrDefault(k)}");
                }
                Assert.Equal(expected, actual);
                output.WriteLine($"{parts[1]} : export identique ({actualFiles.Count} fichiers)");
            }
            finally { temp.Delete(true); }
        }
    }

    [Fact]
    public void OperationsDuClasseurIdentiquesASwift()
    {
        if (Corpus == null || OpsSwift == null) return;
        var temp = Directory.CreateTempSubdirectory("ecritoire-ops-");
        try
        {
            var root = Path.Combine(temp.FullName, "Exemple");
            CopyDirectory(Path.Combine(Corpus, "Exemple"), root);
            // Mêmes opérations, dans le même ordre, que le programme de référence Swift.
            var roman = Path.Combine(root, "Roman");
            var d1 = BinderOperations.CreateDocument(root, "Sans titre", 1);
            var f1 = BinderOperations.CreateFolder(root, "Partie test", 0);
            BinderOperations.CreateDocument(f1, "Chapitre", null);
            BinderOperations.CreateDocument(root, "Sans titre", null);
            var renamed = BinderOperations.Rename(d1, "Lettre à Élise");
            BinderOperations.SetIcon(renamed, "carnet-rouge");
            BinderOperations.SetIcon(f1, "etoile");
            var moved = BinderOperations.MoveItem(renamed, f1, 0);
            BinderOperations.MoveItem(Path.Combine(root, "À propos.md"), root, 0);
            var chap = Path.Combine(f1, "Chapitre.md");
            Comments.Save(new() { new DocumentComment { Id = Id(9), Date = At(1_790_000_000), Quote = "x", Text = "note" } }, chap, root);
            var f2 = BinderOperations.Rename(f1, "Partie un", root);
            BinderOperations.SetIcon(f2, null);
            var inRoman = BinderOperations.MoveItem(Path.Combine(f2, Path.GetFileName(moved)), roman, 1);
            BinderOperations.Rename(inRoman, "lettre à élise");

            var expected = Snapshot(OpsSwift);
            var actual = Snapshot(root);
            foreach (var key in expected.Keys.Union(actual.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                bool same = expected.TryGetValue(key, out var e) && actual.TryGetValue(key, out var a) && e.SequenceEqual(a);
                if (!same) output.WriteLine($"différence : {key}\n  macOS   : {(expected.TryGetValue(key, out var eb) ? Encoding.UTF8.GetString(eb).Replace("\n", "⏎").Replace("\t", "⇥") : "absent")}\n  Windows : {(actual.TryGetValue(key, out var ab) ? Encoding.UTF8.GetString(ab).Replace("\n", "⏎").Replace("\t", "⇥") : "absent")}");
            }
            Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (key, bytes) in expected) Assert.True(bytes.SequenceEqual(actual[key]), $"{key} diffère");
            output.WriteLine($"{expected.Count} fichiers identiques à macOS après les opérations");
        }
        finally { temp.Delete(true); }
    }

    /// <summary>Contenu de tous les fichiers d'un dossier (noms en forme composée, séparateur « / »).</summary>
    /// <remarks>Les noms écrits dans .order et .icones sont comparés en forme Unicode composée : Foundation (Swift) écrit
    /// en forme décomposée les noms passés par une URL de fichier, Windows en forme composée ; les deux apps
    /// comparent les noms à équivalence canonique près.</remarks>
    static Dictionary<string, byte[]> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != ".DS_Store")
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/').Normalize(NormalizationForm.FormC),
                          f => Path.GetFileName(f) is Binder.OrderFileName or Binder.IconsFileName
                              ? Encoding.UTF8.GetBytes(File.ReadAllText(f, Encoding.UTF8).Normalize(NormalizationForm.FormC))
                              : File.ReadAllBytes(f));

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
