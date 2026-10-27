using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Ecritoire.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Vérification automatique : --captures dossier --classeur chemin
        var args = e.Args;
        int c = Array.IndexOf(args, "--captures"), b = Array.IndexOf(args, "--classeur");
        if (c >= 0 && c + 1 < args.Length && b >= 0 && b + 1 < args.Length)
        {
            Ecritoire.App.MainWindow.ProbeFolder = Path.GetFullPath(args[c + 1]);
            Ecritoire.App.MainWindow.ProbeBinder = Path.GetFullPath(args[b + 1]);
        }
        DispatcherUnhandledException += (_, args) =>
        {
            // Une erreur ne doit jamais fermer l'app sans enregistrer : on la montre, on la note, on continue.
            Log(args.Exception);
            MessageBox.Show("Une erreur est survenue :\n\n" + args.Exception.Message, "Écritoire", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    public static void Log(Exception error)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ecritoire");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "erreurs.log"), $"{DateTime.Now:s} {error}\n\n");
        }
        catch { }
    }
}
