using System.Text.RegularExpressions;

namespace Ecritoire.Pdf;

/// <summary>
/// Mise en forme dans un paragraphe (MarkdownHighlighter.swift : inline) : gras, italique, code, liens ; les signes
/// Markdown disparaissent comme dans l'éditeur hors du paragraphe du curseur.
/// </summary>
public static class Inline
{
    public readonly record struct Run(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Link = false);

    static readonly Regex BothRE = new(@"(?<![*\\])(\*\*\*|___)(?=[^\s*_])(.+?)(?<=[^\s*_\\])\1(?![*_])", RegexOptions.Compiled);
    static readonly Regex BoldRE = new(@"(?<!\\)(\*\*|__)(?=\S)(.+?)(?<=\S)(?<!\\)\1", RegexOptions.Compiled);
    static readonly Regex ItalicRE = new(@"(?<![*_\\\w])([*_])(?=[^\s*_])(.+?)(?<=[^\s*_\\])\1(?![*_\w])", RegexOptions.Compiled);
    static readonly Regex CodeRE = new(@"`[^`\n]+`", RegexOptions.Compiled);
    static readonly Regex WikiRE = new(@"(?<!!)\[\[([^\]\n|]+)(?:\|([^\]\n]+))?\]\]", RegexOptions.Compiled);
    static readonly Regex LinkRE = new(@"(?<!!)\[([^\]\n]+)\]\(([^)\n]+)\)", RegexOptions.Compiled);
    static readonly Regex EscapeRE = new(@"\\([\\`*_{}\[\]()#+\-.!>])", RegexOptions.Compiled);

    public static List<Run> Runs(string line)
    {
        var output = new List<Run>();
        Parse(line, false, false, output);
        return output;
    }

    static void Parse(string s, bool bold, bool italic, List<Run> output)
    {
        int pos = 0;
        while (pos < s.Length)
        {
            Match? best = null; int which = -1;
            var regexes = new[] { CodeRE, WikiRE, LinkRE, BothRE, BoldRE, ItalicRE };
            for (int i = 0; i < regexes.Length; i++)
            {
                var m = regexes[i].Match(s, pos);
                if (m.Success && (best == null || m.Index < best.Index)) { best = m; which = i; }
            }
            if (best == null) { Plain(s[pos..], bold, italic, output); return; }
            if (best.Index > pos) Plain(s[pos..best.Index], bold, italic, output);
            switch (which)
            {
                case 0: output.Add(new Run(best.Value, bold, italic, Code: true)); break;
                case 1: output.Add(new Run(best.Groups[2].Success ? best.Groups[2].Value : best.Groups[1].Value, bold, italic, Link: true)); break;
                case 2: output.Add(new Run(Unescape(best.Groups[1].Value), bold, italic, Link: true)); break;
                case 3: Parse(best.Groups[2].Value, true, true, output); break;
                case 4: Parse(best.Groups[2].Value, true, italic, output); break;
                case 5: Parse(best.Groups[2].Value, bold, true, output); break;
            }
            pos = best.Index + best.Length;
        }
    }

    static void Plain(string s, bool bold, bool italic, List<Run> output)
    {
        if (s.Length > 0) output.Add(new Run(Unescape(s), bold, italic));
    }

    static string Unescape(string s) => EscapeRE.Replace(s, "$1");
}

/// <summary>Dimensions en pixels d'une image PNG, JPEG ou GIF (lues dans l'en-tête).</summary>
public static class ImageSize
{
    public static (int Width, int Height)? Of(byte[] b)
    {
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
            return (BE32(b, 16), BE32(b, 20));
        if (b.Length > 10 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F')
            return (b[6] | b[7] << 8, b[8] | b[9] << 8);
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                int len = b[i + 2] << 8 | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
                i += 2 + len;
            }
        }
        return null;
    }

    static int BE32(byte[] b, int i) => b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3];
}
