using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OficinaDiag.Cloud;

/// <summary>
/// Checks GitHub Releases for a newer build and self-updates in place:
/// downloads the win-x64 zip, verifies its SHA-256 against the release's
/// SHA256SUMS asset, then a staged PowerShell script waits for this process
/// to exit, extracts over the app dir, and relaunches.
///
/// LIMITAÇÃO DE SEGURANÇA (revisão 2026-10): o SHA256SUMS vem da MESMA
/// release que o zip, por isso só prova INTEGRIDADE (download completo e
/// não corrompido), não AUTENTICIDADE. Quem comprometer a conta GitHub ou o
/// workflow de release publica um zip e um checksum coerentes, e o updater
/// executa-os. O que fazemos aqui: fixar o publicador — só aceitamos assets
/// servidos de https://github.com/braindeadpt/oficinaos-diag/releases/download/&lt;tag&gt;/.
/// Isso trava URLs manipulados/redirecionados para outro repo, mas não um
/// repo comprometido.
///
/// TODO(security): verificar a assinatura do SHA256SUMS com uma chave pública
/// embutida no exe (minisign / Ed25519; a chave privada fica fora do GitHub,
/// ex. num cofre offline) — ou assinar o exe com Authenticode / Azure Trusted
/// Signing e validar o certificado antes de aplicar (resolve também o
/// SmartScreen). Ver o PR fix/review-2026-10 para o plano.
/// </summary>
public sealed class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("oficinaos-diag");
    }

    public sealed record UpdateInfo(Version Version, string Tag, string ZipUrl, string ChecksumUrl);

    /// <summary>Repo publicador fixo — os assets de atualização só podem vir daqui.</summary>
    public const string TrustedOwner = "braindeadpt";
    public const string TrustedRepo = "oficinaos-diag";

    /// <summary>
    /// True só para <c>https://github.com/braindeadpt/oficinaos-diag/releases/download/&lt;tag&gt;/&lt;ficheiro&gt;</c>
    /// (o <c>browser_download_url</c> normal do GitHub). O redirecionamento
    /// posterior para o CDN do GitHub é feito pelo próprio github.com.
    /// </summary>
    public static bool IsTrustedAssetUrl(string? url, string tag)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(tag)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort) return false;
        if (!string.Equals(u.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return false;
        var seg = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // owner/repo/releases/download/<tag>/<file>
        return seg.Length == 6
            && string.Equals(seg[0], TrustedOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(seg[1], TrustedRepo, StringComparison.OrdinalIgnoreCase)
            && seg[2] == "releases" && seg[3] == "download"
            && Uri.UnescapeDataString(seg[4]) == tag
            && seg[5].Length > 0 && !seg[5].Contains("..");
    }

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
            var urls = doc.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .Where(u => u is not null)
                .ToList();
            var zip = urls.FirstOrDefault(u => u?.EndsWith(".zip") == true);
            var sums = urls.FirstOrDefault(u => u?.EndsWith("SHA256SUMS.txt") == true);
            // Fail closed: a release without checksums is never applied.
            if (zip is null || sums is null) return null;
            // Fail closed: assets fora do repo publicador fixo nunca são aplicados.
            if (!IsTrustedAssetUrl(zip, tag) || !IsTrustedAssetUrl(sums, tag)) return null;
            return new UpdateInfo(remote, tag, zip, sums);
        }
        catch { return null; } // offline or rate-limited — never block startup
    }

    /// <summary>
    /// Downloads the zip, verifies it against the release's SHA256SUMS asset,
    /// and stages a PowerShell updater that extracts over
    /// <paramref name="appDir"/> once this process exits. Caller should quit
    /// right after a true return.
    /// </summary>
    public static async Task<(bool ok, string message)> DownloadAndStageAsync(
        UpdateInfo info, string appDir)
    {
        try
        {
            // Defesa em profundidade — CheckAsync já filtra, mas o UpdateInfo é público.
            if (!IsTrustedAssetUrl(info.ZipUrl, info.Tag) || !IsTrustedAssetUrl(info.ChecksumUrl, info.Tag))
                return (false, "Origem da atualização não reconhecida — atualização recusada.");
            var zipPath = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-{info.Tag}.zip");

            // Stream to disk — the self-contained zip is 100+ MB and does not
            // belong in a byte[] on the large-object heap.
            using (var src = await Http.GetStreamAsync(info.ZipUrl))
            using (var dst = File.Create(zipPath))
                await src.CopyToAsync(dst);

            var expected = await ExpectedHashAsync(info);
            string actual;
            await using (var fs = File.OpenRead(zipPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            if (expected is null)
                return (false, "A release não traz checksum — atualização recusada.");
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zipPath);
                return (false, "Checksum do download não confere — atualização cancelada.");
            }

            // UTF-8 BOM so Windows PowerShell 5.1 reads accented paths correctly.
            // Single-quoted literals escaped via '' — a path with an apostrophe
            // must not break (or inject into) the staged script.
            var ps1Path = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-update-{info.Tag}.ps1");
            var logPath = Path.Combine(Path.GetTempPath(), "oficinaos-diag-update.log");
            var exePath = Path.Combine(appDir, "OficinaDiag.exe");
            var script = string.Join(Environment.NewLine, new[]
            {
                "Start-Sleep -Seconds 3",
                $"$log = '{Psq(logPath)}'",
                $"\"update {Psq(info.Tag)} @ $(Get-Date -Format o)\" | Out-File $log",
                $"$exe = '{Psq(exePath)}'",
                "Copy-Item $exe \"$exe.bak\" -Force -ErrorAction SilentlyContinue",
                $"tar -xf '{Psq(zipPath)}' -C '{Psq(appDir)}' 2>&1 | Out-File $log -Append",
                "\"exit=$LASTEXITCODE\" | Out-File $log -Append",
                "if ($LASTEXITCODE -ne 0) {",
                "  \"extract failed — restoring previous build\" | Out-File $log -Append",
                "  Copy-Item \"$exe.bak\" $exe -Force -ErrorAction SilentlyContinue",
                "}",
                "Start-Process $exe",
                "Remove-Item \"$exe.bak\" -ErrorAction SilentlyContinue",
                $"Remove-Item '{Psq(zipPath)}' -ErrorAction SilentlyContinue",
                "Remove-Item $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue",
            });
            await File.WriteAllTextAsync(ps1Path, script, new System.Text.UTF8Encoding(true));

            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return proc is null
                ? (false, "Não consegui arrancar o updater — atualiza manualmente.")
                : (true, "A atualizar — a app reinicia já.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Fetches the SHA256SUMS release asset and returns the hash recorded for
    /// the downloaded zip's filename, or null when the file/line is absent.
    /// </summary>
    private static async Task<string?> ExpectedHashAsync(UpdateInfo info)
    {
        var sums = await Http.GetStringAsync(info.ChecksumUrl);
        var fileName = info.ZipUrl.Split('/').Last();
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == fileName)
                return parts[0];
        }
        return null;
    }

    /// <summary>Escapes a string for a single-quoted PowerShell literal.</summary>
    private static string Psq(string value) => value.Replace("'", "''");
}
