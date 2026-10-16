using System.Globalization;
using System.Text;

namespace Ecritoire.Core;

/// <summary>Une modification entre deux versions d'un texte (positions en UTF-16, comme NSString).</summary>
public sealed record DiffHunk(int BaseStart, int BaseLength, int CurrentStart, int CurrentLength, string Deleted, string Inserted);

/// <summary>
/// Différence lisible entre deux versions (TextDiff.swift) : d'abord par paragraphe, puis mot à mot à l'intérieur des
/// paragraphes modifiés ; deux modifications séparées par une seule espace sont fusionnées.
/// </summary>
public static class TextDiff
{
    public static List<DiffHunk> Hunks(string @base, string current)
    {
        var result = new List<DiffHunk>();
        if (@base == current) return result;
        var bl = Lines(@base); var cl = Lines(current);
        foreach (var (a0, a1, b0, b1) in Blocks(bl.Select(p => p.Text).ToList(), cl.Select(p => p.Text).ToList()))
        {
            int baseStart = a0 < bl.Count ? bl[a0].Offset : @base.Length;
            int curStart = b0 < cl.Count ? cl[b0].Offset : current.Length;
            var deleted = string.Concat(bl.Skip(a0).Take(a1 - a0).Select(p => p.Text));
            var inserted = string.Concat(cl.Skip(b0).Take(b1 - b0).Select(p => p.Text));
            var hunk = new DiffHunk(baseStart, deleted.Length, curStart, inserted.Length, deleted, inserted);
            if (deleted.Length > 0 && inserted.Length > 0 && deleted.Length + inserted.Length < 40_000) result.AddRange(Refine(hunk));
            else result.Add(hunk);
        }
        return result;
    }

    record struct Piece(string Text, int Offset);

    static List<Piece> Lines(string text)
    {
        var output = new List<Piece>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { output.Add(new Piece(text[start..(i + 1)], start)); start = i + 1; }
        if (start < text.Length) output.Add(new Piece(text[start..], start));
        return output;
    }

