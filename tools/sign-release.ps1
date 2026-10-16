# Assinatura OFFLINE de releases — corre na máquina do dono, NUNCA no CI.
#
# Fluxo (ver docs/RELEASE.md):
#   1. tag v* -> release.yml faz build+testes e publica um DRAFT com o zip
#      e o SHA256SUMS.txt (sem assinatura — o draft fica invisível para o
#      updater, que só olha para releases/latest publicadas).
#   2. Este script descarrega o SHA256SUMS.txt do draft, valida que a linha
#      "version <x.y.z>" corresponde ao tag (estamos a assinar "isto É a
#      versão X" — um SUMS antigo republicado num tag mais alto falha aqui
#      e no updater) e que a linha "commit <sha>" corresponde ao commit do
#      tag (um tag movido depois do build selava o build errado), resolve o
#      commit do tag e recusa se não for ancestor de main (a chave offline
#      só sela builds que vieram de main), pede confirmação, assina com a
#      chave local e faz upload do .sig.
#   3. Só então publica o draft.
#
#   .\tools\sign-release.ps1 -Tag v0.3.0                  # chave via DPAPI, sem prompt
#   .\tools\sign-release.ps1 -Tag v0.3.0 -KeyFile E:\oficinaos-release-key.enc.pem
#   .\tools\sign-release.ps1 -Tag v0.3.0 -Confirm:$false  # salta a confirmação (automação)
#
# A chave vive FORA do repo e fora do CI, em duas formas criadas por
# tools/protect-release-key.ps1 (corre uma vez por máquina):
#   - %LOCALAPPDATA%\OficinaOS\release-key.dpapi — PKCS#8 protegido com
#     DPAPI/CurrentUser: só esta conta nesta máquina o abre, sem prompt.
#     É o caminho por omissão.
#   - .enc.pem cifrado por passphrase (backup, idealmente noutro disco ou
#     pen): com -KeyFile o script pede a passphrase (Read-Host
#     -AsSecureString) e decifra só em memória.
# Em NENHUM caminho a chave é escrita em disco em plaintext.
# Quem tiver push no repo consegue criar um draft, mas sem a chave não
# produz um .sig válido — e o updater falha fechado sem ele.
# Requer: pwsh 7+, gh autenticado, Windows (DPAPI no caminho por omissão).
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$KeyFile,
    [string]$Repo = "braindeadpt/oficinaos-diag",
    # -NoPublish: testa o fluxo num draft (download→valida→assina→upload)
    # sem publicar — o draft continua invisível para o updater.
    [switch]$NoPublish,
    # -Confirm:$false salta a confirmação "Assinar <tag> @ <sha>?" — para
    # automação. Por omissão a confirmação corre sempre.
    [bool]$Confirm = $true
)
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "requires PowerShell 7+ (pwsh) — PEM import isn't available in Windows PowerShell 5.1."
}
if (-not $Tag.StartsWith('v')) { Write-Error "tag must look like v0.3.0 — got '$Tag'" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "gh CLI not found — needed to reach the draft release"
}
if ($KeyFile -and -not (Test-Path $KeyFile)) {
    Write-Error "key file not found: $KeyFile"
}

