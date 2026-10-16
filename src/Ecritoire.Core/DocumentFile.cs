using System.Text;

namespace Ecritoire.Core;

/// <summary>
/// Un document ouvert : lecture et enregistrement avec les mêmes règles que l'app macOS (EditorModel.save) :
/// UTF-8 sans BOM, fins de ligne LF ; un fichier qui n'avait pas d'en-tête YAML (un poème, une note) est réécrit
/// tel quel tant que rien d'autre que le texte n'a changé ; rien n'est écrit si le texte est identique au disque.
/// </summary>
public sealed class DocumentFile
{
    static readonly UTF8Encoding Utf8 = new(false);

    public string Path { get; private set; }
    public string Title { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public string Status { get; set; } = DocumentStatus.Brouillon;
    public bool Publish { get; set; }
    public string Body { get; set; } = "";
    public List<string> ExtraLines { get; private set; } = new();
    /// <summary>Le fichier avait un en-tête YAML (sinon on n'en ajoute pas).</summary>
    public bool HasFrontmatter { get; private set; } = true;
    /// <summary>Dernier texte lu ou écrit sur le disque.</summary>
    public string LastWritten { get; private set; } = "";

    DocumentFile(string path) { Path = path; }

    public string DefaultTitle => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Titre affiché : celui de l'en-tête, à défaut le nom du fichier.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? DefaultTitle : Title.Trim();

    public static DocumentFile Load(string path)
    {
        var file = new DocumentFile(path);
        file.Reload();
        return file;
    }

    public void Reload()
    {
        var text = ReadText(Path);
        var doc = MarkdownDocument.Parse(text, DefaultTitle);
        HasFrontmatter = text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal);
        Title = doc.Title; Synopsis = doc.Synopsis; Status = doc.Status; Publish = doc.Publish;
        Body = doc.Body; ExtraLines = doc.ExtraLines;
        LastWritten = text;
    }

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Utf8.GetString(bytes, skip, bytes.Length - skip);
    }

    /// <summary>Texte à écrire sur le disque (même règle que l'app macOS).</summary>
    public string TextToWrite()
    {
        var body = Body.Replace("\r\n", "\n");
        bool poemNumbering = Poem.IsPoem(body);
        bool plain = !HasFrontmatter && (poemNumbering || (Title == DefaultTitle && Synopsis.Length == 0
            && Status == DocumentStatus.Brouillon && !Publish && ExtraLines.Count == 0));
        if (plain) return body;
        return new MarkdownDocument { Title = Title, Synopsis = Synopsis, Status = Status, Publish = Publish, Body = body, ExtraLines = ExtraLines }
            .Serialized();
    }

    /// <summary>Enregistre si le texte a changé. Renvoie vrai si le fichier a été écrit.</summary>
    public bool Save()
    {
        var text = TextToWrite();
        if (text == LastWritten) return false;
        WriteAtomic(Path, text);
        LastWritten = text;
        return true;
    }

    /// <summary>Le fichier a-t-il été modifié par un autre programme depuis la dernière lecture ou écriture ?</summary>
    public bool ChangedOnDisk() => File.Exists(Path) && ReadText(Path) != LastWritten;

    /// <summary>Écriture atomique : fichier temporaire à côté, puis remplacement.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var temp = System.IO.Path.Combine(dir, "." + System.IO.Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        File.WriteAllText(temp, text, Utf8);
        File.Move(temp, path, overwrite: true);
    }
}
