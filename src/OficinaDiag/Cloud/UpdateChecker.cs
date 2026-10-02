using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OficinaDiag.Cloud;

/// <summary>
/// Checks GitHub Releases for a newer build and self-updates in place:
/// downloads the win-x64 zip, then a staged PowerShell script waits for
/// this process to exit, extracts over the app dir, and relaunches.
/// </summary>
public sealed class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("oficinaos-diag");
    }

    public sealed record UpdateInfo(Version Version, string Tag, string ZipUrl);

    /// <summary>Latest published release, or null when we're current/unreachable.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(
                "https://api.github.com/repos/braindeadpt/oficinaos-diag/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var remote) || remote <= current)
                return null;
            var zip = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(u => u?.EndsWith(".zip") == true);
            return zip is null ? null : new UpdateInfo(remote, tag, zip);
        }
        catch { return null; } // offline or rate-limited — never block startup
    }

    /// <summary>
    /// Downloads the zip and stages a PowerShell updater that extracts over
    /// <paramref name="appDir"/> once this process exits. Caller should quit
    /// right after a true return.
    /// </summary>
    public static async Task<(bool ok, string message)> DownloadAndStageAsync(
        UpdateInfo info, string appDir)
    {
        try
        {
            var zipPath = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-{info.Tag}.zip");
            await File.WriteAllBytesAsync(zipPath, await Http.GetByteArrayAsync(info.ZipUrl));

            // UTF-8 BOM so Windows PowerShell 5.1 reads accented paths correctly.
            var ps1Path = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-update-{info.Tag}.ps1");
            var script = string.Join(Environment.NewLine, new[]
            {
                "Start-Sleep -Seconds 3",
                $"tar -xf '{zipPath}' -C '{appDir}'",
                $"Start-Process '{Path.Combine(appDir, "OficinaDiag.exe")}'",
                $"Remove-Item '{zipPath}' -ErrorAction SilentlyContinue",
                "Remove-Item $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue",
            });
            await File.WriteAllTextAsync(ps1Path, script, new System.Text.UTF8Encoding(true));

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return (true, "A atualizar — a app reinicia já.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
