using System.Text;
using System.Text.RegularExpressions;

namespace Ecritoire.Core;

/// <summary>Statuts proposés dans l'inspecteur. La valeur est écrite telle quelle dans l'en-tête YAML.</summary>
public static class DocumentStatus
{
    public const string Idee = "idée", Brouillon = "brouillon", EnCours = "en cours", Relu = "relu", Termine = "terminé";
    public static readonly string[] All = { Idee, Brouillon, EnCours, Relu, Termine };
}

/// <summary>
/// Un document Markdown découpé en en-tête YAML + corps, exactement comme l'app macOS (MarkdownDocument.swift) :
/// clés reconnues title, synopsis, status, publish ; les autres lignes d'en-tête sont gardées telles quelles, dans
/// leur ordre. Le texte écrit est identique octet par octet à celui de l'app macOS.
/// </summary>
public sealed record MarkdownDocument
{
    public string Title { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public string Status { get; set; } = DocumentStatus.Brouillon;
    public bool Publish { get; set; }
    public string Body { get; set; } = "";
    /// <summary>Lignes d'en-tête non reconnues (clé + valeur brute, ou ligne de continuation).</summary>
    public List<string> ExtraLines { get; set; } = new();

    public static MarkdownDocument Parse(string text, string defaultTitle = "")
    {
        var doc = new MarkdownDocument { Title = defaultTitle };
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) { doc.Body = normalized; return doc; }
        var lines = normalized.Split('\n');
        int end = -1;
        for (int i = 1; i < lines.Length; i++) if (lines[i] == "---" || lines[i] == "...") { end = i; break; }
        if (end < 0) { doc.Body = normalized; return doc; }
        bool skippingContinuation = false;
        for (int i = 1; i < end; i++)
        {
            var line = lines[i];
            if (SplitKeyValue(line) is var (key, value))
            {
                skippingContinuation = false;
                switch (key)
                {
                    case "title": doc.Title = Unquote(value); break;
                    case "synopsis": doc.Synopsis = Unquote(value); break;
                    case "status": doc.Status = Unquote(value); break;
                    case "publish":
                        doc.Publish = new[] { "true", "yes", "oui", "1" }.Contains(Unquote(value).ToLowerInvariant()); break;
                    default: doc.ExtraLines.Add(line); continue;
                }
                skippingContinuation = value.Length == 0;
            }
            else if (skippingContinuation) continue; // valeur multiligne d'une clé reconnue : non gérée
            else doc.ExtraLines.Add(line);
        }
        var body = string.Join("\n", lines, end + 1, lines.Length - end - 1);
        if (body.StartsWith('\n')) body = body[1..];
        doc.Body = body;
        return doc;
    }

    /// <summary>Découpe « clé: valeur » pour une clé de premier niveau (sans indentation).</summary>
    internal static (string Key, string Value)? SplitKeyValue(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '-' || line[0] == '#') return null;
        int colon = line.IndexOf(':');
        if (colon < 0) return null;
        var key = line[..colon].Trim(' ', '\t');
        if (key.Length == 0 || key.Contains(' ')) return null;
        return (key, line[(colon + 1)..].Trim(' ', '\t'));
    }

    internal static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var sb = new StringBuilder();
            bool escaping = false;
            foreach (var ch in value[1..^1])
            {
                if (escaping) { sb.Append(ch switch { 'n' => '\n', 't' => '\t', _ => ch }); escaping = false; }
                else if (ch == '\\') escaping = true;
                else sb.Append(ch);
            }
            return sb.ToString();
        }
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
        int hash = value.IndexOf(" #", StringComparison.Ordinal); // commentaire YAML en fin de ligne
        return hash >= 0 ? value[..hash] : value;
    }

    public string Serialized()
    {
        var sb = new StringBuilder("---\n");
        sb.Append("title: ").Append(Quote(Title)).Append('\n');
        sb.Append("synopsis: ").Append(Quote(Synopsis)).Append('\n');
        sb.Append("status: ").Append(Quote(Status)).Append('\n');
        sb.Append("publish: ").Append(Publish ? "true" : "false").Append('\n');
        foreach (var line in ExtraLines) sb.Append(line).Append('\n');
        sb.Append("---\n\n").Append(Body);
        return sb.ToString();
    }

    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";
}

public static class WordCounter
{
    /// <summary>Mots : suites séparées par des blancs contenant au moins une lettre ou un chiffre (« l'arbre » = 1, « — » = 0).</summary>
    public static int Count(string text)
    {
        int count = 0; bool inWord = false, hasAlnum = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { if (inWord && hasAlnum) count++; inWord = false; hasAlnum = false; }
            else { inWord = true; if (Rune.IsLetterOrDigit(rune)) hasAlnum = true; }
        }
        if (inWord && hasAlnum) count++;
        return count;
    }
}

public static class TitleMatch
{
    /// <summary>Forme comparable d'un titre : sans accents, casse, ponctuation ni numérotation de tête.</summary>
    public static string Normalize(string title)
    {
        var s = Text.RemoveDiacritics(title).ToLowerInvariant();
        s = Regex.Replace(s, @"^[\d.\s]+", "");
        s = Regex.Replace(s, @"[^\p{L}\p{N}]+", " ");
        return s.Trim(' ');
    }

    /// <summary>Si le texte commence par un titre identique au titre du document, sa plage (paragraphe à masquer).</summary>
    public static (int Start, int Length)? DuplicatedTitle(string body, string title)
    {
        int location = 0;
        while (location < body.Length)
        {
            var (start, length) = Text.ParagraphRange(body, location);
            var line = body.Substring(start, length).Trim();
            if (line.Length == 0) { location = start + length; continue; }
            if (!line.StartsWith('#')) return null;
            var t = line.TrimStart('#').Trim(' ', '\t');
            var wanted = Normalize(title);
            return wanted.Length > 0 && Normalize(t) == wanted ? (start, length) : null;
        }
        return null;
    }
}
