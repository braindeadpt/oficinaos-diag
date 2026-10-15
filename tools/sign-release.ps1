# Assinatura OFFLINE de releases — corre na máquina do dono, NUNCA no CI.
#
# Fluxo (ver docs/RELEASE.md):
#   1. tag v* -> release.yml faz build+testes e publica um DRAFT com o zip
#      e o SHA256SUMS.txt (sem assinatura — o draft fica invisível para o
#      updater, que só olha para releases/latest publicadas).
#   2. Este script descarrega o SHA256SUMS.txt do draft, valida que a linha
#      "version <x.y.z>" corresponde ao tag (estamos a assinar "isto É a
#      versão X" — um SUMS antigo republicado num tag mais alto falha aqui
#      e no updater), assina com a PEM local e faz upload do .sig.
#   3. Só então publica o draft.
#
#   .\tools\sign-release.ps1 -Tag v0.3.0
#   .\tools\sign-release.ps1 -Tag v0.3.0 -KeyFile E:\cofre\release-key.pem
#
# A PEM vive FORA do repo e fora do CI (cofre/pen cifrada). Quem tiver push
# no repo consegue criar um draft, mas sem a chave não produz um .sig válido
# — e o updater falha fechado sem ele. Requer: pwsh 7+, gh autenticado.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$KeyFile,
    [string]$Repo = "braindeadpt/oficinaos-diag",
    # -NoPublish: testa o fluxo num draft (download→valida→assina→upload)
    # sem publicar — o draft continua invisível para o updater.
    [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "requires PowerShell 7+ (pwsh) — PEM import isn't available in Windows PowerShell 5.1."
}
if (-not $Tag.StartsWith('v')) { Write-Error "tag must look like v0.3.0 — got '$Tag'" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "gh CLI not found — needed to reach the draft release"
}

if (-not $KeyFile) {
    $candidates = @(
        (Join-Path $env:USERPROFILE 'oficinaos-release-key.pem'),
        (Join-Path $env:USERPROFILE 'release-signing-key.pem')
    )
    $KeyFile = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $KeyFile) { Write-Error "no -KeyFile and none of: $($candidates -join ', ')" }
}
if (-not (Test-Path $KeyFile)) { Write-Error "key file not found: $KeyFile" }
$KeyPem = [System.IO.File]::ReadAllText((Resolve-Path $KeyFile))

# --- draft release + assets via API (gh release download não serve drafts) ---
Write-Host "a ler a release $Tag ..."
$rel = gh api "repos/$Repo/releases/tags/$Tag" | ConvertFrom-Json
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

# --- assinar ---
$ec = [System.Security.Cryptography.ECDsa]::Create()
try {
    $ec.ImportFromPem($KeyPem)
    $bytes = [System.IO.File]::ReadAllBytes($sumsPath)
    $sig = $ec.SignData(
        $bytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $sigPath = "$sumsPath.sig"
    [System.IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig))
} finally {
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
