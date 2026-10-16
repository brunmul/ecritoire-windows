using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ecritoire.Core;

/// <summary>Un commentaire posé sur un passage (Comments.swift), rangé hors du texte dans
/// .ecritoire/commentaires/&lt;chemin du document&gt;.json et ancré par le passage cité et son contexte.</summary>
public sealed class DocumentComment
{
    public sealed class Reply
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
        public DateTime Date { get; set; } = Comments.NowToTheSecond();
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Quote { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Date { get; set; } = Comments.NowToTheSecond();
    public bool Resolved { get; set; }
    public List<Reply> Replies { get; set; } = new();
    /// <summary>Date d'envoi à Claude : le commentaire passe alors en archive.</summary>
    public DateTime? Sent { get; set; }

    /// <summary>Archivé : envoyé à Claude ou résolu.</summary>
    public bool IsArchived => Resolved || Sent != null;
}

public static class Comments
{
    public const int ContextLength = 40;

    public static DateTime NowToTheSecond()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc);
    }

    /// <summary>Contexte avant/après un passage, pour l'ancrage.</summary>
    public static (string Prefix, string Suffix) Context(int start, int length, string text)
    {
        int a = Math.Max(0, start - ContextLength), b = Math.Min(text.Length, start + length + ContextLength);
        return (text[a..start], text[(start + length)..b]);
    }

    /// <summary>Retrouve le passage cité : parmi ses occurrences, celle dont le contexte ressemble le plus à celui enregistré.</summary>
    public static (int Start, int Length)? Locate(DocumentComment comment, string text, int? hint = null)
    {
        if (comment.Quote.Length == 0) return null;
        (int Start, int Length, int Score)? best = null;
        int from = 0, guard = 0;
        while (guard++ < 500 && from <= text.Length)
        {
            int found = text.IndexOf(comment.Quote, from, StringComparison.Ordinal);
            if (found < 0) break;
            var (p, s) = Context(found, comment.Quote.Length, text);
            int score = CommonSuffix(p, comment.Prefix) + CommonPrefix(s, comment.Suffix);
            if (hint is { } h) score -= Math.Min(Math.Abs(found - h) / 2000, 20);
            if (best == null || score > best.Value.Score) best = (found, comment.Quote.Length, score);
            int next = found + Math.Max(comment.Quote.Length, 1);
            if (next >= text.Length) break;
            from = next;
        }
        if (best is { } b) return (b.Start, b.Length);
        // Passage modifié : on se rabat sur le contexte, s'il est encore là.
        if (comment.Prefix.Length >= 12)
        {
            var tail = comment.Prefix.Length > 24 ? comment.Prefix[^24..] : comment.Prefix;
            int p = text.IndexOf(tail, StringComparison.Ordinal);
            if (p >= 0) return (p + tail.Length, 0);
        }
        return null;
    }

    static int CommonSuffix(string a, string b) { int n = 0; while (n < a.Length && n < b.Length && a[^(n + 1)] == b[^(n + 1)]) n++; return n; }
    static int CommonPrefix(string a, string b) { int n = 0; while (n < a.Length && n < b.Length && a[n] == b[n]) n++; return n; }

    // MARK: Fichier annexe (JSON identique à celui de l'app macOS)

    public static List<DocumentComment> Load(string document, string root)
    {
        var file = Sidecar.CommentsFile(document, root);
        if (file == null || !File.Exists(file)) return new();
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            var list = new List<DocumentComment>();
            foreach (var e in json.RootElement.EnumerateArray())
            {
                var c = new DocumentComment
                {
                    Id = Guid.Parse(e.GetProperty("id").GetString()!),
                    Quote = e.GetProperty("quote").GetString() ?? "",
                    Prefix = e.GetProperty("prefix").GetString() ?? "",
                    Suffix = e.GetProperty("suffix").GetString() ?? "",
                    Text = e.GetProperty("text").GetString() ?? "",
                    Date = ParseDate(e.GetProperty("date").GetString()!),
                    Resolved = e.TryGetProperty("resolved", out var r) && r.GetBoolean(),
                    Sent = e.TryGetProperty("sent", out var s) && s.ValueKind == JsonValueKind.String ? ParseDate(s.GetString()!) : null,
                };
                if (e.TryGetProperty("replies", out var replies))
                    foreach (var rep in replies.EnumerateArray())
                        c.Replies.Add(new DocumentComment.Reply { Id = Guid.Parse(rep.GetProperty("id").GetString()!), Text = rep.GetProperty("text").GetString() ?? "", Date = ParseDate(rep.GetProperty("date").GetString()!) });
                list.Add(c);
            }
            return list;
        }
        catch (Exception) { return new(); }
    }

    public static void Save(List<DocumentComment> comments, string document, string root)
    {
        var file = Sidecar.CommentsFile(document, root);
        if (file == null) return;
        if (comments.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        DocumentFile.WriteAtomic(file, Encode(comments));
    }

    static DateTime ParseDate(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>Même texte que JSONEncoder de Swift (prettyPrinted, sortedKeys, withoutEscapingSlashes, dates ISO 8601).</summary>
    public static string Encode(List<DocumentComment> comments)
    {
        var w = new SwiftJsonWriter();
        w.Array(comments.Count, i =>
        {
            var c = comments[i];
            var fields = new List<(string, Action)>
            {
                ("date", () => w.String(Iso(c.Date))),
                ("id", () => w.String(c.Id.ToString().ToUpperInvariant())),
                ("prefix", () => w.String(c.Prefix)),
                ("quote", () => w.String(c.Quote)),
                ("replies", () => w.Array(c.Replies.Count, j =>
                {
                    var r = c.Replies[j];
                    w.Object(new List<(string, Action)> { ("date", () => w.String(Iso(r.Date))), ("id", () => w.String(r.Id.ToString().ToUpperInvariant())), ("text", () => w.String(r.Text)) });
                })),
                ("resolved", () => w.Raw(c.Resolved ? "true" : "false")),
            };
            if (c.Sent is { } sent) fields.Add(("sent", () => w.String(Iso(sent))));
            fields.Add(("suffix", () => w.String(c.Suffix)));
            fields.Add(("text", () => w.String(c.Text)));
            w.Object(fields);
        });
        return w.ToString();
    }

    static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Écriture JSON au format de JSONEncoder (prettyPrinted) de Swift : indentation de 2 espaces, « "clé" : valeur ».</summary>
public sealed class SwiftJsonWriter
{
    readonly StringBuilder sb = new();
    int depth;

    void Indent() => sb.Append(' ', depth * 2);
    public void Raw(string s) => sb.Append(s);

    public void String(string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public void Array(int count, Action<int> item)
    {
        sb.Append('[').Append('\n');
        depth++;
        if (count == 0) { sb.Append('\n'); }
        for (int i = 0; i < count; i++)
        {
            Indent();
            item(i);
            sb.Append(i < count - 1 ? ",\n" : "\n");
        }
        depth--;
        Indent();
        sb.Append(']');
    }

    public void Object(List<(string Key, Action Value)> fields)
    {
        sb.Append('{').Append('\n');
        depth++;
        for (int i = 0; i < fields.Count; i++)
        {
            Indent();
            String(fields[i].Key);
            sb.Append(" : ");
            fields[i].Value();
            sb.Append(i < fields.Count - 1 ? ",\n" : "\n");
        }
        depth--;
        Indent();
        sb.Append('}');
    }

    public override string ToString() => sb.ToString();
}
