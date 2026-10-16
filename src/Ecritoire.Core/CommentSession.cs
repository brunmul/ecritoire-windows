using System.Globalization;
using System.Text;

namespace Ecritoire.Core;

/// <summary>
/// Commentaires du document ouvert (CommentStore.swift), sans interface : ancrage dans le texte, suivi des modifications,
/// numérotation, archives (envoyés à Claude ou résolus), enregistrement du fichier annexe, prompt pour Claude.
/// </summary>
public sealed class CommentSession
{
    public List<DocumentComment> All { get; private set; } = new();
    public Dictionary<Guid, (int Start, int Length)> Ranges { get; } = new();
    public string? Document { get; private set; }
    public string? Root { get; private set; }

    public void Load(string? document, string? root, string text)
    {
        Document = document; Root = root;
        All = document != null && root != null ? Comments.Load(document, root) : new();
        Ranges.Clear();
        Reanchor(text);
    }

    public void Relocate(string document) => Document = document;

    public void Reanchor(string text)
    {
        var fresh = new Dictionary<Guid, (int, int)>();
        foreach (var c in All)
            if (Comments.Locate(c, text, Ranges.TryGetValue(c.Id, out var r) ? r.Start : null) is { } found) fresh[c.Id] = found;
        Ranges.Clear();
        foreach (var (k, v) in fresh) Ranges[k] = v;
    }

    public IEnumerable<DocumentComment> Active => All.Where(c => !c.IsArchived);
    public int OpenCount => All.Count(c => !c.IsArchived);
    public int ArchivedCount => All.Count - OpenCount;

    int Location(Guid id) => Ranges.TryGetValue(id, out var r) ? r.Start : int.MaxValue;

    /// <summary>Commentaires affichés dans le volet (en cours, ou archives), dans l'ordre du texte.</summary>
    public List<DocumentComment> Visible(bool archive) =>
        All.Where(c => c.IsArchived == archive).OrderBy(c => Location(c.Id)).ThenBy(c => c.Date).ToList();

    /// <summary>Numéro affiché : ordre dans le texte parmi les commentaires du même groupe.</summary>
    public int Number(Guid id)
    {
        bool archived = All.FirstOrDefault(c => c.Id == id)?.IsArchived ?? false;
        var ordered = All.Where(c => c.IsArchived == archived).OrderBy(c => Location(c.Id)).ToList();
        return ordered.FindIndex(c => c.Id == id) + 1;
    }

    /// <summary>Met à jour les citations d'après les plages actuelles, puis écrit le fichier annexe.</summary>
    public void Persist(string text)
    {
        if (Document == null || Root == null) return;
        foreach (var c in All)
        {
            if (!Ranges.TryGetValue(c.Id, out var r) || r.Length <= 0 || r.Start + r.Length > text.Length) continue;
            c.Quote = text.Substring(r.Start, r.Length);
            (c.Prefix, c.Suffix) = Comments.Context(r.Start, r.Length, text);
        }
        Comments.Save(All, Document, Root);
    }

    /// <summary>Le texte a changé : <paramref name="offset"/>, longueur supprimée et insérée.</summary>
    public void TextEdited(int offset, int removed, int inserted)
    {
        if (Ranges.Count == 0) return;
        int delta = inserted - removed;
        (int, int) Shift((int Start, int Length) r)
        {
            int oldEnd = offset + removed, rEnd = r.Start + r.Length;
            if (oldEnd <= r.Start && !(removed == 0 && offset == r.Start && r.Length > 0)) return (r.Start + delta, r.Length);
            if (offset >= rEnd) return r;
            int start = r.Start <= offset ? r.Start : offset;
            int end = rEnd >= oldEnd ? rEnd + delta : offset + inserted;
            return (start, Math.Max(0, end - start));
        }
        foreach (var id in Ranges.Keys.ToList()) Ranges[id] = Shift(Ranges[id]);
    }

    public DocumentComment Add(int start, int length, string text, string body)
    {
        var (p, s) = Comments.Context(start, length, text);
        var c = new DocumentComment { Quote = text.Substring(start, length), Prefix = p, Suffix = s, Text = body.Trim() };
        All.Add(c);
        Ranges[c.Id] = (start, length);
        Persist(text);
        return c;
    }

    public void ToggleResolved(Guid id, string text) { if (Find(id) is { } c) { c.Resolved = !c.Resolved; Persist(text); } }

    public void Reply(Guid id, string reply, string text)
    {
        if (Find(id) is not { } c || reply.Trim().Length == 0) return;
        c.Replies.Add(new DocumentComment.Reply { Text = reply.Trim() });
        Persist(text);
    }

