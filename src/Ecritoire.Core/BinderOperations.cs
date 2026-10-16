using System.Text;

namespace Ecritoire.Core;

public sealed class BinderException : Exception
{
    public BinderException(string message) : base(message) { }
}

/// <summary>
/// Opérations sur le classeur, identiques à l'app macOS (Binder.swift) : création, renommage, déplacement, corbeille,
/// icônes. Les fichiers .order (un nom par ligne, terminé par un retour à la ligne) et .icones (« nom TAB icône »,
/// triés par nom) sont écrits exactement comme sur Mac ; commentaires et instantanés suivent les documents.
/// </summary>
public static class BinderOperations
{
    static readonly UTF8Encoding Utf8 = new(false);

    public static void WriteOrder(IEnumerable<string> names, string folder) =>
        DocumentFile.WriteAtomic(Path.Combine(folder, Binder.OrderFileName), string.Join("\n", names) + "\n");

    /// <summary>Insère <paramref name="name"/> dans l'ordre du dossier à la position <paramref name="index"/> (null : à la fin).</summary>
    public static void InsertInOrder(string name, string folder, int? index)
    {
        var names = Binder.OrderedEntries(folder).Select(Path.GetFileName).Where(n => n != name).ToList()!;
        int position = Math.Clamp(index ?? names.Count, 0, names.Count);
        names.Insert(position, name);
        WriteOrder(names!, folder);
    }

    public static void RemoveFromOrder(string name, string folder)
    {
        var names = Binder.ReadOrder(folder);
        if (!names.Contains(name)) return;
        WriteOrder(names.Where(n => n != name), folder);
    }

    static void ReplaceInOrder(string old, string @new, string folder)
    {
        var names = Binder.ReadOrder(folder);
        int i = names.IndexOf(old);
        if (i < 0) return;
        names[i] = @new;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        WriteOrder(names.Where(n => seen.Add(n)), folder);
    }

    // MARK: Création

