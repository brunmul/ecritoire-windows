using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Ecritoire.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
