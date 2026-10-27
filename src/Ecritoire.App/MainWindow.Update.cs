using System.Windows;

namespace Ecritoire.App;

/// <summary>Mise à jour automatique (Updater) : bouton discret dans l'en-tête quand une version plus récente existe.</summary>
public partial class MainWindow
{
    Updater.Release? available;

    async void CheckForUpdate()
    {
        await Task.Delay(TimeSpan.FromSeconds(4)); // le classeur s'ouvre d'abord
        available = await Updater.CheckAsync();
        if (available == null) return;
        UpdateButton.Content = $"Mettre à jour ({available.Version})";
        UpdateButton.ToolTip = $"Écritoire {available.Version} est disponible (vous avez la {Updater.Current})."
            + (available.Notes.Length > 0 ? "\n\n" + (available.Notes.Length > 600 ? available.Notes[..600] + "…" : available.Notes) : "");
        UpdateButton.Visibility = Visibility.Visible;
    }

    async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (available is not { } release) return;
        if (MessageBox.Show(this, $"Installer Écritoire {release.Version} ?\n\nVos documents sont enregistrés, puis Écritoire se ferme et se rouvre une fois la mise à jour installée.",
                "Mise à jour", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        UpdateButton.IsEnabled = false;
        try
        {
            var installer = await Updater.DownloadAsync(release, new Progress<double>(p => UpdateButton.Content = $"Téléchargement… {p:P0}"));
            Flush();
            settings.Save();
            Updater.Install(installer);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = $"Mettre à jour ({release.Version})";
            MessageBox.Show(this, "La mise à jour n'a pas pu être téléchargée : " + ex.Message, "Mise à jour", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
