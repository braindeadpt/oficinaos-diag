using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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
/// vier assinado — e a assinatura é OFFLINE: a release.yml publica só um
/// draft (zip + SHA256SUMS), e tools/sign-release.ps1 na máquina do dono
/// assina e publica. A chave privada nunca toca no CI — quem tiver push no
/// repo consegue forjar um draft, mas sem a chave não produz um .sig válido.
/// Rotação: tools/generate-release-key.ps1, embutir a nova pública, e só
/// então aposentar a antiga — um exe antigo valida com a chave que traz.
///
/// ANTI-DOWNGRADE: o SHA256SUMS declara "version <x.y.z>" — assinado,
/// prende a versão ao conteúdo. Exigimos version == tag e > local, senão
/// um SUMS velho podia ser republicado num tag mais alto (replay).
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
    /// Nome do exe da app: o updater procura-o no staging e no appDir. Tem de
    /// bater com o <c>&lt;AssemblyName&gt;</c> do csproj da app — o
    /// <c>PackageFilesTests</c> falha se divergirem.
    /// </summary>
    internal const string AppExeName = "OficinaDiag.exe";

    /// <summary>
    /// Chave pública EC P-256 (SubjectPublicKeyInfo, base64) que assina o
    /// SHA256SUMS de cada release. A privada vive num cofre offline do dono
    /// — nunca no repo nem no CI (rodada 2026-10-15: a primeira versão
    /// transitou por um GitHub secret durante a implementação inicial e foi
    /// aposentada antes de qualquer release a ter usado).
    /// Gerada com tools/generate-release-key.ps1.
    /// </summary>
    internal const string ReleaseSigningPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXWMC+Ch7bMRfIYBy3PamIFQ2euAADeNuvRPXbYi7dq3AqFVqk32XYNqWINniP2MB31pWTtmmGZhprg6giQT98A==";

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

    /// <summary>
    /// True quando a app corre empacotada (MSIX/Store). Aí o diretório do exe
    /// é read-only, o tar não consegue escrever por cima e a Política 10.2.1
    /// da Store proíbe auto-updaters externos — o updater tem de se calar.
    /// </summary>
    public static bool IsPackagedApp()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // Sem buffer só interessa o código: 15700 = APPMODEL_ERROR_NO_PACKAGE.
            uint length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch { return false; }
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);

    /// <summary>Latest published release, or null when we're current/unreachable.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current)
    {
        // MSIX/Store: não há atualização fora da Store — nem uma chamada de rede.
        if (IsPackagedApp()) return null;
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
        UpdateInfo info, string appDir, Version current)
    {
        try
        {
            // Defesa em profundidade — CheckAsync já filtra, mas o UpdateInfo é público.
            if (!IsTrustedAssetUrl(info.ZipUrl, info.Tag)
                || !IsTrustedAssetUrl(info.ChecksumUrl, info.Tag)
                || !IsTrustedAssetUrl(info.SigUrl, info.Tag))
                return (false, "Origem da atualização não reconhecida — atualização recusada.");

            // Segunda defesa do MSIX: mesmo que a deteção de pacote falhe,
            // gastar 100 MB para depois não conseguir escrever em appDir é pior
            // que recusar já.
            if (IsPackagedApp())
                return (false, "Instalação da Microsoft Store — a atualização chega pela Store.");
            if (!IsDirectoryWritable(appDir))
                return (false, "Pasta da aplicação sem permissão de escrita — atualiza manualmente.");

            // ANTES de gastar 100 MB de download: a assinatura do SHA256SUMS
            // tem de validar contra a chave pública embutida, e a versão que
            // o SUMS declara tem de bater no tag e ser mais nova que a local —
            // senão um SUMS antigo assinado podia ser republicado num tag
            // mais alto (replay) e esta máquina fazia downgrade silencioso.
            var sumsBytes = await Http.GetByteArrayAsync(info.ChecksumUrl);
            var sigB64 = (await Http.GetStringAsync(info.SigUrl)).Trim();
            if (ValidateSums(sumsBytes, sigB64, info.Tag, current) is { } err)
                return (false, err);

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
            var script = BuildUpdateScript(info.Tag, appDir, zipPath, logPath, Environment.ProcessId);
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
    /// Sonda de escrita antes de gastar 100 MB: em C:\Program Files\WindowsApps
    /// (ou noutra pasta protegida) o download acabava sempre em falha.
    /// </summary>
    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".oficinaos-write-{Guid.NewGuid():N}.tmp");
            using var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Script staged que substitui a app depois de ela sair. Separado do
    /// download para poder ser testado sem rede e, sobretudo, para garantir
    /// uma ordem: extrair e validar numa pasta temporária **antes** de tocar
    /// em appDir. Um tar falhado a meio deixava a instalação híbrida.
    /// </summary>
    internal static string BuildUpdateScript(
        string tag, string appDir, string zipPath, string logPath, int processId)
    {
        var stage = Path.Combine(Path.GetTempPath(), $"oficinaos-stage-{tag}-{Guid.NewGuid():N}");
        var exePath = Path.Combine(appDir, AppExeName);
        var adbPath = Path.Combine(appDir, "tools", "platform-tools", "adb.exe");
        return string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Continue'",
            $"$log = '{Psq(logPath)}'",
            $"\"update {Psq(tag)} @ $(Get-Date -Format o)\" | Out-File $log",
            $"$appDir = '{Psq(appDir)}'",
            $"$exe = '{Psq(exePath)}'",
            $"$zip = '{Psq(zipPath)}'",
            $"$stage = '{Psq(stage)}'",
            $"$adb = '{Psq(adbPath)}'",
            // 1) Esperar pelo PID real. O Sleep fixo de 3 s deixava o exe
            //    bloqueado e o tar falhava a meio da atualização.
            $"Wait-Process -Id {processId} -Timeout 30 -ErrorAction SilentlyContinue",
            // 2) adb/idevice seguram .exe dentro de appDir — libertar antes.
            "if (Test-Path $adb) { & $adb kill-server 2>$null | Out-Null }",
            "Stop-Process -Name adb,idevicediagnostics,idevicecrashreport -Force -ErrorAction SilentlyContinue",
            // 3) Extrair para staging limpo: appDir só é tocado depois de o
            //    zip provar que traz o exe da app (AppExeName).
            "New-Item -ItemType Directory -Force $stage | Out-Null",
            "tar -xf $zip -C $stage 2>&1 | Out-File $log -Append",
            "\"extract exit=$LASTEXITCODE\" | Out-File $log -Append",
            $"if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $stage '{AppExeName}'))) {{",
            "  'extract failed — nada em appDir foi alterado' | Out-File $log -Append",
            "  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue",
            "  Start-Process $exe",
            "  exit 1",
            "}",
            // 4) ZipSlip: symlink/junction ou caminho que resolva fora do
            //    staging aborta antes de qualquer escrita em appDir.
            "$root = [IO.Path]::GetFullPath($stage).TrimEnd('\\') + '\\'",
            "$escaped = Get-ChildItem $stage -Recurse -Force | Where-Object {",
            // ReparsePoint (e não .LinkType): o updater corre no powershell.exe 5.1.
            "  ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or -not [IO.Path]::GetFullPath($_.FullName).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)",
            "}",
            "if ($escaped) {",
            "  'zip com entradas fora do staging — atualização recusada' | Out-File $log -Append",
            "  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue",
            "  Start-Process $exe",
            "  exit 1",
            "}",
            // 5) Só agora: backup do exe, cópia do staging e rollback no catch.
            "try {",
            "  Copy-Item $exe \"$exe.bak\" -Force -ErrorAction Stop",
            "  Copy-Item (Join-Path $stage '*') $appDir -Recurse -Force -ErrorAction Stop",
            "} catch {",
            "  \"copy failed: $_ — a restaurar o build anterior\" | Out-File $log -Append",
            "  if (Test-Path \"$exe.bak\") { Copy-Item \"$exe.bak\" $exe -Force -ErrorAction SilentlyContinue }",
            "  Start-Process $exe",
            "  Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue",
            "  exit 1",
            "}",
            "Start-Process $exe",
            "Remove-Item \"$exe.bak\" -Force -ErrorAction SilentlyContinue",
            "Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue",
            "Remove-Item $zip -Force -ErrorAction SilentlyContinue",
            "Remove-Item $MyInvocation.MyCommand.Path -ErrorAction SilentlyContinue",
        });
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
    /// Validação completa do SHA256SUMS antes do download: assinatura EC
    /// P-256 válida E a linha "version &lt;x.y.z&gt;" (coberta pela assinatura)
    /// igual ao tag e mais recente que a versão local. Devolve null quando
    /// está tudo bem, ou a mensagem de erro a mostrar — fail closed sempre.
    /// </summary>
    internal static string? ValidateSums(
        byte[] sumsBytes, string sigB64, string tag, Version current, string? publicKeyB64 = null)
    {
        if (!VerifySumsSignature(sumsBytes, sigB64, publicKeyB64))
            return "Assinatura da release inválida — atualização recusada.";
        if (!Version.TryParse(tag.TrimStart('v'), out var tagVer))
            return "Tag da release inválida — atualização recusada.";
        var declared = DeclaredVersion(sumsBytes);
        if (declared is null || declared != tagVer)
            return "A release não declara a versão do tag — atualização recusada.";
        if (declared <= current)
            return "A release não é mais recente — atualização recusada.";
        return null;
    }

    /// <summary>Versão declarada na linha "version &lt;x.y.z&gt;" do SHA256SUMS.</summary>
    internal static Version? DeclaredVersion(byte[] sumsBytes)
    {
        foreach (var line in System.Text.Encoding.UTF8.GetString(sumsBytes).Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "version"
                && Version.TryParse(parts[1], out var v))
                return v;
        }
        return null;
    }

    /// <summary>
    /// Returns the hash recorded in the (already signature-verified)
    /// SHA256SUMS payload for the downloaded zip's filename, or null when
    /// the file/line is absent. Linhas de metadados assinados ("version",
    /// "commit") não são entradas de ficheiro — e a ausência delas (SUMS de
    /// antes de as existirem) também é aceite.
    /// </summary>
    internal static string? ExpectedHash(byte[] sumsBytes, string zipUrl)
    {
        var fileName = zipUrl.Split('/').Last();
        foreach (var line in System.Text.Encoding.UTF8.GetString(sumsBytes).Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0] is "version" or "commit") continue; // metadados, não ficheiros
            if (parts[^1].TrimStart('*') == fileName)
                return parts[0];
        }
        return null;
    }

    /// <summary>Escapes a string for a single-quoted PowerShell literal.</summary>
    private static string Psq(string value) => value.Replace("'", "''");
}
