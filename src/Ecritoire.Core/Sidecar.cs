using System.Text;
using System.Text.Json;

namespace Ecritoire.Core;

/// <summary>
/// Fichiers annexes d'un classeur, dans le dossier caché « .ecritoire » (commentaires, archives, instantanés, prompts,
/// affichage.json), au format de l'app macOS. Les chemins relatifs y sont écrits avec des « / », même sous Windows.
/// L'étape 1 de l'app Windows les lit sans jamais les modifier.
/// </summary>
public static class Sidecar
{
    public const string FolderName = ".ecritoire";
    public enum Display { Prose, Poeme }

    /// <summary>Chemin relatif au classeur, avec « / » (comme sur macOS), en forme Unicode composée.</summary>
    public static string? RelativePath(string document, string root)
    {
        var r = Path.GetFullPath(root).TrimEnd('/', '\\');
        var d = Path.GetFullPath(document);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!d.StartsWith(r + Path.DirectorySeparatorChar, cmp)) return null;
        return d[(r.Length + 1)..].Replace('\\', '/').Normalize(NormalizationForm.FormC);
    }

    public static string? CommentsFile(string document, string root) =>
        RelativePath(document, root) is { } rel ? Path.Combine(root, FolderName, "commentaires", rel.Replace('/', Path.DirectorySeparatorChar) + ".json") : null;

    public static string? SnapshotFile(string document, string root) =>
        RelativePath(document, root) is { } rel ? Path.Combine(root, FolderName, "instantanes", rel.Replace('/', Path.DirectorySeparatorChar)) : null;

    public static string? LoadSnapshot(string document, string root)
    {
        var file = SnapshotFile(document, root);
        return file != null && File.Exists(file) ? DocumentFile.ReadText(file) : null;
    }

    /// <summary>Enregistre (ou efface avec null) l'instantané d'un document : sa version de référence, en-tête compris.</summary>
    public static void SaveSnapshot(string? text, string document, string root)
    {
        var file = SnapshotFile(document, root);
        if (file == null) return;
        if (text == null) { if (File.Exists(file)) File.Delete(file); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        DocumentFile.WriteAtomic(file, text);
    }

    /// <summary>Un document ou un dossier a été renommé ou déplacé : ses commentaires et instantanés suivent.</summary>
    public static void Relocate(string oldPath, string newPath, string root)
    {
        if (RelativePath(oldPath, root) is not { } oldRel || RelativePath(newPath, root) is not { } newRel) return;
        var basePath = Path.Combine(root, FolderName);
        foreach (var kind in new[] { "commentaires", "instantanes" })
        {
            var dir = Path.Combine(basePath, kind);
            var pairs = new List<(string, string)> { (Path.Combine(dir, oldRel), Path.Combine(dir, newRel)) };
            if (kind == "commentaires") pairs.Add((Path.Combine(dir, oldRel + ".json"), Path.Combine(dir, newRel + ".json")));
            foreach (var (from, to) in pairs)
            {
                if (!File.Exists(from) && !Directory.Exists(from)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
                }
                catch (IOException) { }
            }
        }
    }

    /// <summary>Affichage choisi pour un document (« prose » ou « poeme »), null si aucun choix enregistré.</summary>
    public static Display? DisplayFor(string document, string root)
    {
        var rel = RelativePath(document, root);
        var file = Path.Combine(root, FolderName, "affichage.json");
        if (rel == null || !File.Exists(file)) return null;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file, new UTF8Encoding(false)));
            if (map == null) return null;
            foreach (var (key, value) in map)
                if (key.Normalize(NormalizationForm.FormC) == rel)
                    return value == "poeme" ? Display.Poeme : value == "prose" ? Display.Prose : null;
        }
        catch (JsonException) { } catch (IOException) { }
        return null;
    }

    /// <summary>Affichage par défaut (comme l'app macOS) : choix enregistré, sinon poème dans un classeur « Poèmes » ou pour un texte en versets.</summary>
    public static bool PoemDisplay(string document, string? root, string body)
    {
        if (root != null && DisplayFor(document, root) is { } chosen) return chosen == Display.Poeme;
        var folder = TitleMatch.Normalize(Path.GetFileName(root ?? "") ?? "");
        return Poem.IsPoem(body) || folder.StartsWith("poeme", StringComparison.Ordinal);
    }
}
