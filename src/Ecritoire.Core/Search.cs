using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ecritoire.Core;

/// <summary>
/// Forme « repliée » d'un texte pour la recherche (Search.swift) : minuscules, sans accents, ligatures dépliées
/// (œ = oe), apostrophes et guillemets unifiés, espaces insécables = espace, blancs regroupés. En mode Markdown, la
/// syntaxe est ignorée (**, ==, [[ ]], balises, # et &gt; de début de ligne, numéros de versets, lignes d'image).
/// </summary>
public static class SearchFold
{
    static readonly ConcurrentDictionary<int, string> Cache = new();

    static string FoldScalar(int v)
    {
        switch (v)
        {
            case 0x2019: case 0x2018: case 0x02BC: case 0x0060: case 0x00B4: case 0x2032: return "'";
            case 0x201C: case 0x201D: case 0x00AB: case 0x00BB: case 0x201E: case 0x2033: return "\"";
            case 0x00A0: case 0x202F: case 0x2009: case 0x2007: case 0x2002: case 0x2003: case 0x200A: return " ";
            case 0x200B: case 0x00AD: case 0xFEFF: return "";
            case 0x0153: case 0x0152: return "oe";
            case 0x00E6: case 0x00C6: return "ae";
            case 0x00DF: return "ss";
            case 0x2010: case 0x2011: return "-";
        }
        return Cache.GetOrAdd(v, k => Text.RemoveDiacritics(char.ConvertFromUtf32(k)).ToLowerInvariant());
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c >= 0x80 && !char.IsWhiteSpace(c) && !char.IsPunctuation(c);

    /// <summary>Requête : repliée sans règles Markdown.</summary>
    public static string Query(string text)
    {
        var sb = new StringBuilder();
        Fold(text, false, sb, null, null);
        return sb.ToString().Trim(' ', '\n');
    }

    /// <summary>Replie <paramref name="text"/>. <paramref name="map"/> reçoit, pour chaque caractère produit, la position
    /// UTF-16 d'origine ; <paramref name="paragraphs"/> reçoit (position repliée, position d'origine) de chaque paragraphe.</summary>
    public static void Fold(string text, bool markdown, StringBuilder output, List<int>? map, List<(int Folded, int Original)>? paragraphs)
    {
        int i = 0;
        bool lineStart = true, lastSpace = true;
        paragraphs?.Add((output.Length, 0));
        void Emit(char c, int at) { output.Append(c); map?.Add(at); }
        int Next(int at) => at < text.Length && char.IsHighSurrogate(text[at]) && at + 1 < text.Length ? 2 : 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n' || c == '\r' || c == '\u2029')
            {
                if (output.Length > 0 && output[^1] != '\n')
                {
                    if (output[^1] == ' ') { output.Length--; if (map != null) map.RemoveAt(map.Count - 1); }
                    Emit('\n', i);
                }
                i++;
                lineStart = true; lastSpace = true;
                paragraphs?.Add((output.Length, i));
                continue;
            }
            if (markdown)
            {
                if (lineStart)
                {
                    int j = i;
                    while (j < text.Length && (text[j] == '#' || text[j] == '>' || text[j] == ' ')) j++;
                    if (j < text.Length && text[j] == '[')
                    {
                        int k = j + 1;
                        while (k < text.Length && text[k] >= '0' && text[k] <= '9') k++;
                        // « [n](…) » est un renvoi de note, pas un numéro de verset.
                        if (k > j + 1 && k < text.Length && text[k] == ']' && !(k + 1 < text.Length && text[k + 1] == '('))
                        {
                            j = k + 1;
                            while (j < text.Length && text[j] == ' ') j++;
                        }
                    }
                    bool callout = j + 1 < text.Length && text[j] == '[' && text[j + 1] == '!';
                    bool image = j + 2 < text.Length && text[j] == '!' && text[j + 1] == '[' && text[j + 2] == '[';
                    if (callout || image) while (j < text.Length && text[j] != '\n') j++;
                    i = j;
                    lineStart = false;
                    continue;
                }
                if (c == '*' || c == '_') { i++; continue; }
                if (c == '=' && ((i + 1 < text.Length && text[i + 1] == '=') || (i > 0 && text[i - 1] == '='))) { i++; continue; }
                if (c == '[' && i + 1 < text.Length && text[i + 1] == '[') { i += 2; continue; }
                if (c == ']' && i + 1 < text.Length && text[i + 1] == ']') { i += 2; continue; }
                if (c == '<' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
                {
                    int j = i + 1;
                    while (j < text.Length && text[j] != '>' && text[j] != '\n' && j - i < 80) j++;
                    if (j < text.Length && text[j] == '>') { i = j + 1; continue; }
                }
            }
            lineStart = false;
            if (c < 0x80)
            {
                if (c == '\t') c = ' ';
                if (c == ' ') { if (!lastSpace) Emit(' ', i); lastSpace = true; }
                else { Emit(char.ToLowerInvariant(c), i); lastSpace = false; }
                i++;
            }
            else
            {
                int n = Next(i);
                var folded = FoldScalar(char.ConvertToUtf32(text, i));
                if (folded == " ") { if (!lastSpace) Emit(' ', i); lastSpace = true; }
                else { foreach (var f in folded) Emit(f, i); if (folded.Length > 0) lastSpace = false; }
                i += n;
            }
        }
    }
}

/// <summary>Termes repliés : mots (préfixes de mots) et phrases exactes entre guillemets.</summary>
public sealed class SearchQuery
{
    public IReadOnlyList<string> Terms { get; }

