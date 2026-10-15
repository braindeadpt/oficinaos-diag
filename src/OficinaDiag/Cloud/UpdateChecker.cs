using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OficinaDiag.Cloud;

/// <summary>
/// Checks GitHub Releases for a newer build and self-updates in place:
/// downloads the win-x64 zip, verifies its SHA-256 against the release's
/// SHA256SUMS asset — and, before any of that, verifies the ECDSA P-256
/// signature of SHA256SUMS against the public key embedded below — then a
/// staged PowerShell script waits for this process to exit, extracts over
/// the app dir, and relaunches.
///
/// AUTENTICIDADE (resolvido 2026-10): o SHA256SUMS só prova integridade se
/// vier assinado. A release.yml assina o ficheiro com a chave privada
/// RELEASE_SIGNING_KEY (GitHub secret + cópia offline do dono — nunca no
/// repo). Uma conta GitHub comprometida já não chega: sem a chave, o .sig
/// não valida e a atualização é recusada. Rotação: gerar novo par com
/// tools/generate-release-key.ps1, embutir a nova pública, e só então
/// trocar o secret — um exe antigo continua a validar com a chave que traz.
/// </summary>
public sealed class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    // O zip self-contained pesa ~100 MB — 20 s de timeout partia qualquer
    // ligação mais lenta que ~5 MB/s. Download próprio: 10 minutos.
    private static readonly HttpClient DownloadHttp = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    static UpdateChecker()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("oficinaos-diag");
        DownloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("oficinaos-diag");
    }

    public sealed record UpdateInfo(Version Version, string Tag, string ZipUrl, string ChecksumUrl, string SigUrl);

    /// <summary>Repo publicador fixo — os assets de atualização só podem vir daqui.</summary>
    public const string TrustedOwner = "braindeadpt";
    public const string TrustedRepo = "oficinaos-diag";

    /// <summary>
    /// Chave pública EC P-256 (SubjectPublicKeyInfo, base64) que assina o
    /// SHA256SUMS de cada release. A privada vive fora do repo (secret
    /// RELEASE_SIGNING_KEY no GitHub Actions + cópia offline do dono).
    /// Gerada com tools/generate-release-key.ps1.
    /// </summary>
    internal const string ReleaseSigningPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEi01EhWqaWUkR8auOG7WnVuS9oGlI4jxQaUCOmqrzPw7cYOaCP2RzZLSnYuoZ1j2axpucL9UU4gO/kd+guVletg==";

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
            var sig = urls.FirstOrDefault(u => u?.EndsWith("SHA256SUMS.sig") == true);
            // Fail closed: a release without checksums or signature is never applied.
            if (zip is null || sums is null || sig is null) return null;
            // Fail closed: assets fora do repo publicador fixo nunca são aplicados.
            if (!IsTrustedAssetUrl(zip, tag) || !IsTrustedAssetUrl(sums, tag) || !IsTrustedAssetUrl(sig, tag))
                return null;
            return new UpdateInfo(remote, tag, zip, sums, sig);
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
            if (!IsTrustedAssetUrl(info.ZipUrl, info.Tag)
                || !IsTrustedAssetUrl(info.ChecksumUrl, info.Tag)
                || !IsTrustedAssetUrl(info.SigUrl, info.Tag))
                return (false, "Origem da atualização não reconhecida — atualização recusada.");

            // ANTES de gastar 100 MB de download: a assinatura do SHA256SUMS
            // tem de validar contra a chave pública embutida. Sem assinatura
            // válida, quem controla o repo não controla a atualização.
            var sumsBytes = await Http.GetByteArrayAsync(info.ChecksumUrl);
            var sigB64 = (await Http.GetStringAsync(info.SigUrl)).Trim();
            if (!VerifySumsSignature(sumsBytes, sigB64))
                return (false, "Assinatura da release inválida — atualização recusada.");

            var zipPath = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-{info.Tag}.zip");

            // Stream to disk — the self-contained zip is 100+ MB and does not
            // belong in a byte[] on the large-object heap.
            using (var src = await DownloadHttp.GetStreamAsync(info.ZipUrl))
            using (var dst = File.Create(zipPath))
                await src.CopyToAsync(dst);

            var expected = ExpectedHash(sumsBytes, info.ZipUrl);
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
    /// Verifies the base64 ECDSA P-256 signature over the raw SHA256SUMS
    /// bytes against the embedded release public key. Any parse/verify
    /// problem returns false — updates fail closed, never open.
    /// </summary>
    internal static bool VerifySumsSignature(byte[] sumsBytes, string sigB64, string? publicKeyB64 = null)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(publicKeyB64 ?? ReleaseSigningPublicKey), out _);
            var sig = Convert.FromBase64String(sigB64);
            return ec.VerifyData(sumsBytes, sig, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }

    /// <summary>
    /// Returns the hash recorded in the (already signature-verified)
    /// SHA256SUMS payload for the downloaded zip's filename, or null when
    /// the file/line is absent.
    /// </summary>
    private static string? ExpectedHash(byte[] sumsBytes, string zipUrl)
    {
        var fileName = zipUrl.Split('/').Last();
        foreach (var line in System.Text.Encoding.UTF8.GetString(sumsBytes).Split('\n'))
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
