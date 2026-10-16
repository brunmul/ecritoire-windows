using System.Text;
using System.Text.RegularExpressions;

namespace Ecritoire.Core;

/// <summary>
/// Export d'un document ou d'un dossier (Export.swift) : un dossier donne un seul texte, ses documents dans l'ordre du
/// classeur, chacun précédé de son titre en intertitre (un niveau de plus par profondeur), les titres internes décalés
/// d'autant. Les fichiers sources ne sont jamais modifiés.
/// </summary>
public static class Export
{
    public sealed record Section(string Title, int Level, string Body, bool Poem, bool IsFolder, string Source);

    static readonly char[] Newlines = { '\n', '\r', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029' };

    public static List<Section> Sections(string path, string? root)
    {
        var output = new List<Section>();
        Collect(path, 0, root, output);
        return output;
    }

    static void Collect(string path, int depth, string? root, List<Section> output)
    {
        int level = Math.Min(depth + 1, 6);
        if (Directory.Exists(path))
        {
            output.Add(new Section(System.IO.Path.GetFileName(path.TrimEnd('/', '\\')), level, "", false, true, path));
            foreach (var child in Binder.OrderedEntries(path))
                if (Directory.Exists(child) || string.Equals(System.IO.Path.GetExtension(child), ".md", StringComparison.OrdinalIgnoreCase))
                    Collect(child, depth + 1, root, output);
            return;
        }
        string text;
        try { text = DocumentFile.ReadText(path); } catch (IOException) { return; }
        output.Add(SectionOf(text, path, level, root));
    }

    public static Section SectionOf(string text, string path, int level, string? root)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        bool hasFrontmatter = text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal);
        var doc = MarkdownDocument.Parse(text, stem);
        var title = doc.Title.Trim(' ', '\t');
        var body = doc.Body;
        if ((!hasFrontmatter || title.Length == 0) && FirstHeading(body) is { } first) title = first;
        if (title.Length == 0) title = stem;
        if (TitleMatch.DuplicatedTitle(body, title) is { } dup) body = body.Remove(dup.Start, dup.Length);
        body = body.Trim(Newlines);
        bool poem = Core.Poem.IsPoem(body);
        if (root != null)
        {
            if (Sidecar.DisplayFor(path, root) is { } chosen) poem = chosen == Sidecar.Display.Poeme;
            else if (TitleMatch.Normalize(System.IO.Path.GetFileName(root.TrimEnd('/', '\\'))).StartsWith("poeme", StringComparison.Ordinal)) poem = true;
        }
        return new Section(title, level, body, poem, false, path);
    }

    internal static string? FirstHeading(string body)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = line.Trim(' ', '\t');
            if (t.Length == 0) continue;
            if (!t.StartsWith('#')) return null;
            var title = t.TrimStart('#').Trim(' ', '\t');
            return title.Length == 0 ? null : title;
        }
        return null;
    }

    // MARK: Texte de chaque section (commun au Markdown et au PDF)

    public static string TextOf(Section section)
    {
        var heading = new string('#', section.Level) + " " + section.Title;
        if (section.IsFolder) return heading;
        var body = RemoveComments(section.Body);
        body = RemoveHighlights(body);
        body = ShiftHeadings(body, section.Level - 1);
        return body.Length == 0 ? heading : heading + "\n\n" + body;
    }

    static readonly Regex CommentRE = new(@"%%[\s\S]*?%%|<!--[\s\S]*?-->", RegexOptions.Compiled);
    static readonly Regex HighlightRE = new(@"(?<!=)==(?=[^\s=])(.+?)(?<=[^\s=])==(?!=)", RegexOptions.Compiled);
    static readonly Regex MarkRE = new(@"<mark class=""[a-zA-Zéè-]+"">(.+?)</mark>", RegexOptions.Compiled);
    static readonly Regex HeadingRE = new(@"^(#{1,6})(?=[ \t])", RegexOptions.Compiled | RegexOptions.Multiline);
    static readonly Regex ImageRE = new(@"!\[\[([^\]\n|]+)(?:\|[^\]\n]*)?\]\]", RegexOptions.Compiled);
    static readonly Regex WikiRE = new(@"(?<!!)\[\[([^\]\n|]+)(?:\|([^\]\n]+))?\]\]", RegexOptions.Compiled);
    static readonly Regex VerseRE = new(@"^\[\d{1,4}\](?!\()[ \t]?", RegexOptions.Compiled | RegexOptions.Multiline);

    public static string RemoveComments(string text)
    {
        var output = CommentRE.Replace(text, "");
        return Regex.Replace(output, @"\n[ \t]*\n(?:[ \t]*\n)+", "\n\n").Trim(Newlines);
    }

    public static string RemoveHighlights(string text) => MarkRE.Replace(HighlightRE.Replace(text, m => m.Groups[1].Value), m => m.Groups[1].Value);

    public static string ShiftHeadings(string text, int delta) =>
        delta <= 0 ? text : HeadingRE.Replace(text, m => new string('#', Math.Min(6, m.Groups[1].Length + delta)));

    public static string RemoveVerseNumbers(string text) => VerseRE.Replace(text, "");

    // MARK: Markdown

    public sealed record ImageCopy(string Source, string Name);

    public static (string Text, List<ImageCopy> Images) Markdown(List<Section> sections)
    {
        var images = new List<ImageCopy>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var section in sections)
        {
            var text = TextOf(section);
            if (section.Poem) text = RemoveVerseNumbers(text);
            var folder = section.IsFolder ? section.Source : System.IO.Path.GetDirectoryName(section.Source)!;
            text = ImageRE.Replace(text, m =>
            {
                var name = m.Groups[1].Value.Trim(' ', '\t');
                var source = System.IO.Path.Combine(folder, name);
                var alt = System.IO.Path.ChangeExtension(name, null) ?? name;
                if (System.IO.Path.HasExtension(name)) alt = name[..name.LastIndexOf('.')];
                if (!File.Exists(source)) return $"![{alt}]({Encode(name)})";
                var existing = images.FirstOrDefault(i => Binder.SamePath(i.Source, source));
                if (existing != null) return $"![{alt}]({Encode(existing.Name)})";
                var unique = UniqueName(name, used);
                images.Add(new ImageCopy(source, unique));
                return $"![{alt}]({Encode(unique)})";
            });
            text = WikiRE.Replace(text, m => m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[1].Value);
            parts.Add(text);
        }
        return (string.Join("\n\n", parts) + "\n", images);
    }

    /// <summary>Écrit le Markdown et copie les images à côté ; renvoie les fichiers écrits.</summary>
    public static List<string> WriteMarkdown(List<Section> sections, string destination)
    {
        var (text, images) = Markdown(sections);
        File.WriteAllText(destination, text, new UTF8Encoding(false));
        var written = new List<string> { destination };
        var folder = System.IO.Path.GetDirectoryName(destination)!;
        foreach (var image in images)
        {
            var target = System.IO.Path.Combine(folder, image.Name);
            File.Copy(image.Source, target, overwrite: true);
            written.Add(target);
        }
        return written;
    }

    /// <summary>Encodage d'URL comme urlPathAllowed de Foundation, parenthèses comprises.</summary>
    internal static string Encode(string name)
    {
        const string allowed = "!$&'*+,-./:=@_~";
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            char c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || allowed.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    internal static string UniqueName(string name, HashSet<string> used)
    {
        int dot = name.LastIndexOf('.');
        bool hasExt = dot > 0 && dot < name.Length - 1 && !name[(dot + 1)..].Contains('/');
        string baseName = hasExt ? name[..dot] : name, ext = hasExt ? name[(dot + 1)..] : "";
        var candidate = name;
        int i = 2;
        while (used.Contains(candidate.ToLowerInvariant()))
        {
            candidate = ext.Length == 0 ? $"{baseName} {i}" : $"{baseName} {i}.{ext}";
            i++;
        }
        used.Add(candidate.ToLowerInvariant());
        return candidate;
    }
}