    /// <summary>Mots, espaces et ponctuation séparés (par caractère étendu, comme Character en Swift).</summary>
    static List<Piece> Tokens(string text)
    {
        var output = new List<Piece>();
        var current = new StringBuilder();
        int offset = 0, currentKind = -1;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var c = (string)e.Current;
            int k = Kind(c);
            if (k != currentKind || k == 2)
            {
                if (current.Length > 0) { output.Add(new Piece(current.ToString(), offset)); offset += current.Length; }
                current.Clear();
                currentKind = k;
            }
            current.Append(c);
        }
        if (current.Length > 0) output.Add(new Piece(current.ToString(), offset));
        return output;
    }

    static int Kind(string c)
    {
        var rune = Rune.GetRuneAt(c, 0);
        if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) return 0;
        if (c.Length > 0 && (Rune.IsWhiteSpace(rune) || c == "\r\n")) return 1;
        return 2;
    }

    /// <summary>Blocs modifiés (plages d'indices), préfixe et suffixe communs retirés d'abord.</summary>
    static List<(int A0, int A1, int B0, int B1)> Blocks(List<string> a, List<string> b)
    {
        int start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
        int endA = a.Count, endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }
        var midA = a.GetRange(start, endA - start); var midB = b.GetRange(start, endB - start);
        var (removed, inserted) = Myers(midA, midB);
        var output = new List<(int, int, int, int)>();
        int i = 0, j = 0;
        while (i < midA.Count || j < midB.Count)
        {
            if (removed.Contains(i) || inserted.Contains(j))
            {
                int i0 = i, j0 = j;
                while (i < midA.Count && removed.Contains(i)) i++;
                while (j < midB.Count && inserted.Contains(j)) j++;
                output.Add((start + i0, start + i, start + j0, start + j));
            }
            else { i++; j++; }
        }
        return output;
    }

    /// <summary>Suppressions et insertions, exactement comme difference(from:) de la bibliothèque standard Swift :
    /// Myers en espace linéaire (« serpent du milieu », diviser pour régner), avec les mêmes départages
    /// (stdlib/public/core/Diffing.swift).</summary>
    internal static (HashSet<int> Removed, HashSet<int> Inserted) Myers(List<string> a, List<string> b)
    {
        var removed = new HashSet<int>(); var inserted = new HashSet<int>();
        var box = Shrunk(new Rect(0, 0, a.Count, b.Count), a, b);
        if (box.Size < 0) return (removed, inserted);
        var myers = new LinearMyers(box.Size);
        foreach (var (isInsertion, offset) in myers.FindDifferences(box, a, b))
            (isInsertion ? inserted : removed).Add(offset);
        return (removed, inserted);
    }

    record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int Size => Width + Height;
        public int Delta => Width - Height;
        public bool IsEven => Delta % 2 == 0;
        public bool IsOdd => !IsEven;
        public int Max => (Size + 1) / 2;
        public Rect CroppedToTopLeftOf(Rect l) => new(Left, Top, l.Left, l.Top);
        public Rect CroppedToBottomRightOf(Rect l) => new(l.Right, l.Bottom, Right, Bottom);
    }

    static Rect Shrunk(Rect r, List<string> old, List<string> @new)
    {
        int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        while (left < right && top < bottom && old[left] == @new[top]) { left++; top++; }
        while (right > left && bottom > top && old[right - 1] == @new[bottom - 1]) { right--; bottom--; }
        return new Rect(left, top, right, bottom);
    }

    sealed class LinearMyers
    {
        readonly int[] buffer;
        readonly int forwardOffset, backwardOffset;

        public LinearMyers(int size)
        {
            int range = size * 2 + 1;
            buffer = new int[range * 2];
            forwardOffset = size;
            backwardOffset = range + size;
        }

        int F(int k) => buffer[forwardOffset + k];
        void SetF(int k, int v) => buffer[forwardOffset + k] = v;
        int B(int c) => buffer[backwardOffset + c];
        void SetB(int c, int v) => buffer[backwardOffset + c] = v;

        public List<(bool IsInsertion, int Offset)> FindDifferences(Rect initial, List<string> old, List<string> @new)
        {
            var result = new List<(bool, int)>();
            var stack = new List<Rect>();
            var current = initial;
            while (true)
            {
                var (edit, snake) = MiddleSnake(current, old, @new);
                if (edit is { } e) result.Add(e);
                if (snake is not { } snakeBox)
                {
                    if (stack.Count == 0) return result;
                    current = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                var head = Shrunk(current.CroppedToTopLeftOf(snakeBox), old, @new);
                var tail = Shrunk(current.CroppedToBottomRightOf(snakeBox), old, @new);
                current = head;
                stack.Add(tail);
            }
        }

        ((bool, int)?, Rect?) MiddleSnake(Rect box, List<string> old, List<string> @new)
        {
            if (box.Size <= 1)
            {
                if (box.Size == 0) return (null, null);
                return box.Width == 1 ? ((false, box.Left), null) : ((true, box.Top), null);
            }
            SetF(1, box.Left);
            SetB(1, box.Bottom);
            for (int depth = 0; depth <= box.Max; depth++)
            {
                if (box.IsOdd)
                {
                    if (Forward(box, depth, old, @new) is { } r) return (r.Edit, r.Snake);
                    Backward(box, depth, old, @new);
                }
                else
                {
                    Forward(box, depth, old, @new);
                    if (Backward(box, depth, old, @new) is { } r) return (r.Edit, r.Snake);
                }
            }
            throw new InvalidOperationException("Myers : inaccessible");
        }

        ((bool, int) Edit, Rect Snake)? Forward(Rect box, int depth, List<string> old, List<string> @new)
        {
            for (int k = depth; k >= -depth; k -= 2)
            {
                int newRight, newBottom, newLeft, newTop, editIndex;
                bool isInsertion;
                if (k == -depth || (k != depth && F(k - 1) < F(k + 1)))
                {
                    newRight = F(k + 1);
                    newLeft = newRight;
                    newBottom = box.Top + (newRight - box.Left) - k;
                    isInsertion = true;
                    editIndex = newBottom - 1;
                }
                else
                {
                    newLeft = F(k - 1);
                    newRight = newLeft + 1;
                    newBottom = box.Top + (newRight - box.Left) - k;
                    isInsertion = false;
                    editIndex = newLeft;
                }
                newTop = depth == 0 || newRight != newLeft ? newBottom : newBottom - 1;
                while (newRight < box.Right && newBottom < box.Bottom && old[newRight] == @new[newBottom]) { newRight++; newBottom++; }
                SetF(k, newRight);
                if (!box.IsOdd) continue;
                int c = k - box.Delta;
                if (c >= -(depth - 1) && c <= depth - 1 && newBottom >= B(c))
                    return ((isInsertion, editIndex), new Rect(newLeft, newTop, newRight, newBottom));
            }
            return null;
        }

        ((bool, int) Edit, Rect Snake)? Backward(Rect box, int depth, List<string> old, List<string> @new)
        {
            for (int c = depth; c >= -depth; c -= 2)
            {
                int k = c + box.Delta;
                int newLeft, newTop, newRight, newBottom, editIndex;
                bool isInsertion;
                if (c == -depth || (c != depth && B(c - 1) > B(c + 1)))
                {
                    newTop = B(c + 1);
                    newBottom = newTop;
                    newLeft = box.Left + (newTop - box.Top) + k;
                    isInsertion = false;
                    editIndex = newLeft;
                }
                else
                {
                    newBottom = B(c - 1);
                    newTop = newBottom - 1;
                    newLeft = box.Left + (newTop - box.Top) + k;
                    isInsertion = true;
                    editIndex = newTop;
                }
                newRight = depth == 0 || newTop != newBottom ? newLeft : newLeft + 1;
                while (newLeft > box.Left && newTop > box.Top && old[newLeft - 1] == @new[newTop - 1]) { newLeft--; newTop--; }
                SetB(c, newTop);
                if (!box.IsEven) continue;
                if (k >= -depth && k <= depth && newLeft <= F(k))
                    return ((isInsertion, editIndex), new Rect(newLeft, newTop, newRight, newBottom));
            }
            return null;
        }
    }

    static List<DiffHunk> Refine(DiffHunk h)
    {
        var a = Tokens(h.Deleted); var b = Tokens(h.Inserted);
        var output = new List<DiffHunk>();
        foreach (var (a0, a1, b0, b1) in Blocks(a.Select(p => p.Text).ToList(), b.Select(p => p.Text).ToList()))
        {
            int aStart = a0 < a.Count ? a[a0].Offset : h.Deleted.Length;
            int bStart = b0 < b.Count ? b[b0].Offset : h.Inserted.Length;
            var deleted = string.Concat(a.Skip(a0).Take(a1 - a0).Select(p => p.Text));
            var inserted = string.Concat(b.Skip(b0).Take(b1 - b0).Select(p => p.Text));
            output.Add(new DiffHunk(h.BaseStart + aStart, deleted.Length, h.CurrentStart + bStart, inserted.Length, deleted, inserted));
        }
        return Merge(output, h);
    }

    static List<DiffHunk> Merge(List<DiffHunk> hunks, DiffHunk @base)
    {
        var output = new List<DiffHunk>();
        if (hunks.Count == 0) return output;
        var cur = hunks[0];
        foreach (var next in hunks.Skip(1))
        {
            int gapBase = next.BaseStart - (cur.BaseStart + cur.BaseLength);
            int gapCur = next.CurrentStart - (cur.CurrentStart + cur.CurrentLength);
            int gapFrom = cur.BaseStart + cur.BaseLength - @base.BaseStart;
            var gapText = @base.Deleted.Substring(gapFrom, Math.Max(0, gapBase));
            if (gapBase == gapCur && gapBase <= 1 && gapText.Trim(' ', '\t').Length == 0)
            {
                int bStart = cur.BaseStart, bLen = next.BaseStart + next.BaseLength - cur.BaseStart;
                int cStart = cur.CurrentStart, cLen = next.CurrentStart + next.CurrentLength - cur.CurrentStart;
                cur = new DiffHunk(bStart, bLen, cStart, cLen, @base.Deleted.Substring(bStart - @base.BaseStart, bLen), @base.Inserted.Substring(cStart - @base.CurrentStart, cLen));
            }
            else { output.Add(cur); cur = next; }
        }
        output.Add(cur);
        return output;
    }

    /// <summary>Accepter une modification : l'ancienne version l'intègre.</summary>
    public static string Accept(DiffHunk h, string @base) => @base.Remove(h.BaseStart, h.BaseLength).Insert(h.BaseStart, h.Inserted);

    /// <summary>Refuser une modification : la version actuelle revient au texte d'origine.</summary>
    public static string Reject(DiffHunk h, string current) => current.Remove(h.CurrentStart, h.CurrentLength).Insert(h.CurrentStart, h.Deleted);
}