    public void Delete(Guid id, string text) { All.RemoveAll(c => c.Id == id); Ranges.Remove(id); Persist(text); }

    /// <summary>Sort un commentaire des archives.</summary>
    public void Restore(Guid id, string text) { if (Find(id) is { } c) { c.Sent = null; c.Resolved = false; Persist(text); } }

    /// <summary>Envoi à Claude : les commentaires en cours passent en archive.</summary>
    public void ArchiveSent(IEnumerable<Guid> ids, string text)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var c in All.Where(c => set.Contains(c.Id))) c.Sent = now;
        Persist(text);
    }

    DocumentComment? Find(Guid id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>Commentaire en cours sous une position du texte.</summary>
    public Guid? At(int location)
    {
        foreach (var (id, r) in Ranges)
            if (location >= r.Start && location < r.Start + r.Length && Find(id) is { IsArchived: false }) return id;
        return null;
    }

    // MARK: Prompt pour Claude (Review.swift : claudePrompt)

    /// <summary>Prompt complet : consignes, chemin, commentaires numérotés avec leur passage, texte intégral.</summary>
    public string ClaudePrompt(string title, string path, string body, string? fileText, bool poemNumbering)
    {
        var open = Active.OrderBy(c => Number(c.Id)).ToList();
        var sb = new StringBuilder();
        var shownTitle = title.Length == 0 ? Path.GetFileNameWithoutExtension(path) : title;
        sb.Append($"# Révision demandée — « {shownTitle} »\n\n");
        sb.Append("Tu travailles sur un document Markdown d'Écritoire, mon outil d'écriture :\n");
        sb.Append($"`{path}`\n\n");
        sb.Append("## Consignes\n");
        sb.Append("- Modifie directement ce fichier pour traiter chacun des commentaires numérotés ci-dessous.\n");
        sb.Append("- Ne change que ce que les commentaires demandent ; laisse tout le reste mot pour mot.\n");
        sb.Append("- Conserve l'en-tête YAML et la syntaxe : titres `#`, encadrés `> [!intro]`, citations `>`, surlignages `==…==` et `<mark class=\"…\">`, images `![[…]]`, liens `[[…]]`.\n");
        if (poemNumbering)
            sb.Append("- C'est un poème en versets : un verset = un paragraphe, précédé de sa position `[n]`. Garde ce format (une ligne vide entre deux versets) et renumérote si tu ajoutes, supprimes ou fusionnes des versets ; les références « v. n » désignent ces numéros.\n");
        sb.Append("- N'invente ni faits ni citations. Si un commentaire est ambigu, fais le choix le plus prudent et signale-le.\n");
        sb.Append("- Ne modifie pas le dossier caché `.ecritoire` (commentaires et instantanés).\n");
        sb.Append("- À la fin, résume en une ligne par commentaire ce que tu as changé.\n\n");
        sb.Append($"## Commentaires ({open.Count})\n"); // chaîne multiligne Swift : un seul retour avant les commentaires
        foreach (var c in open)
        {
            int n = Number(c.Id);
            string? reference = poemNumbering && Ranges.TryGetValue(c.Id, out var rr) ? Core.Poem.Reference(rr.Start, rr.Length, body) : null;
            sb.Append($"### {n}. {(reference != null ? reference + " — " : "")}« {c.Quote} »\n");
            if (Ranges.TryGetValue(c.Id, out var r) && r.Start + r.Length <= body.Length)
            {
                var (p, s) = Comments.Context(r.Start, r.Length, body);
                sb.Append($"Contexte : …{p.Replace("\n", " ")}**{c.Quote}**{s.Replace("\n", " ")}…\n");
            }
            else sb.Append("(Ce passage n'a pas été retrouvé dans la version actuelle.)\n");
            sb.Append($"Commentaire : {c.Text}\n");
            foreach (var reply in c.Replies) sb.Append($"Précision : {reply.Text}\n");
            sb.Append('\n');
        }
        sb.Append("## Texte intégral actuel\n\n````markdown\n");
        sb.Append(fileText ?? body);
        sb.Append("\n````\n");
        return sb.ToString();
    }

    /// <summary>Fichier où ranger le prompt : .ecritoire/prompts/&lt;document&gt; — aaaa-mm-jj HHhmm.md.</summary>
    public static string PromptFile(string document, string root, DateTime now) =>
        Path.Combine(root, Sidecar.FolderName, "prompts",
            $"{Path.GetFileNameWithoutExtension(document)} — {now.ToString("yyyy-MM-dd HH'h'mm", CultureInfo.InvariantCulture)}.md");
}
