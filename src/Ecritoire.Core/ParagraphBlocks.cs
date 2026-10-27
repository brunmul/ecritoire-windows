using System.Text.RegularExpressions;

namespace Ecritoire.Core;

/// <summary>
/// Blocs de paragraphes du texte (lignes séparées par « \n », indices à partir de 0) pour la poignée façon Notion :
/// déplacer, dupliquer, supprimer, transformer. Un bloc est une ligne, ou une suite de lignes « > » (citation, encadré
/// « intro ») ; les lignes vides qui le suivent voyagent avec lui. Le format du fichier reste du Markdown ordinaire.
/// </summary>
public static class ParagraphBlocks
{
    static readonly Regex PrefixRE = new(@"^(#{1,6}[ \t]+|>[ \t]?|[-*+][ \t]+|\d+[.)][ \t]+)", RegexOptions.Compiled);

    public static string[] Lines(string text) => text.Split('\n');

    /// <summary>Première et dernière ligne du bloc qui contient la ligne <paramref name="index"/>.</summary>
    public static (int Start, int End) Block(IReadOnlyList<string> lines, int index)
    {
        int start = index, end = index;
        if (lines[index].StartsWith('>'))
        {
            while (start > 0 && lines[start - 1].StartsWith('>')) start--;
            while (end + 1 < lines.Count && lines[end + 1].StartsWith('>')) end++;
        }
        return (start, end);
    }

    static bool Empty(string s) => s.Trim().Length == 0;

    /// <summary>Retire le bloc et l'écart (lignes vides) qui le sépare du suivant, ou du précédent s'il est le dernier.</summary>
    static (List<string> Block, int Gap, int RemovedFrom, int RemovedCount) Take(List<string> lines, int index)
    {
        var (s, e) = Block(lines, index);
        var block = lines.GetRange(s, e - s + 1);
        int after = 0;
        while (e + 1 + after < lines.Count && Empty(lines[e + 1 + after])) after++;
        // Dernier bloc : les lignes vides finales restent en place, on prend l'écart qui le précède.
        if (e + 1 + after >= lines.Count) after = 0;
        int before = 0;
        if (after == 0) while (s - 1 - before >= 0 && Empty(lines[s - 1 - before])) before++;
        int from = s - before, count = e - s + 1 + after + before;
        lines.RemoveRange(from, count);
        return (block, Math.Max(after, before), from, count);
    }

    /// <summary>
    /// Déplace le bloc de la ligne <paramref name="index"/> avant la ligne <paramref name="before"/> (indices du texte
    /// d'origine ; le nombre de lignes pour la fin). Renvoie le nouveau texte et la ligne où commence le bloc, ou null
    /// si rien ne bouge.
    /// </summary>
    public static (string Text, int Line)? Move(string text, int index, int before)
    {
        var lines = Lines(text).ToList();
        var (s, e) = Block(lines, index);
        if (before >= s && before <= e + 1) return null;
        // Juste après les lignes vides qui suivent le bloc : même place.
        int next = e + 1;
        while (next < lines.Count && Empty(lines[next])) next++;
        if (before > e && (before <= next || next >= lines.Count)) return null;
        var (block, gap, _, count) = Take(lines, index);
        int target = before > e ? before - count : before;
        var spacing = Enumerable.Repeat("", gap).ToList();
        int line;
        if (target >= lines.Count || lines.Skip(target).All(Empty))
        {
            // À la fin : avant les lignes vides finales, séparé du texte qui précède.
            int end = lines.Count;
            while (end > 0 && Empty(lines[end - 1])) end--;
            var inserted = (end > 0 ? spacing : new List<string>()).Concat(block).ToList();
            lines.InsertRange(end, inserted);
            line = end + (end > 0 ? spacing.Count : 0);
        }
        else
        {
            lines.InsertRange(target, block.Concat(spacing));
            line = target;
        }
        return (string.Join("\n", lines), line);
    }

    /// <summary>Copie du bloc juste après lui, avec le même écart.</summary>
    public static (string Text, int Line) Duplicate(string text, int index)
    {
        var lines = Lines(text).ToList();
        var (s, e) = Block(lines, index);
        int gap = 0;
        while (e + 1 + gap < lines.Count && Empty(lines[e + 1 + gap])) gap++;
        // Écart du bloc (aucun entre deux éléments de liste collés) ; une ligne vide s'il est le dernier.
        if (e + 1 + gap >= lines.Count) gap = 1;
        lines.InsertRange(e + 1, Enumerable.Repeat("", gap).Concat(lines.GetRange(s, e - s + 1)).ToList());
        return (string.Join("\n", lines), e + 1 + gap);
    }

    /// <summary>Supprime le bloc et son écart.</summary>
    public static (string Text, int Line) Delete(string text, int index)
    {
        var lines = Lines(text).ToList();
        var (_, _, from, _) = Take(lines, index);
        return (string.Join("\n", lines), Math.Min(from, Math.Max(0, lines.Count - 1)));
    }

    /// <summary>Ligne avec un autre signe de début (« # », « ## », « > », « - », ou rien pour du texte).</summary>
    public static string Transform(string line, string prefix) => prefix + PrefixRE.Replace(line, "", 1);
}