    public SearchQuery(string text)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        bool inQuote = false;
        void Flush() { var f = SearchFold.Query(current.ToString()); if (f.Length > 0) terms.Add(f); current.Clear(); }
        foreach (var ch in text)
        {
            if (ch is '"' or '“' or '”' or '«' or '»') { Flush(); inQuote = !inQuote; }
            else if (char.IsWhiteSpace(ch) && !inQuote) Flush();
            else current.Append(ch);
        }
        Flush();
        Terms = terms.Count > 1 ? terms.Where(t => t.Length > 1).ToList() : terms;
    }

    public bool IsEmpty => Terms.Count == 0 || (Terms.Count == 1 && Terms[0].Length < 2);
}

/// <summary>Un document indexé : titre, intertitres, texte replié et table des paragraphes.</summary>
public sealed class IndexedDocument
{
    public string Path { get; }
    public string RelativePath { get; }
    public string Title { get; }
    internal string FoldedTitle { get; }
    /// <summary>Corps (sans en-tête YAML), tel que l'éditeur l'affiche : les positions des résultats s'y rapportent.</summary>
    public string Body { get; }
    internal string Folded { get; }
    internal List<(int Folded, int Original)> Paragraphs { get; } = new();
    public List<(int Location, int Level, string Title)> Headings { get; } = new();
    internal List<string> FoldedHeadings { get; }
    public bool IsPoem { get; }

    internal IndexedDocument(string path, string relativePath, string text)
    {
        Path = path; RelativePath = relativePath;
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        var doc = MarkdownDocument.Parse(text, fileName);
        string? firstHeading = null;
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            var first = text.Split('\n', 2)[0];
            if (first.StartsWith("# ", StringComparison.Ordinal)) firstHeading = first[2..].Trim(' ', '\t', '\r');
        }
        Title = firstHeading ?? (doc.Title.Length == 0 ? fileName : doc.Title);
        Body = doc.Body;
        FoldedTitle = SearchFold.Query(Title);
        var sb = new StringBuilder(Body.Length);
        SearchFold.Fold(Body, true, sb, null, Paragraphs);
        Folded = sb.ToString();
        bool inFence = false;
        int pos = 0;
        foreach (var raw in Body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0)
            {
                if (line.StartsWith("```") || line.StartsWith("~~~")) inFence = !inFence;
                else if (!inFence && line.StartsWith('#'))
                {
                    int level = line.TakeWhile(c => c == '#').Count();
                    if (level <= 6 && line.Length > level && line[level] == ' ')
                    {
                        var t = Regex.Replace(line[level..].Trim(' ', '\t'), @"\*\*|__|(?<!\\)\*|\[\[|\]\]|==", "");
                        Headings.Add((pos, level, t));
                    }
                }
            }
            pos += raw.Length + 1;
        }
        FoldedHeadings = Headings.Select(h => SearchFold.Query(h.Title)).ToList();
        IsPoem = Poem.IsPoem(Body);
    }

    internal int ParagraphIndexOfFolded(int offset)
    {
        int low = 0, high = Paragraphs.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (Paragraphs[mid].Folded <= offset) low = mid; else high = mid - 1;
        }
        return low;
    }
}

