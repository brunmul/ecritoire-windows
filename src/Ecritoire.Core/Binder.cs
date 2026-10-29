using System.Text;

namespace Ecritoire.Core;

/// <summary>Un élément du classeur : un dossier ou un document .md.</summary>
public sealed class BinderNode
{
    public string Path { get; }
    public bool IsFolder { get; }
    public List<BinderNode> Children { get; } = new();
    /// <summary>Icône choisie (identifiant du catalogue de l'app), null = icône par défaut.</summary>
    public string? Icon { get; internal set; }
    public BinderNode? Parent { get; internal set; }

    internal BinderNode(string path, bool isFolder) { Path = path; IsFolder = isFolder; }

    /// <summary>Nom affiché : le nom du fichier sans l'extension .md.</summary>
    public string DisplayName => IsFolder ? System.IO.Path.GetFileName(Path) : System.IO.Path.GetFileNameWithoutExtension(Path);

    public IEnumerable<BinderNode> AllDescendants => Children.SelectMany(c => new[] { c }.Concat(c.AllDescendants));

    public BinderNode? Find(string target)
    {
        if (Binder.SamePath(Path, target)) return this;
        foreach (var c in Children) if (c.Find(target) is { } hit) return hit;
        return null;
    }
}

/// <summary>
/// Lecture d'un dossier « classeur », au même format que l'app macOS (Binder.swift) : l'ordre manuel d'un dossier est
/// dans un fichier texte .order (un nom par ligne), les icônes dans .icones (« nom TAB icône »). Les éléments absents de
/// .order viennent à la fin, triés par nom. Les fichiers et dossiers cachés (commençant par « . ») sont ignorés.
/// </summary>
public static class Binder
{
    public const string OrderFileName = ".order";
    public const string IconsFileName = ".icones";
    public const string DocumentExtension = ".md";
    static readonly UTF8Encoding Utf8 = new(false);

    /// <param name="arranged">true : documents avant dossiers (<see cref="Arranged"/>) ; false : ordre de .order, comme sur Mac.</param>
    public static BinderNode LoadTree(string root, bool arranged = false)
    {
        var node = new BinderNode(root, true);
        Fill(node, arranged);
        return node;
    }

    static void Fill(BinderNode folder, bool arranged)
    {
        var icons = ReadIcons(folder.Path);
        foreach (var path in arranged ? Arranged(folder.Path) : OrderedEntries(folder.Path))
        {
            var child = new BinderNode(path, Directory.Exists(path)) { Parent = folder };
            child.Icon = icons.GetValueOrDefault(System.IO.Path.GetFileName(path));
            if (child.IsFolder) Fill(child, arranged);
            folder.Children.Add(child);
        }
    }

    /// <summary>Paquets macOS (projets Scrivener, apps) : vus comme des fichiers, jamais ouverts comme dossiers.</summary>
    static bool IsPackage(string dir) =>
        new[] { ".scriv", ".app", ".bundle", ".pages", ".rtfd" }.Contains(System.IO.Path.GetExtension(dir).ToLowerInvariant());

    /// <summary>Dossiers et fichiers .md visibles d'un dossier, dans l'ordre manuel.</summary>
    public static List<string> OrderedEntries(string folder)
    {
        var visible = new List<string>();
        try
        {
            foreach (var e in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (e.Name.StartsWith('.') || (e.Attributes & FileAttributes.Hidden) != 0 && e.Name.StartsWith('.')) continue;
                if (e is DirectoryInfo d) { if (!IsPackage(d.FullName)) visible.Add(d.FullName); }
                else if (string.Equals(e.Extension, DocumentExtension, StringComparison.OrdinalIgnoreCase)) visible.Add(e.FullName);
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in visible) byName.TryAdd(NFC(System.IO.Path.GetFileName(v)), v);
        var result = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ReadOrder(folder))
        {
            var key = NFC(name);
            if (!used.Contains(key) && byName.TryGetValue(key, out var path)) { result.Add(path); used.Add(key); }
        }
        var rest = visible.Where(v => !used.Contains(NFC(System.IO.Path.GetFileName(v)))).ToList();
        rest.Sort((a, b) => Text.NaturalCompare(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
        result.AddRange(rest);
        return result;
    }

    /// <summary>Ordre affiché par l'app Windows : les documents d'abord, puis les dossiers, chacun dans l'ordre de .order.
    /// Les numéros affichés dans l'arbre sont les positions dans cette liste.</summary>
    public static List<string> Arranged(string folder)
    {
        var entries = OrderedEntries(folder);
        var folders = entries.Where(Directory.Exists).ToList();
        return entries.Except(folders).Concat(folders).ToList();
    }

    /// <summary>Les noms écrits sur macOS peuvent être décomposés (NFD) : on compare en forme composée.</summary>
    static string NFC(string s) => s.Normalize(NormalizationForm.FormC);

    public static List<string> ReadOrder(string folder)
    {
        var file = System.IO.Path.Combine(folder, OrderFileName);
        if (!File.Exists(file)) return new();
        try
        {
            return File.ReadAllText(file, Utf8).Split('\n', '\r', '\u2028', '\u2029', '\u0085')
                .Select(l => l.Trim(' ', '\t')).Where(l => l.Length > 0).ToList();
        }
        catch (IOException) { return new(); }
    }

    public static Dictionary<string, string> ReadIcons(string folder)
    {
        var file = System.IO.Path.Combine(folder, IconsFileName);
        var output = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(file)) return output;
        try
        {
            foreach (var line in File.ReadAllText(file, Utf8).Split('\n', '\r'))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    output[parts[0]] = parts[1];
                    output[NFC(parts[0])] = parts[1];
                }
            }
        }
        catch (IOException) { }
        return output;
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\').Normalize(NormalizationForm.FormC),
                      System.IO.Path.GetFullPath(b).TrimEnd('/', '\\').Normalize(NormalizationForm.FormC),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
