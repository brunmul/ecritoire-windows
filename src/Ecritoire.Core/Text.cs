using System.Globalization;
using System.Text;

namespace Ecritoire.Core;

/// <summary>Outils de texte communs (positions en unités UTF-16, comme NSString dans l'app macOS).</summary>
public static class Text
{
    public static string RemoveDiacritics(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    static bool IsParagraphEnd(char c) => c == '\n' || c == '\r' || c == '\u2029' || c == '\u0085';

    /// <summary>Paragraphe qui contient <paramref name="location"/>, fin de ligne comprise (comme NSString.paragraphRange).</summary>
    public static (int Start, int Length) ParagraphRange(string text, int location)
    {
        location = Math.Clamp(location, 0, text.Length);
        int start = location;
        while (start > 0 && !IsParagraphEnd(text[start - 1])) start--;
        int end = location;
        while (end < text.Length && !IsParagraphEnd(text[end])) end++;
        if (end < text.Length)
        {
            if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n') end += 2; else end++;
        }
        return (start, end - start);
    }

    /// <summary>Paragraphes (sans leur fin de ligne) : début, longueur, longueur avec la fin de ligne.</summary>
    public static IEnumerable<(int Start, int Length, int Enclosing)> Paragraphs(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && !IsParagraphEnd(text[i])) i++;
            int length = i - start;
            if (i < text.Length) { if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i += 2; else i++; }
            yield return (start, length, i - start);
        }
    }

    /// <summary>Comparaison « naturelle » des noms (2 avant 10, sans tenir compte de la casse), comme le Finder.</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        var cmp = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int si = i, sj = j;
                while (i < a.Length && !char.IsDigit(a[i])) i++;
                while (j < b.Length && !char.IsDigit(b[j])) j++;
                int c = cmp.Compare(a[si..i], b[sj..j], CompareOptions.IgnoreCase);
                if (c != 0) return c;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
