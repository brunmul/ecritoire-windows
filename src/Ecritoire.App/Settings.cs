using System.IO;
using System.Text.Json;

namespace Ecritoire.App;

/// <summary>Réglages de l'app (dernier classeur, zoom…), dans %APPDATA%\Ecritoire\reglages.json — jamais dans le classeur.</summary>
public sealed class Settings
{
    public string? LastBinder { get; set; }
    public Dictionary<string, string> LastDocument { get; set; } = new();
    public double Zoom { get; set; } = 1.0;
    public double SidebarWidth { get; set; } = 260;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ecritoire", "reglages.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) { App.Log(e); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { App.Log(e); }
    }
}