/// <summary>Index en mémoire d'un dossier (un classeur, ou tous les classeurs). Rien n'est écrit dans les fichiers.</summary>
public sealed class SearchIndex
{
    public string Root { get; }
    readonly object gate = new();
    Dictionary<string, IndexedDocument> documents = new();
    public bool IsReady { get; private set; }

    public SearchIndex(string root) { Root = System.IO.Path.GetFullPath(root).TrimEnd('/', '\\'); }

    public int DocumentCount { get { lock (gate) return documents.Count; } }

    /// <summary>Tous les documents .md du dossier (hors dossiers cachés et paquets).</summary>
    public static List<string> MarkdownFiles(string root)
    {
        var output = new List<string>();
        void Walk(string dir)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); } catch { return; }
            foreach (var e in entries)
            {
                if (e.Name.StartsWith('.')) continue;
                if (e is DirectoryInfo d)
                {
                    if (new[] { ".scriv", ".app", ".bundle" }.Contains(d.Extension.ToLowerInvariant())) continue;
                    Walk(d.FullName);
                }
                else if (string.Equals(e.Extension, ".md", StringComparison.OrdinalIgnoreCase)) output.Add(e.FullName);
            }
        }
        Walk(root);
        return output;
    }

    string Relative(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full[(Root.Length + 1)..].Replace('\\', '/') : System.IO.Path.GetFileName(path);
    }

    public void Build()
    {
        var files = MarkdownFiles(Root);
        var built = new IndexedDocument?[files.Count];
        Parallel.For(0, files.Count, i => built[i] = Load(files[i]));
        lock (gate)
        {
            documents = built.Where(d => d != null).ToDictionary(d => d!.RelativePath, d => d!);
            IsReady = true;
        }
    }

    IndexedDocument? Load(string path)
    {
        try { return new IndexedDocument(path, Relative(path), DocumentFile.ReadText(path)); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Mise à jour d'un document modifié, créé ou supprimé.</summary>
    public void Update(string path)
    {
        var key = Relative(path);
        var doc = File.Exists(path) ? Load(path) : null;
        lock (gate) { if (doc != null) documents[key] = doc; else documents.Remove(key); }
    }

    public sealed record Hit(string Id, int Start, int Length, string Before, string Match, string After, string? Heading, bool IsHeading, string? Verse);
    public sealed record DocumentResult(string Path, string RelativePath, string Title, bool TitleMatches, int Count, List<Hit> Hits);
    public sealed record Results(List<DocumentResult> Documents, int TotalHits, int SearchedDocuments, double Milliseconds);

    /// <summary>Au-delà, on affiche « 2 000+ ».</summary>
    public const int CountLimit = 2000;

    /// <summary>Documents dont le titre ou le texte contient tous les termes ; titres d'abord, puis intertitres, puis nombre d'occurrences.</summary>
    public Results Search(SearchQuery query, int maxHitsPerDocument = 8, int maxDocuments = 100, CancellationToken cancel = default)
    {
        var watch = Stopwatch.StartNew();
        List<IndexedDocument> docs;
        lock (gate) docs = documents.Values.ToList();
        if (query.IsEmpty) return new Results(new(), 0, docs.Count, 0);
        var terms = query.Terms;
        var matches = new (IndexedDocument Doc, bool Title, bool Heading, int Count)?[docs.Count];
        Parallel.For(0, docs.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var doc = docs[i];
            bool title = terms.All(t => Count(t, doc.FoldedTitle, 1) > 0);
            int count = int.MaxValue;
            foreach (var t in terms) { count = Math.Min(count, Count(t, doc.Folded, CountLimit)); if (count == 0) break; }
            if (!title && count == 0) return;
            bool heading = !title && count > 0 && doc.FoldedHeadings.Any(f => terms.All(t => Count(t, f, 1) > 0));
            matches[i] = (doc, title, heading, count);
        });
        var ranked = matches.Where(m => m != null).Select(m => m!.Value)
            .OrderByDescending(m => m.Title).ThenByDescending(m => m.Heading).ThenByDescending(m => m.Count)
            .ThenBy(m => m.Doc.RelativePath, Comparer<string>.Create(Text.NaturalCompare)).ToList();
        int total = ranked.Sum(m => m.Count);
        var shown = ranked.Take(maxDocuments).ToList();
        var results = new DocumentResult[shown.Count];
        Parallel.For(0, shown.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var m = shown[i];
            var hits = m.Count > 0 ? Hits(m.Doc, terms, maxHitsPerDocument) : new List<Hit>();
            results[i] = new DocumentResult(m.Doc.Path, m.Doc.RelativePath, m.Doc.Title, m.Title, m.Count, hits);
        });
        return new Results(results.ToList(), total, docs.Count, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Nombre d'occurrences en début de mot.</summary>
    internal static int Count(string term, string haystack, int limit)
    {
        if (term.Length == 0 || haystack.Length < term.Length) return 0;
        bool startsWithWord = SearchFold.IsWordChar(term[0]);
        int n = 0, offset = 0;
        while (n < limit)
        {
            int pos = haystack.IndexOf(term, offset, StringComparison.Ordinal);
            if (pos < 0) break;
            if (!startsWithWord || pos == 0 || !SearchFold.IsWordChar(haystack[pos - 1])) n++;
            offset = pos + 1;
        }
        return n;
    }

    internal static List<int> Find(string term, string haystack, int limit)
    {
        var output = new List<int>();
        if (term.Length == 0 || haystack.Length < term.Length) return output;
        bool startsWithWord = SearchFold.IsWordChar(term[0]);
        int offset = 0;
        while (output.Count < limit)
        {
            int pos = haystack.IndexOf(term, offset, StringComparison.Ordinal);
            if (pos < 0) break;
            if (!startsWithWord || pos == 0 || !SearchFold.IsWordChar(haystack[pos - 1])) output.Add(pos);
            offset = pos + 1;
        }
        return output;
    }

    /// <summary>Occurrences affichées : paragraphes qui contiennent tous les termes (intertitres d'abord), avec contexte.</summary>
    internal static List<Hit> Hits(IndexedDocument doc, IReadOnlyList<string> terms, int limit)
    {
        var perTerm = terms.Select(t => Find(t, doc.Folded, 20_000)).ToList();
        if (perTerm.Any(p => p.Count == 0)) return new();
        var sets = perTerm.Select(p => p.Select(doc.ParagraphIndexOfFolded).ToHashSet()).ToList();
        var common = new HashSet<int>(sets[0]);
        foreach (var s in sets.Skip(1)) common.IntersectWith(s);
        int anchor = 0;
        if (common.Count == 0)
        {
            anchor = Enumerable.Range(0, perTerm.Count).OrderBy(i => perTerm[i].Count).First();
            common = sets[anchor];
        }
        var firstInParagraph = new Dictionary<int, int>();
        foreach (var pos in perTerm[anchor])
        {
            int p = doc.ParagraphIndexOfFolded(pos);
            if (common.Contains(p)) firstInParagraph.TryAdd(p, pos);
        }
        var headingStarts = doc.Headings.Select(h => h.Location).ToHashSet();
        var ordered = firstInParagraph.Keys.OrderBy(p => p).Select(p => (P: p, IsHeading: headingStarts.Contains(doc.Paragraphs[p].Original)))
            .OrderByDescending(x => x.IsHeading).ThenBy(x => x.P);
        var output = new List<Hit>();
        foreach (var (p, isHeading) in ordered.Take(limit))
            if (MakeHit(doc, p, firstInParagraph[p], terms[anchor].Length, isHeading) is { } hit) output.Add(hit);
        return output;
    }

    static Hit? MakeHit(IndexedDocument doc, int p, int foldedPosition, int termLength, bool isHeading)
    {
        var body = doc.Body;
        int paraStart = doc.Paragraphs[p].Original;
        if (paraStart > body.Length) return null;
        var (pStart, pLength) = Text.ParagraphRange(body, paraStart);
        var paragraph = body.Substring(pStart, pLength);
        var sb = new StringBuilder();
        var map = new List<int>();
        SearchFold.Fold(paragraph, true, sb, map, null);
        int local = foldedPosition - doc.Paragraphs[p].Folded;
        if (local < 0 || local + termLength > map.Count || termLength <= 0) return null;
        int startLocal = map[local];
        int lastChar = map[local + termLength - 1];
        int endLocal = Math.Min(paragraph.Length, lastChar + (char.IsHighSurrogate(paragraph[Math.Min(lastChar, paragraph.Length - 1)]) ? 2 : 1));
        int matchLength = Math.Max(0, endLocal - startLocal);
        var before = ContextBefore(paragraph, startLocal);
        var after = ContextAfter(paragraph, startLocal + matchLength);
        int start = pStart + startLocal;
        string? parent = isHeading ? null : doc.Headings.LastOrDefault(h => h.Location < pStart).Title;
        string? verse = doc.IsPoem ? Poem.Reference(start, matchLength, body) : null;
        return new Hit($"{doc.RelativePath}#{start}", start, matchLength, Clean(before), Clean(paragraph.Substring(startLocal, matchLength)),
                       Clean(after), parent, isHeading, verse);
    }

    /// <summary>Environ une phrase avant le terme (160 caractères au plus, coupé à un mot).</summary>
    internal static string ContextBefore(string text, int location)
    {
        int window = Math.Max(0, location - 160);
        var slice = text[window..location];
        int start = 0;
        bool ellipsis = window > 0;
        int searchEnd = slice.Length;
        for (int pass = 0; pass < 2; pass++)
        {
            var matches = Regex.Matches(slice[..searchEnd], @"[.!?…][""»”)]?\s+");
            if (matches.Count == 0) break;
            var r = matches[^1];
            start = r.Index + r.Length;
            ellipsis = false;
            if (slice.Length - start >= 40 || r.Index == 0) break;
            searchEnd = r.Index;
        }
        if (ellipsis)
        {
            int space = slice.IndexOf(' ');
            if (space >= 0 && space < 30) start = space + 1;
        }
        return (ellipsis ? "…" : "") + slice[start..];
    }

    /// <summary>Environ une phrase après le terme (160 caractères au plus).</summary>
    internal static string ContextAfter(string text, int location)
    {
        int end = Math.Min(text.Length, location + 160);
        var slice = text[location..end];
        var r = Regex.Match(slice, @"[.!?](\s|$)");
        if (r.Success && r.Index > 20) return slice[..(r.Index + r.Length)].Trim('\n', '\r');
        var s = slice.Trim('\n', '\r');
        if (end < text.Length && s.LastIndexOf(' ') is var space and >= 0) s = s[..space] + "…";
        return s;
    }

    /// <summary>Texte affichable : syntaxe Markdown retirée.</summary>
    internal static string Clean(string s)
    {
        var t = Regex.Replace(s, @"<[^>\n]{1,80}>|\*\*|__|==|\[\[|\]\]|(?<![\w])[*_]|[*_](?![\w])", "");
        t = Regex.Replace(t, @"^(#{1,6}|>|\[\d+\])\s*", "");
        t = Regex.Replace(t, @"\\([\\`*_{}\[\]()#+\-.!>])", "$1");
        return t.Replace("\r", "").Replace("\n", " ");
    }
}