    public static string UniquePath(string folder, string baseName, string? extension)
    {
        string Make(string n) => Path.Combine(folder, extension == null ? n : n + extension);
        var candidate = Make(baseName);
        int i = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate)) candidate = Make($"{baseName} {i++}");
        return candidate;
    }

    public static string CreateDocument(string folder, string baseName = "Sans titre", int? index = null)
    {
        var path = UniquePath(folder, baseName, Binder.DocumentExtension);
        var doc = new MarkdownDocument { Title = Path.GetFileNameWithoutExtension(path) };
        DocumentFile.WriteAtomic(path, doc.Serialized());
        InsertInOrder(Path.GetFileName(path), folder, index);
        return path;
    }

    public static string CreateFolder(string folder, string baseName = "Nouveau dossier", int? index = null)
    {
        var path = UniquePath(folder, baseName, null);
        Directory.CreateDirectory(path);
        InsertInOrder(Path.GetFileName(path), folder, index);
        return path;
    }

    // MARK: Renommage, déplacement, corbeille

    /// <summary>Caractères interdits dans un nom : ceux de macOS (« / », « : ») et ceux de Windows.</summary>
    static readonly char[] Forbidden = { '/', ':', '\\', '*', '?', '"', '<', '>', '|' };

    /// <summary>Renomme un élément ; pour un document, « .md » est ajouté. Renvoie le nouveau chemin.</summary>
    public static string Rename(string path, string newName, string? root = null)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('.') || trimmed.IndexOfAny(Forbidden) >= 0)
            throw new BinderException("Ce nom n'est pas valide.");
        var folder = Path.GetDirectoryName(path)!;
        bool isDir = Directory.Exists(path);
        var target = Path.Combine(folder, trimmed);
        if (!isDir && !string.Equals(Path.GetExtension(target), Binder.DocumentExtension, StringComparison.OrdinalIgnoreCase))
            target += Binder.DocumentExtension;
        var oldName = Path.GetFileName(path);
        var newFileName = Path.GetFileName(target);
        if (newFileName == oldName) return path;
        bool caseOnly = string.Equals(newFileName, oldName, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target)))
            throw new BinderException($"« {newFileName} » existe déjà à cet endroit.");
        if (caseOnly)
        {
            // Volume insensible à la casse : on passe par un nom temporaire.
            var temp = Path.Combine(folder, ".renommage-" + Guid.NewGuid().ToString("D").ToUpperInvariant());
            Move(path, temp, isDir);
            Move(temp, target, isDir);
        }
        else Move(path, target, isDir);
        ReplaceInOrder(oldName, newFileName, folder);
        var icons = Binder.ReadIcons(folder);
        RemoveKey(icons, oldName, out var icon);
        if (icon != null) { icons[newFileName] = icon; WriteIcons(icons, folder); }
        if (root != null) Sidecar.Relocate(path, target, root);
        return target;
    }

    static void Move(string from, string to, bool isDir)
    {
        if (isDir) Directory.Move(from, to); else File.Move(from, to);
    }

    /// <summary>Déplace un élément dans <paramref name="destination"/> à la position <paramref name="index"/>
    /// (dans l'ordre affiché de la destination ; null : à la fin). Gère aussi le simple réordonnancement.</summary>
    public static string MoveItem(string path, string destination, int? index, string? root = null)
    {
        var source = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        if (index < 0) index = null;
        if (Binder.SamePath(source, destination))
        {
            var names = Binder.OrderedEntries(destination).Select(p => Path.GetFileName(p)!).ToList();
            int old = names.IndexOf(name);
            if (old < 0) return path;
            names.RemoveAt(old);
            int position = names.Count;
            if (index is { } i) position = i > old ? i - 1 : i;
            names.Insert(Math.Clamp(position, 0, names.Count), name);
            WriteOrder(names, destination);
            return path;
        }
        var dest = Path.GetFullPath(destination).TrimEnd('/', '\\');
        var src = Path.GetFullPath(path).TrimEnd('/', '\\');
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(dest, src, cmp) || dest.StartsWith(src + Path.DirectorySeparatorChar, cmp))
            throw new BinderException("Impossible de déplacer un dossier dans lui-même.");
        var target = Path.Combine(destination, name);
        if (File.Exists(target) || Directory.Exists(target)) throw new BinderException($"« {name} » existe déjà à cet endroit.");
        Move(path, target, Directory.Exists(path));
        RemoveFromOrder(name, source);
        InsertInOrder(name, destination, index);
        var sourceIcons = Binder.ReadIcons(source);
        RemoveKey(sourceIcons, name, out var icon);
        if (icon != null)
        {
            WriteIcons(sourceIcons, source);
            var destIcons = Binder.ReadIcons(destination);
            destIcons[name] = icon;
            WriteIcons(destIcons, destination);
        }
        if (root != null) Sidecar.Relocate(path, target, root);
        return target;
    }

    /// <summary>Après la mise à la corbeille d'un élément (par l'app) : il quitte l'ordre de son dossier.</summary>
    public static void Trashed(string path) => RemoveFromOrder(Path.GetFileName(path), Path.GetDirectoryName(path)!);

    // MARK: Icônes

    /// <summary>Retire une clé en tenant compte des deux formes Unicode (Binder.ReadIcons garde les deux).</summary>
    static void RemoveKey(Dictionary<string, string> icons, string name, out string? icon)
    {
        icon = null;
        foreach (var key in new[] { name, name.Normalize(NormalizationForm.FormC), name.Normalize(NormalizationForm.FormD) })
            if (icons.Remove(key, out var v)) icon ??= v;
    }

    /// <summary>Écrit .icones : une ligne « nom TAB icône » par élément, triées par nom (comme l'app macOS).</summary>
    public static void WriteIcons(Dictionary<string, string> icons, string folder)
    {
        var file = Path.Combine(folder, Binder.IconsFileName);
        // ReadIcons garde chaque nom sous ses formes composée et décomposée : une seule ligne par élément.
        var unique = new Dictionary<string, (string Name, string Icon)>(StringComparer.Ordinal);
        foreach (var (k, v) in icons) unique.TryAdd(k.Normalize(NormalizationForm.FormC), (k, v));
        if (unique.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
        var lines = unique.Values.OrderBy(e => e.Name, Text.ScalarComparer).Select(e => $"{e.Name}\t{e.Icon}");
        DocumentFile.WriteAtomic(file, string.Join("\n", lines) + "\n");
    }

    public static void SetIcon(string path, string? icon)
    {
        var folder = Path.GetDirectoryName(path)!;
        var icons = Binder.ReadIcons(folder);
        var name = Path.GetFileName(path);
        RemoveKey(icons, name, out _);
        if (icon != null) icons[name] = icon;
        WriteIcons(icons, folder);
    }
}
