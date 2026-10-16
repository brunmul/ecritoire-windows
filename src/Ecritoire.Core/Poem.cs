using System.Text.RegularExpressions;

namespace Ecritoire.Core;

/// <summary>
/// Format « poème » (Poem.swift) : un verset par paragraphe, préfixé de sa position entre crochets (« [12] Texte… »).
/// Ce qui précède le premier verset (titre « # … », ligne de révision, note) est l'en-tête, clos par une ligne « --- ».
/// « [n](…) » en début de ligne est un renvoi de note (lien), pas un numéro de verset.
/// </summary>
public static class Poem
{
    public sealed record Verse(int Number, int Start, int Length, (int Start, int Length)? Marker, string Text);

    static readonly Regex MarkerRE = new(@"^\[(\d{1,4})\](?!\()[ \t]?", RegexOptions.Compiled);
    static readonly Regex InnerMarkerRE = new(@"\[\d{1,4}\](?!\()[ \t]?", RegexOptions.Compiled);
    static readonly Regex RuleRE = new(@"^[ \t]*(?:-{3,}|\*{3,}|_{3,})[ \t]*$", RegexOptions.Compiled);

    public static (int Start, int Length, int Number)? Marker(string line)
    {
        var m = MarkerRE.Match(line);
        return m.Success ? (m.Index, m.Length, int.Parse(m.Groups[1].Value)) : null;
    }

    public static bool IsRule(string line) => RuleRE.IsMatch(line);

    /// <summary>Vrai si le texte suit le format des versets numérotés (au moins deux paragraphes « [n] »).</summary>
    public static bool IsPoem(string text)
    {
        int count = 0;
        foreach (var (start, length, _) in Text.Paragraphs(text))
            if (Marker(text.Substring(start, length)) != null && ++count >= 2) return true;
        return false;
    }

    /// <summary>Fin de l'en-tête : après la ligne « --- » qui précède le premier verset, sinon au premier verset.</summary>
    public static int HeaderEnd(string text)
    {
        int? ruleEnd = null;
        foreach (var (start, length, enclosing) in Text.Paragraphs(text))
        {
            var line = text.Substring(start, length);
            if (Marker(line) != null) return ruleEnd ?? start;
            if (IsRule(line)) ruleEnd = start + enclosing;
        }
        return ruleEnd ?? 0;
    }

    /// <summary>Versets (paragraphes non vides après l'en-tête), numérotés d'après leur position.</summary>
    public static List<Verse> Verses(string text)
    {
        var output = new List<Verse>();
        int headerEnd = HeaderEnd(text);
        foreach (var (start, length, _) in Text.Paragraphs(text))
        {
            if (start < headerEnd) continue;
            var line = text.Substring(start, length);
            if (line.Trim(' ', '\t').Length == 0) continue;
            var m = Marker(line);
            (int, int)? marker = m is { } mm ? (start + mm.Start, mm.Length) : null;
            var body = m is { } m2 ? line[(m2.Start + m2.Length)..] : line;
            output.Add(new Verse(output.Count + 1, start, length, marker, body));
        }
        return output;
    }

    /// <summary>Référence des versets touchés par une plage : « v. 12 », « v. 9–11 » (null hors des versets).</summary>
    public static string? Reference(int start, int length, string text)
    {
        var touched = Verses(text).Where(v =>
            (Math.Max(v.Start, start) < Math.Min(v.Start + v.Length, start + length)) || (start >= v.Start && start < v.Start + v.Length)).ToList();
        if (touched.Count == 0) return null;
        int first = touched[0].Number, last = touched[^1].Number;
        return first == last ? $"v. {first}" : $"v. {first}–{last}";
    }

    /// <summary>Texte renuméroté d'après la position des versets (mêmes règles que l'app macOS).</summary>
    public static string Renumbered(string text)
    {
        if (!IsPoem(text)) return text;
        var edits = new List<(int Start, int Length, string Replacement)>();
        int? previousEnd = null;
        foreach (var verse in Verses(text))
        {
            var wanted = $"[{verse.Number}] ";
            bool glued = previousEnd is { } p && verse.Start == p + 1;
            if (verse.Marker is { } marker)
            {
                if (text.Substring(marker.Start, marker.Length) != wanted) edits.Add((marker.Start, marker.Length, wanted));
                if (glued) edits.Add((verse.Start, 0, "\n"));
            }
            else edits.Add((verse.Start, 0, (glued ? "\n" : "") + wanted));
            int contentStart = verse.Marker is { } mk ? mk.Start + mk.Length : verse.Start;
            var content = text.Substring(contentStart, verse.Start + verse.Length - contentStart);
            foreach (Match m in InnerMarkerRE.Matches(content).Reverse())
            {
                int at = contentStart + m.Index;
                char before = at > 0 ? text[at - 1] : ' ';
                bool needsSpace = !(before == ' ' || before == '\t') && at > contentStart;
                edits.Add((at, m.Length, needsSpace ? " " : ""));
            }
            previousEnd = verse.Start + verse.Length;
        }
        var sorted = edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.Length);
        var output = new System.Text.StringBuilder(text);
        foreach (var e in sorted) output.Remove(e.Start, e.Length).Insert(e.Start, e.Replacement);
        return output.ToString();
    }

    /// <summary>Premiers mots d'un verset (pour le sommaire).</summary>
    public static string Incipit(string text, int words = 6)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var head = string.Join(" ", parts.Take(words));
        return parts.Length > words ? head + "…" : head;
    }
}