# --- draft release + assets via API (gh release download não serve drafts;
# e /releases/tags/:tag não resolve drafts cujo git tag ainda não existe) ---
Write-Host "a ler a release $Tag ..."
$all = gh api "repos/$Repo/releases" | ConvertFrom-Json
# Se houver duplicados do tag (drafts manuais esquecidos), o draft ganha —
# publicar o que já está live é recusado a seguir de qualquer forma.
$stub = $all | Where-Object { $_.tag_name -eq $Tag -and $_.draft } | Select-Object -First 1
if (-not $stub) {
    $stub = $all | Where-Object { $_.tag_name -eq $Tag } | Select-Object -First 1
}
if (-not $stub) { Write-Error "release $Tag não encontrada — correu o tag push / release.yml?" }
# A lista vem sem assets completos — busca a release inteira por id.
$rel = gh api "repos/$Repo/releases/$($stub.id)" | ConvertFrom-Json
if (-not $rel.draft) { Write-Error "$Tag já está publicada — recuso re-assinar uma release live" }
$sums = $rel.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
if (-not $sums) { Write-Error "draft sem SHA256SUMS.txt — o release.yml correu completo?" }
if ($rel.assets | Where-Object { $_.name -eq 'SHA256SUMS.sig' }) {
    Write-Error "o draft já tem SHA256SUMS.sig — apaga-o primeiro se queres re-assinar"
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "oficinaos-sign-$Tag"
New-Item -ItemType Directory -Force $tmp | Out-Null
$sumsPath = Join-Path $tmp 'SHA256SUMS.txt'
gh api "repos/$Repo/releases/assets/$($sums.id)" -H 'Accept: application/octet-stream' > $sumsPath
if ($LASTEXITCODE -ne 0) { Write-Error "download do SHA256SUMS falhou" }

# Assinamos uma afirmação: "estes hashes SÃO a versão X". A linha version
# tem de bater no tag — senão assinávamos uma mentira (replay antigo).
$expectedVersion = $Tag.TrimStart('v')
$declared = Get-Content $sumsPath | Where-Object { $_ -match '^version\s+(\S+)' } |
    Select-Object -First 1
if (-not $declared -or $Matches[1] -ne $expectedVersion) {
    Write-Error "SHA256SUMS declara version '$($Matches[1])' mas o tag é '$Tag' — draft errado?"
}
Write-Host "version $expectedVersion confirmada no SUMS"

# O SUMS declara também o commit que gerou o build ("commit <sha>") — sem
# ele não há prova de que o draft corresponde ao tag, e aceitava-se um SUMS
# antigo republicado. Linha em falta = recusar, mesmo com version certa.
$commitLine = Get-Content $sumsPath | Where-Object { $_ -match '^commit\s+(\S+)' } |
    Select-Object -First 1
if (-not $commitLine -or $commitLine -notmatch '^commit\s+([0-9a-fA-F]{40})\s*$') {
    Write-Error "SHA256SUMS sem linha 'commit <sha-40>' — o draft não declara o commit que o construiu; recuso assinar"
}
$declaredCommit = $Matches[1].ToLowerInvariant()

# --- o que estamos a selar: commit do tag, e tem de vir de main ---------
# Um draft pode nascer de qualquer tag; a chave offline só deve assinar
# builds de commits que estão em main. Assinar às cegas um build de um
# branch qualquer transformava a PEM num selo de commits não revistos.
Write-Host "a resolver o commit de $Tag ..."
$refJson = gh api "repos/$Repo/git/ref/tags/$Tag"
if ($LASTEXITCODE -ne 0) {
    Write-Error "git ref tags/$Tag não existe — o tag foi pushed para o origin?"
}
$ref = $refJson | ConvertFrom-Json
if (-not $ref.object) { Write-Error "ref tags/$Tag respondeu sem objeto" }
$sha = $ref.object.sha
if ($ref.object.type -eq 'tag') {
    # tag anotada → desreferencia até ao commit
    $tagJson = gh api "repos/$Repo/git/tags/$sha"
    if ($LASTEXITCODE -ne 0) { Write-Error "tag anotada $sha ilegível" }
    $sha = ($tagJson | ConvertFrom-Json).object.sha
}
$commitJson = gh api "repos/$Repo/commits/$sha"
if ($LASTEXITCODE -ne 0) { Write-Error "commit $sha ilegível" }
$commit = $commitJson | ConvertFrom-Json
$author = $commit.commit.author.name
$msg = ($commit.commit.message -split "`n")[0]
Write-Host "commit do tag: $sha — $author — $msg"

# O draft foi construído DESTE commit — um tag criado num commit fora de
# main e depois movido para um commit de main passava no guard de
# ancestralidade mas selava o build errado. O SUMS prende o draft ao commit.
if ($declaredCommit -ne $sha.ToLowerInvariant()) {
    Write-Error "commit no SUMS:  $declaredCommit`ncommit do tag:  $sha`n— o tag foi movido depois do CI fazer o draft; recuso assinar um build que não é deste commit"
}

# ancestor de main? compare/<sha>...main descreve o HEAD (main) face à
# BASE (sha): "ahead" = main contém o sha e mais (ancestor ✓); "identical"
# = mesmo commit; "behind" = o tag tem commits que main não tem; e
# "diverged" = histórias separadas. Só os dois primeiros são assináveis.
$cmpJson = gh api "repos/$Repo/compare/$sha...main"
if ($LASTEXITCODE -ne 0) { Write-Error "compare $sha...main falhou — sem prova de ancestralidade não assino" }
$cmp = $cmpJson | ConvertFrom-Json
if ($cmp.status -ne 'identical' -and $cmp.status -ne 'ahead') {
    Write-Error "$sha não é ancestor de main (compare: $($cmp.status), behind_by $($cmp.behind_by)) — recuso assinar um build de um commit fora de main"
}

if ($Confirm) {
    $ans = Read-Host "Assinar $Tag @ $sha ? [s/N]"
    if ($ans -notmatch '^(s|sim|y|yes)$') { Write-Error "assinatura cancelada — nada foi carregado" }
}

# --- carregar a chave só agora, e nunca em plaintext para disco ---------
# Omissão: release-key.dpapi (DPAPI CurrentUser — abre sem prompt, só
# nesta conta/máquina). -KeyFile: .enc.pem pede a passphrase e decifra em
# memória; um PEM plaintext ainda é aceite por compatibilidade (pen antiga).
$ec = [System.Security.Cryptography.ECDsa]::Create()
$pkcs8 = $null; $prot = $null; $sig = $null; $pass = $null
try {
    if ($KeyFile) {
        $keyPem = [System.IO.File]::ReadAllText((Resolve-Path $KeyFile))
        if ($keyPem -match 'BEGIN ENCRYPTED PRIVATE KEY') {
            $sec = Read-Host "Passphrase de $KeyFile" -AsSecureString
            $pass = [System.Net.NetworkCredential]::new('k', $sec).Password
            $sec.Dispose()
            try {
                $ec.ImportFromEncryptedPem($keyPem, $pass)
            } catch {
                Write-Error "passphrase errada ou ficheiro corrompido: $KeyFile"
            }
            $pass = $null
        } else {
            $ec.ImportFromPem($keyPem)
        }
        $keyPem = $null
    } else {
        $dpapiFile = Join-Path $env:LOCALAPPDATA 'OficinaOS\release-key.dpapi'
        if (-not (Test-Path $dpapiFile)) {
            Write-Error "sem $dpapiFile — corre .\tools\protect-release-key.ps1 uma vez, ou passa -KeyFile <.enc.pem>"
        }
        $prot = [System.IO.File]::ReadAllBytes($dpapiFile)
        $pkcs8 = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $prot,
            [Text.Encoding]::UTF8.GetBytes('oficinaos-release'),
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $ec.ImportPkcs8PrivateKey($pkcs8, [ref]$null)
    }

    # --- assinar ---
    $bytes = [System.IO.File]::ReadAllBytes($sumsPath)
    $sig = $ec.SignData(
        $bytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $sigPath = "$sumsPath.sig"
    [System.IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig))
} finally {
    foreach ($b in @($pkcs8, $prot, $sig)) {
        if ($b) { [Array]::Clear($b, 0, $b.Length) }
    }
    $pass = $null
    $ec.Dispose()
}

# --- upload do .sig ao draft ---
$uploads = ($rel.upload_url -replace '\{\?.*$', '')
gh api --method POST "$uploads`?name=SHA256SUMS.sig" `
    -H 'Content-Type: application/octet-stream' --input $sigPath | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "upload do .sig falhou" }

# --- publicar ---
if ($NoPublish) {
    Write-Host ""
    Write-Host "$Tag assinada (draft, -NoPublish) — .sig no draft, nada publicado"
} else {
    gh api --method PATCH "repos/$Repo/releases/$($rel.id)" -F draft=false | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "publish falhou — .sig já está no draft, corrige e corre outra vez" }
    Write-Host ""
    Write-Host "$Tag assinada e publicada — updater passa a vê-la em releases/latest"
}

Remove-Item -Recurse -Force $tmp
