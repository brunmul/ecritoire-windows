using Ecritoire.Core;
using Xunit;

namespace Ecritoire.Core.Tests;

/// <summary>Ordre numéroté du classeur : documents d'abord, puis dossiers, rangés par glisser-déposer dans .order.</summary>
public class OrdreTests
{
    static string Folder(out DirectoryInfo temp)
    {
        temp = Directory.CreateTempSubdirectory("ecritoire-ordre-");
        var root = temp.FullName;
        foreach (var name in new[] { "A.md", "B.md", "C.md" }) File.WriteAllText(Path.Combine(root, name), "");
        Directory.CreateDirectory(Path.Combine(root, "Partie"));
        Directory.CreateDirectory(Path.Combine(root, "Annexes"));
        // Ordre écrit sur Mac : un dossier entre deux documents.
        File.WriteAllText(Path.Combine(root, Binder.OrderFileName), "B.md\nPartie\nA.md\nC.md\nAnnexes\n");
        return root;
    }

    static List<string> Names(string folder) => Binder.Arranged(folder).Select(p => Path.GetFileName(p)!).ToList();

    [Fact]
    public void DocumentsAvantDossiers()
    {
        var root = Folder(out var temp);
        try { Assert.Equal(new[] { "B.md", "A.md", "C.md", "Partie", "Annexes" }, Names(root)); }
        finally { temp.Delete(true); }
    }

    [Fact]
    public void DeposerAvantLePremierLeRendPremier()
    {
        var root = Folder(out var temp);
        try
        {
            BinderOperations.Place(Path.Combine(root, "C.md"), root, 0);
            Assert.Equal(new[] { "C.md", "B.md", "A.md", "Partie", "Annexes" }, Names(root));
            // .order est réécrit dans l'ordre affiché : l'app Mac le lit tel quel.
            Assert.Equal("C.md\nB.md\nA.md\nPartie\nAnnexes\n", File.ReadAllText(Path.Combine(root, Binder.OrderFileName)));
            BinderOperations.Place(Path.Combine(root, "C.md"), root, 2);
            Assert.Equal(new[] { "B.md", "A.md", "C.md", "Partie", "Annexes" }, Names(root));
        }
        finally { temp.Delete(true); }
    }

    [Fact]
    public void PositionRameneeDansSaPartie()
    {
        var root = Folder(out var temp);
        try
        {
            // Un document déposé parmi les dossiers reste après le dernier document.
            BinderOperations.Place(Path.Combine(root, "B.md"), root, 4);
            Assert.Equal(new[] { "A.md", "C.md", "B.md", "Partie", "Annexes" }, Names(root));
            // Un dossier déposé en tête reste après les documents.
            BinderOperations.Place(Path.Combine(root, "Annexes"), root, 0);
            Assert.Equal(new[] { "A.md", "C.md", "B.md", "Annexes", "Partie" }, Names(root));
        }
        finally { temp.Delete(true); }
    }

    [Fact]
    public void DeplacerDansUnAutreDossier()
    {
        var root = Folder(out var temp);
        try
        {
            var partie = Path.Combine(root, "Partie");
            File.WriteAllText(Path.Combine(partie, "X.md"), "");
            var moved = BinderOperations.Place(Path.Combine(root, "A.md"), partie, 0, root);
            Assert.Equal(Path.Combine(partie, "A.md"), moved);
            Assert.Equal(new[] { "A.md", "X.md" }, Names(partie));
            Assert.Equal(new[] { "B.md", "C.md", "Partie", "Annexes" }, Names(root));
            Assert.Throws<BinderException>(() => BinderOperations.Place(partie, partie, 0, root));
        }
        finally { temp.Delete(true); }
    }
}
