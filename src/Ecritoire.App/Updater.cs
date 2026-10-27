using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Ecritoire.App;

/// <summary>
/// Mise à jour automatique : au lancement, l'app regarde la dernière version publiée sur GitHub (releases du dépôt,
/// construites par GitHub Actions). Si elle est plus récente, un bouton le signale ; un clic télécharge
/// l'installateur Inno Setup, le lance sans questions, et l'app se ferme ; l'installateur la rouvre ensuite.
/// </summary>
public static class Updater
{
    /// <summary>Dépôt public dont les releases (sans Walbaum) sont le seul endroit où l'app cherche ses nouvelles versions.</summary>
    public const string Repository = "brunmul/ecritoire-windows";

    public sealed record Release(Version Version, string Tag, string InstallerUrl, string Notes);

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ecritoire-Windows/" + Current);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Dernière version publiée si elle est plus récente que celle-ci, sinon null (ou en cas d'erreur réseau).</summary>
    public static async Task<Release?> CheckAsync()
    {
        try
        {
            using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest");
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            string? url = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Contains("Installation", StringComparison.OrdinalIgnoreCase))
                    url = asset.GetProperty("browser_download_url").GetString();
            }
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            if (url == null || version <= Current) return null;
            return new Release(version, tag, url, notes);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null; // hors ligne ou GitHub indisponible : on réessaiera au prochain lancement
        }
    }

    /// <summary>Télécharge l'installateur dans le dossier temporaire.</summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress = null)
    {
        var file = Path.Combine(Path.GetTempPath(), $"Ecritoire-Installation-{release.Version}.exe");
        using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0, done = 0;
        var partial = file + ".partiel";
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        File.Move(partial, file, overwrite: true);
        return file;
    }

    /// <summary>Lance l'installateur sans questions ; il ferme Écritoire si besoin et la rouvre à la fin.</summary>
    public static void Install(string installer) =>
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
}
