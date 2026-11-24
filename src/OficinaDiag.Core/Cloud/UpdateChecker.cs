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
    /// Chave pública EC P-256 (SubjectPublicKeyInfo, base64) que assina o
    /// SHA256SUMS de cada release. A privada vive num cofre offline do dono
    /// — nunca no repo nem no CI (rodada 2026-10-15: a primeira versão
    /// transitou por um GitHub secret durante a implementação inicial e foi
    /// aposentada antes de qualquer release a ter usado).
    /// Gerada com tools/generate-release-key.ps1.
    /// </summary>
    internal const string ReleaseSigningPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXWMC+Ch7bMRfIYBy3PamIFQ2euAADeNuvRPXbYi7dq3AqFVqk32XYNqWINniP2MB31pWTtmmGZhprg6giQT98A==";

    // --- Costuras da prova de instalação ------------------------------------
    //
    // A prova tem de correr o caminho que instala — descarregar, validar a
    // assinatura, conferir o checksum e estagiar — sem rede externa, sem
    // substituir a app e sem a chave privada do dono. Nada disto muda o
    // comportamento em produção: os valores por omissão são exactamente os de
    // antes, e um teste que os troque devolve-os no fim.

    /// <summary>
    /// Chave pública que valida o `SHA256SUMS`. A prova assina com uma chave de
    /// teste e injecta a pública correspondente; a app usa a embutida.
    /// </summary>
    internal static string ChavePublicaDaRelease = ReleaseSigningPublicKey;

    /// <summary>Transporte dos três pedidos da release. Injectável para o loopback.</summary>
    internal static Func<string, Task<byte[]>> LerSums = url => Http.GetByteArrayAsync(url);
    internal static Func<string, Task<string>> LerSig = url => Http.GetStringAsync(url);
    internal static Func<string, Task<Stream>> AbrirZip = url => DownloadHttp.GetStreamAsync(url);

    /// <summary>
    /// Último passo: lançar o PowerShell que substitui a app depois de esta
    /// sair. Injectável porque nenhuma prova pode substituir a app a sério.
    /// </summary>
    internal static Func<string, bool> ArrancarAtualizador = LancarAtualizador;

    /// <summary>
    /// Costura do harness: o UiShots corre sem rede para as capturas não
    /// dependerem do GitHub. Em produção fica sempre true.
    /// </summary>
    internal static bool VerificarAtualizacao = true;

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
        if (!VerificarAtualizacao) return null;
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
            var sumsBytes = await LerSums(info.ChecksumUrl);
            var sigB64 = (await LerSig(info.SigUrl)).Trim();
            if (ValidateSums(sumsBytes, sigB64, info.Tag, current, ChavePublicaDaRelease) is { } err)
                return (false, err);

            // O tag já passou por Version.TryParse, mas vai para um path —
            // defesa em profundidade contra metacaracteres (.., separadores).
            var safeTag = new string(info.Tag.Where(
                c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
            var zipPath = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-{safeTag}.zip");

            // Stream to disk — the self-contained zip is 100+ MB and does not
            // belong in a byte[] on the large-object heap.
            using (var src = await AbrirZip(info.ZipUrl))
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
            var ps1Path = Path.Combine(Path.GetTempPath(), $"oficinaos-diag-update-{safeTag}.ps1");
            var logPath = Path.Combine(Path.GetTempPath(), "oficinaos-diag-update.log");
            var script = BuildUpdateScript(info.Tag, appDir, zipPath, logPath, Environment.ProcessId);
            WriteUpdateScript(ps1Path, script);

            return ArrancarAtualizador(ps1Path)
                ? (true, "A atualizar — a app reinicia já.")
                : (false, "Não consegui arrancar o updater — atualiza manualmente.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>O valor por omissão de <see cref="ArrancarAtualizador"/>.</summary>
    private static bool LancarAtualizador(string ps1Path)
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1Path}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        return proc is not null;
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
    /// Marcador que o atualizador deixa em <c>appDir</c> quando a aplicação do
    /// staging falha. Vive dentro da instalação porque é ali que o arranque
    /// seguinte o vai procurar — o log fica em %TEMP% e ninguém o lê.
    /// </summary>
    internal const string UpdateFailureMarkerName = ".update-falhou";

    /// <summary>
    /// Texto do aviso da atualização que falhou, ou <c>null</c>. Consome-o:
    /// o marcador é apagado para o técnico o ver uma vez e não em cada
    /// arranque.
    /// </summary>
    internal static string? ConsumeUpdateFailure(string appDir)
    {
        try
        {
            var marcador = Path.Combine(appDir, UpdateFailureMarkerName);
            if (!File.Exists(marcador)) return null;
            var texto = File.ReadAllText(marcador).Trim().TrimStart('\uFEFF');
            File.Delete(marcador);
            return texto.Length == 0 ? null : texto;
        }
        catch { return null; }
    }

    /// <summary>
    /// Escreve o script com BOM. O <c>powershell.exe</c> 5.1 lê um ficheiro sem
    /// BOM como ANSI: o primeiro acento ou travessão parte o script com
    /// <c>ParserError</c> e o updater não faz nada. Um sítio só para a escrita,
    /// para a prova não divergir da produção.
    /// </summary>
    internal static void WriteUpdateScript(string path, string script)
        => File.WriteAllText(path, script, new System.Text.UTF8Encoding(true));

    /// <summary>
    /// Script staged que substitui a app depois de ela sair. Separado do
    /// download para poder ser testado sem rede e, sobretudo, para garantir
    /// uma ordem: extrair e validar numa pasta temporária **antes** de tocar
    /// em appDir. Um tar falhado a meio deixava a instalação híbrida.
    ///
    /// Cópias com <c>robocopy</c>, não <c>Copy-Item</c> (incidente #62,
    /// 2026-11-24): o Copy-Item do PowerShell 5.1 morre em caminhos >260
    /// chars — pastas de instalação profundas (dev <c>bin\Release</c>)
    /// falhavam sempre — e o <c>Remove-Item -Recurse</c> moía uma árvore
    /// <c>.bak</c> envenenada durante ~6 minutos antes do relançamento.
    /// Robocopy lida com long paths nativamente e os seus exit codes são
    /// bitmask: **&lt;8 é sucesso** (1=copiou, 2=extras, 4=mismatch), ≥8 é
    /// falha. <c>/R:0 /W:0</c> é obrigatório — o default do robocopy são
    /// 1M retries com 30 s de espera, e um ficheiro trancado (adb,
    /// antivírus) pendurava o updater durante dias.
    /// </summary>
    internal static string BuildUpdateScript(
        string tag, string appDir, string zipPath, string logPath, int processId)
    {
        var stage = Path.Combine(Path.GetTempPath(), $"oficinaos-stage-{tag}-{Guid.NewGuid():N}");
        var exePath = Path.Combine(appDir, PackagePaths.AppExeName);
        var adbPath = Path.Combine(appDir, PackagePaths.AdbRelativePath);
        // Pasta vazia para o truque `robocopy vazio alvo /MIR`: espelha o
        // nada sobre o alvo e apaga a árvore inteira — o Remove-Item do
        // PS5.1 não chega a caminhos >260 e era o que moía o .bak gigante.
        var emptyDir = Path.Combine(Path.GetTempPath(), $"oficinaos-vazio-{Guid.NewGuid():N}");
        return string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Continue'",
            $"$log = '{PsLiteral(logPath)}'",
            $"\"update {PsLiteral(tag)} @ $(Get-Date -Format o)\" | Out-File $log",
            $"$appDir = '{PsLiteral(appDir)}'",
            $"$exe = '{PsLiteral(exePath)}'",
            $"$zip = '{PsLiteral(zipPath)}'",
            $"$stage = '{PsLiteral(stage)}'",
            $"$adb = '{PsLiteral(adbPath)}'",
            $"$vazio = '{PsLiteral(emptyDir)}'",
            // O técnico abre a app e tem de saber que a atualização falhou: o
            // log vive em %TEMP% e ninguém o vai ler. O marcador é consumido
            // no arranque seguinte (ConsumeUpdateFailure) e apagado.
            $"$marcador = Join-Path $appDir '{UpdateFailureMarkerName}'",
            $"$bak = $appDir + '.bak-' + [Guid]::NewGuid().ToString('N')",
            // $backupOk separa os dois desfechos do catch: se o backup não
            // chegou a completar-se, appDir está intacto e um /MIR de um
            // backup parcial seria destrutivo (purgava o que faltava).
            "$backupOk = $false",
            "function Avisar-Falha([string] $porque) {",
            // Formato com aspas simples: o tag vem da release e não pode ser
            // expandido como expressão.
            $"  ('{{0}} atualização {{1}} falhou: {{2}} — continuas na versão anterior. Log: {{3}}' -f (Get-Date -Format o), '{PsLiteral(tag)}', $porque, $log) | Out-File $marcador -Encoding utf8",
            "}",
            // Apagar uma árvore sem limites de caminho: o /MIR de uma pasta
            // vazia esvazia o alvo e o Remove-Item tira a casca — ordem que
            // sobrevive a árvores mais fundas que o MAX_PATH do PS5.1.
            "function Limpar-Pasta([string] $alvo) {",
            "  if (Test-Path $alvo) {",
            // Se o robocopy não arrancar a limpeza não pode morrer aqui —
            // fica o Remove-Item, que ao menos tenta.
            "    try { & \"$env:SystemRoot\\System32\\robocopy.exe\" $vazio $alvo /MIR /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS | Out-Null } catch { }",
            "    Remove-Item $alvo -Recurse -Force -ErrorAction SilentlyContinue",
            "  }",
            "}",
            "New-Item -ItemType Directory -Force -Path $stage, $vazio | Out-Null",
            // 1) Esperar pelo processo real — pelo HANDLE e sem timeout.
            //    O diálogo «a app reinicia já» fica aberto o tempo que o
            //    técnico demorar a carregar em OK, e um Wait-Process com
            //    timeout avançava com a app viva: o /MIR falhava nos
            //    ficheiros trancados e cada Start-Process abaixo lançava uma
            //    instância que morria no mutex de instância única — a app
            //    terminava sem janela nenhuma. Get-Process confirma que o
            //    PID ainda é o nosso exe: um PID reutilizado por outro
            //    processo não nos faz esperar por ele (e se o PID já não
            //    existe, a app já saiu e não há nada a esperar).
            $"$alvo = Get-Process -Id {processId} -ErrorAction SilentlyContinue",
            $"if ($alvo -and $alvo.ProcessName -eq [IO.Path]::GetFileNameWithoutExtension($exe)) {{ try {{ $alvo.WaitForExit() }} catch {{ }} }}",
            // 2) Extrair para staging limpo: appDir só é tocado depois de o
            //    zip provar que traz o exe da app (PackagePaths.AppExeName).
            //    `tar` nu resolve para o GNU tar do Git (usr\bin à frente de
            //    System32 em máquinas de dev), que lê "C:" como host remoto —
            //    a extração falhava sempre e o update voltava a ser oferecido.
            "& \"$env:SystemRoot\\System32\\tar.exe\" -xf $zip -C $stage 2>&1 | Out-File $log -Append",
            "\"extract exit=$LASTEXITCODE\" | Out-File $log -Append",
            $"if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $stage '{PackagePaths.AppExeName}'))) {{",
            "  'extract failed — nada em appDir foi alterado' | Out-File $log -Append",
            "  Avisar-Falha 'o zip não extraiu'",
            "  Limpar-Pasta $stage",
            "  Remove-Item $vazio -Force -ErrorAction SilentlyContinue",
            "  Start-Process $exe",
            "  exit 1",
            "}",
            // 3) ZipSlip: symlink/junction ou caminho que resolva fora do
            //    staging aborta antes de qualquer escrita em appDir.
            "$root = [IO.Path]::GetFullPath($stage).TrimEnd('\\') + '\\'",
            "$escaped = Get-ChildItem $stage -Recurse -Force | Where-Object {",
            // ReparsePoint (e não .LinkType): o updater corre no powershell.exe 5.1.
            "  ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -or -not [IO.Path]::GetFullPath($_.FullName).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)",
            "}",
            "if ($escaped) {",
            "  'zip com entradas fora do staging — atualização recusada' | Out-File $log -Append",
            "  Avisar-Falha 'o zip trazia entradas fora do staging'",
            "  Limpar-Pasta $stage",
            "  Remove-Item $vazio -Force -ErrorAction SilentlyContinue",
            "  Start-Process $exe",
            "  exit 1",
            "}",
            // 4) Backup do build **inteiro**, aplicação do staging e rollback
            //    no catch — só agora, com o staging já provado. Copiar a
            //    árvore toda é o que permite repor tudo no rollback; antes
            //    era só o exe e uma falha a meio deixava exe velho com DLLs
            //    novas (instalação híbrida).
            //
            //    Inventário do que vive em appDir mas NÃO vem no zip
            //    (revisão #62): o único ficheiro do utilizador é
            //    `oficinaos-diag.config.json` — config portátil com tema,
            //    idioma, token Pro (DPAPI) e identidade da loja
            //    (DiagConfig.cs); o /XF nas três cópias impede o /MIR de o
            //    purgar — sem ele as preferências e o emparelhamento
            //    perdiam-se e o backup era apagado no sucesso. O histórico
            //    (history.jsonl), o diag.log e o outbox vivem em
            //    %APPDATA%\OficinaDiag, e os relatórios vão para a pasta que
            //    o técnico escolhe — nada disso entra em appDir.
            //    `tools/` É versionado com a app: fetch-tools.ps1 +
            //    package-files.txt põem scrcpy e platform-tools no zip, por
            //    isso o espelho tem de a sincronizar como qualquer outro
            //    ficheiro da app. `.update-falhou` é do próprio updater:
            //    purgá-lo numa aplicação bem-sucedida é o comportamento
            //    certo (o aviso já não se aplica).
            "try {",
            //    `.bak-*`/`*.bak-*` dentro de appDir são restos de updaters
            //    antigos (o veneno do #62: `.bak-X/.bak-X/...` ~400 níveis,
            //    ~947 MB) — nunca dados do utilizador, a pasta de instalação
            //    só tem ficheiros da app. Limpeza best-effort: se uma pasta
            //    resistir, o /XD das cópias seguintes impede-na de se
            //    propagar na mesma.
            "  Get-ChildItem $appDir -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*.bak-*' } | ForEach-Object { Limpar-Pasta $_.FullName }",
            //    Backup: /E copia a árvore toda (sem purge — $bak nasce
            //    vazio). /XD '*.bak-*' exclui restos de updater a qualquer
            //    profundidade — incluindo o próprio $bak, que nasce DENTRO
            //    de appDir (o appDir de produção termina em '\'): sem a
            //    exclusão o robocopy recopiava o destino para dentro de si
            //    mesmo, o veneno original do #62. /XF tira o config
            //    portátil do snapshot: é dado do utilizador, não do build.
            //    /XJ não segue junctions — sem ele um /MIR podia apagar
            //    fora da árvore. Exit <8 = sucesso.
            "  & \"$env:SystemRoot\\System32\\robocopy.exe\" $appDir $bak /E /XD '*.bak-*' /XF 'oficinaos-diag.config.json' /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS | Out-File $log -Append",
            "  \"backup exit=$LASTEXITCODE\" | Out-File $log -Append",
            "  if ($LASTEXITCODE -ge 8) { throw \"backup falhou (robocopy exit $LASTEXITCODE)\" }",
            "  $backupOk = $true",
            //    adb/idevice seguram .exe dentro de appDir — libertar antes
            //    da aplicação, que é a única fase que escreve. Só aqui e não
            //    antes da extração: uma release envenenada não pode deixar a
            //    app religada sem servidor adb (o scan seguinte dizia
            //    "nenhum dispositivo" — #62), e uma falha de backup também
            //    não o precisa de matar.
            "  if (Test-Path $adb) { & $adb kill-server 2>$null | Out-Null }",
            "  Stop-Process -Name adb,idevicediagnostics,idevicecrashreport -Force -ErrorAction SilentlyContinue",
            //    Aplicação: /MIR é o espelho verdadeiro — além de copiar,
            //    purga de appDir o que a versão nova já não traz (o
            //    Copy-Item deixava DLLs mortas para trás, outra forma de
            //    híbrido). Os /XD/'/XF'/'/XJ'/'/R:0'/'/W:0' repetem-se: o
            //    purge também os respeita — o /XF é o que poupa o config
            //    portátil e o .bak-* poupa o backup que vive dentro de
            //    appDir durante a aplicação.
            "  & \"$env:SystemRoot\\System32\\robocopy.exe\" $stage $appDir /MIR /XD '*.bak-*' /XF 'oficinaos-diag.config.json' /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS | Out-File $log -Append",
            "  \"apply exit=$LASTEXITCODE\" | Out-File $log -Append",
            "  if ($LASTEXITCODE -ge 8) { throw \"apply falhou (robocopy exit $LASTEXITCODE)\" }",
            "} catch {",
            "  \"copy failed: $_ — a restaurar o build anterior\" | Out-File $log -Append",
            // Rollback: /MIR do backup repõe a versão antiga E remove o que
            // a aplicação parcial trouxe de novo — mais completo que o
            // Copy-Item por cima, que deixava ficheiros novos para trás.
            // O /XF repete-se: sem ele o config portátil (extra face ao
            // backup, que também o exclui) era purgado na reposição.
            // Só corre quando o backup está completo: um /MIR de backup
            // parcial transformava appDir numa instalação truncada.
            "  if ($backupOk -and (Test-Path $bak)) {",
            "    & \"$env:SystemRoot\\System32\\robocopy.exe\" $bak $appDir /MIR /XD '*.bak-*' /XF 'oficinaos-diag.config.json' /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS | Out-Null",
            "  }",
            //    Repor **por cima**, e só depois tentar a troca limpa: o
            //    `Move-Item` para uma pasta que ainda existe move o backup para
            //    dentro dela e não repõe nada (apanhado pela prova), e um
            //    ficheiro em uso (adb, antivírus) impede apagar a pasta.
            "  if ($backupOk -and -not (Test-Path $exe)) {",
            "    Remove-Item $appDir -Recurse -Force -ErrorAction SilentlyContinue",
            "    Move-Item $bak $appDir -Force -ErrorAction SilentlyContinue",
            "  }",
            "  Limpar-Pasta $bak",
            // O aviso vem depois de o rollback correr: dentro de appDir, o
            // marcador escrito antes era apagado pelo /MIR da restauração.
            "  if ($backupOk) {",
            "    if (Test-Path $exe) {",
            "      Avisar-Falha 'a aplicação do staging falhou a meio'",
            "    } else {",
            "      Avisar-Falha 'a aplicação do staging falhou e o rollback não repôs o exe — repõe a instalação a partir do zip'",
            "    }",
            "  } else {",
            "    Avisar-Falha 'a preparação da atualização falhou — nada em appDir foi alterado'",
            "  }",
            // O adb foi morto antes das cópias: sem start-server a app
            // religada arrancava com o servidor em baixo e o scan seguinte
            // reportava «nenhum dispositivo» até um restart manual (#62).
            // start-server é idempotente — corre também no sucesso.
            "  if (Test-Path $adb) { & $adb start-server 2>$null | Out-Null }",
            "  if (Test-Path $exe) { Start-Process $exe }",
            "  Limpar-Pasta $stage",
            "  Remove-Item $zip -Force -ErrorAction SilentlyContinue",
            "  Remove-Item $vazio -Force -ErrorAction SilentlyContinue",
            "  exit 1",
            "}",
            "if (Test-Path $adb) { & $adb start-server 2>$null | Out-Null }",
            "Start-Process $exe",
            "Limpar-Pasta $bak",
            "Limpar-Pasta $stage",
            "Remove-Item $zip -Force -ErrorAction SilentlyContinue",
            "Remove-Item $vazio -Force -ErrorAction SilentlyContinue",
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

    /// <summary>
    /// Escapes a string for a single-quoted PowerShell literal. Internal
    /// porque a prova do UiShots monta literais com os mesmos caminhos —
    /// um checkout com apóstrofo (ex.: <c>C:\Users\Dj'Ávila</c>) partiria
    /// a prova se ela tivesse a sua própria regra de escape.
    /// </summary>
    internal static string PsLiteral(string value) => value.Replace("'", "''");
}
