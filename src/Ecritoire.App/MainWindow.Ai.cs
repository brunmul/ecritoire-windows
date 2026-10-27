using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Ecritoire.Core;

namespace Ecritoire.App;

/// <summary>
/// Barre « Demander à l'IA… » (interface v2 de l'app macOS), en bas de la colonne. Comme sur Mac, elle n'appelle pas
/// d'IA elle-même : elle prépare une demande pour Claude (la consigne, le passage sélectionné, le texte intégral), la copie
/// dans le presse-papiers ou l'enregistre dans .ecritoire/prompts, et prend un instantané pour voir ensuite ce que Claude
/// aura modifié.
/// </summary>
public partial class MainWindow
{
    void InitAi()
    {
        Editor.TextArea.SelectionChanged += (_, _) => UpdateAiContext();
        AiBarItem.IsChecked = settings.AiBarVisible;
    }

    void UpdateAiBar()
    {
        AiBar.Visibility = settings.AiBarVisible && current != null && review == null ? Visibility.Visible : Visibility.Collapsed;
        AiBarItem.IsChecked = settings.AiBarVisible;
        AiSendCommentsItem.IsEnabled = current != null && comments.OpenCount > 0;
        AiSendCommentsItem.Header = comments.OpenCount > 0 ? $"Envoyer les commentaires à Claude ({comments.OpenCount})" : "Envoyer les commentaires à Claude";
        UpdateAiContext();
    }

    void UpdateAiContext()
    {
        AiSendButton.IsEnabled = current != null && review == null && AiBox.Text.Trim().Length > 0;
        if (current == null) { AiContext.Text = ""; return; }
        if (Editor.SelectionLength > 0)
        {
            var s = Editor.SelectedText.Replace("\r", " ").Replace("\n", " ").Trim();
            AiContext.Text = "Sélection : « " + (s.Length > 48 ? s[..48].TrimEnd() + "…" : s) + " »";
        }
        else AiContext.Text = "Tout le document";
    }

    void AiTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        AiPlaceholder.Visibility = AiBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiContext();
    }

    void AiKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { AiSendClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            // Maj+Entrée : retour à la ligne dans la demande.
            int at = AiBox.CaretIndex;
            AiBox.Text = AiBox.Text.Insert(at, "\n");
            AiBox.CaretIndex = at + 1;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { Editor.TextArea.Focus(); e.Handled = true; }
    }

    void AiPlusClick(object sender, RoutedEventArgs e)
    {
        var menu = AiPlusButton.ContextMenu;
        menu.PlacementTarget = AiPlusButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    void AiBarToggleClick(object sender, RoutedEventArgs e)
    {
        settings.AiBarVisible = !settings.AiBarVisible;
        UpdateAiBar();
        if (settings.AiBarVisible) AiBox.Focus(); else Editor.TextArea.Focus();
    }

    void CommentClick(object sender, RoutedEventArgs e) => StartComment();

    void AiSendClick(object sender, RoutedEventArgs e)
    {
        if (current == null || review != null || AiBox.Text.Trim().Length == 0) return;
        DeliverAiRequest(prompt =>
        {
            Clipboard.SetText(prompt);
            SaveState.Text = "Demande copiée : collez-la dans Claude.";
        });
    }

    void AiSaveClick(object sender, RoutedEventArgs e)
    {
        if (current == null || root == null) return;
        if (AiBox.Text.Trim().Length == 0) { SaveState.Text = "Écrivez d'abord votre demande."; AiBox.Focus(); return; }
        var path = current.Path;
        var binder = root;
        DeliverAiRequest(prompt =>
        {
            var file = CommentSession.PromptFile(path, binder, DateTime.Now);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            DocumentFile.WriteAtomic(file, prompt);
            SaveState.Text = "Demande enregistrée dans .ecritoire\\prompts";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); } catch (Exception ex) { App.Log(ex); }
        });
    }

    /// <summary>La demande est livrée, un instantané est gardé, et le champ est vidé.</summary>
    void DeliverAiRequest(Action<string> deliver)
    {
        var prompt = AiPrompt(AiBox.Text.Trim());
        try { deliver(prompt); }
        catch (Exception ex) { App.Log(ex); SaveState.Text = "Envoi impossible : " + ex.Message; return; }
        TakeSnapshot();
        AiBox.Clear();
        Editor.TextArea.Focus();
    }

    /// <summary>Prompt pour Claude, sur le modèle de celui des commentaires (CommentSession.ClaudePrompt).</summary>
    string AiPrompt(string request)
    {
        Flush();
        var body = Editor.Text;
        string? file = null;
        try { file = DocumentFile.ReadText(current!.Path); } catch (IOException) { }
        bool poem = poemDisplay && Poem.IsPoem(body);
        var title = current!.Title.Length == 0 ? Path.GetFileNameWithoutExtension(current.Path) : current.Title;
        var sb = new StringBuilder();
        sb.Append($"# Demande — « {title} »\n\n");
        sb.Append("Tu travailles sur un document Markdown d'Écritoire, mon outil d'écriture :\n");
        sb.Append($"`{current.Path}`\n\n");
        sb.Append("## Demande\n");
        sb.Append(request.Replace("\r\n", "\n")).Append("\n\n");
        int start = Editor.SelectionStart, length = Editor.SelectionLength;
        if (length > 0 && start + length <= body.Length)
        {
            var quote = body.Substring(start, length);
            var (p, s) = Comments.Context(start, length, body);
            sb.Append("## Passage visé\n");
            if (poem && Poem.Reference(start, length, body) is { } reference) sb.Append($"({reference})\n");
            sb.Append($"Contexte : …{p.Replace("\n", " ")}**{quote.Replace("\n", " ")}**{s.Replace("\n", " ")}…\n\n");
        }
        sb.Append("## Consignes\n");
        sb.Append("- Si la demande appelle des changements, modifie directement ce fichier ; sinon, réponds sans le toucher.\n");
        sb.Append("- Ne change que ce que la demande implique ; laisse tout le reste mot pour mot.\n");
        sb.Append("- Conserve l'en-tête YAML et la syntaxe : titres `#`, encadrés `> [!intro]`, citations `>`, surlignages `==…==` et `<mark class=\"…\">`, images `![[…]]`, liens `[[…]]`.\n");
        if (poem)
            sb.Append("- C'est un poème en versets : un verset = un paragraphe, précédé de sa position `[n]`. Garde ce format (une ligne vide entre deux versets) et renumérote si tu ajoutes, supprimes ou fusionnes des versets.\n");
        sb.Append("- N'invente ni faits ni citations. Si la demande est ambiguë, fais le choix le plus prudent et signale-le.\n");
        sb.Append("- Ne modifie pas le dossier caché `.ecritoire` (commentaires et instantanés).\n");
        sb.Append("- À la fin, résume en quelques lignes ce que tu as changé.\n\n");
        sb.Append("## Texte intégral actuel\n\n````markdown\n");
        sb.Append(file ?? body);
        sb.Append("\n````\n");
        return sb.ToString();
    }
}
