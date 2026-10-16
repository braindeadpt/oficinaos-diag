# Proteção única da chave de assinatura — corre UMA VEZ na máquina do dono
# (e outra vez num PC novo, para recuperação). NUNCA no CI.
#
# Lê uma chave cifrada (.enc.pem — "ENCRYPTED PRIVATE KEY"), pede a
# passphrase com Read-Host -AsSecureString e decifra-a SÓ em memória.
# Depois grava a chave em duas formas:
#
#   a) %LOCALAPPDATA%\OficinaOS\release-key.dpapi — os bytes PKCS#8
#      protegidos com DPAPI (ProtectedData, scope CurrentUser, entropy
#      fixa "oficinaos-release"). Só esta conta de utilizador, nesta
#      máquina, consegue desfazer o Protect — é o que deixa o
#      sign-release.ps1 assinar sem pedir nada. ACL: só o utilizador.
#
#   b) E:\oficinaos-release-key.enc.pem (ou -BackupPem) — backup da mesma
#      chave re-cifrada com uma passphrase NOVA, pedida duas vezes sem
#      eco. PBKDF2-SHA256 com 600 000 iterações + AES-256. É o que abre a
#      chave noutro PC: copia-se para lá e corre-se este script outra vez
#      com -SourcePem apontado para a cópia.
#
# No fim apaga a cópia solta em C:\Users (a passphrase antiga deixa de
# ter uso) e confirma que a pública derivada dos DOIS ficheiros bate na
# pública embutida no UpdateChecker — se não bater, algo correu mal e
# nada é apagado.
#
# Passphrases: pedidas com -AsSecureString, nunca impressas, nunca em
# logs, nunca em argumentos de linha de comando.
#
#   .\tools\protect-release-key.ps1
#   .\tools\protect-release-key.ps1 -SourcePem E:\oficinaos-release-key.enc.pem   # PC novo
#
# Requer: pwsh 7+, Windows (DPAPI).
param(
    # .enc.pem actual (a "versão antiga" em C:\Users por omissão; num PC
    # novo aponta-se para a cópia de backup vinda da pen/disco E:).
    [string]$SourcePem = (Join-Path $env:USERPROFILE 'oficinaos-release-key.enc.pem'),
    # Onde gravar o backup re-cifrado — idealmente um disco/pen separado.
    [string]$BackupPem = 'E:\oficinaos-release-key.enc.pem',
    [string]$DpapiFile = (Join-Path $env:LOCALAPPDATA 'OficinaOS\release-key.dpapi')
)
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "requires PowerShell 7+ (pwsh)."
}
if (-not (Test-Path $SourcePem)) { Write-Error "ficheiro não encontrado: $SourcePem" }
if ((Get-Content $SourcePem -TotalCount 1) -notmatch 'BEGIN ENCRYPTED PRIVATE KEY') {
    Write-Error "$SourcePem não é uma chave cifrada (esperava 'ENCRYPTED PRIVATE KEY') — corre primeiro o fluxo antigo de cofre"
}

# --- pública embutida: a verdade que os dois ficheiros têm de provar ---
$checker = Join-Path $PSScriptRoot '..\src\OficinaDiag\Cloud\UpdateChecker.cs'
$m = [regex]::Match([IO.File]::ReadAllText($checker), 'ReleaseSigningPublicKey\s*=\s*"([^"]+)"')
if (-not $m.Success) { Write-Error "ReleaseSigningPublicKey não encontrada em $checker" }
$embedded = $m.Groups[1].Value

$entropy = [Text.Encoding]::UTF8.GetBytes('oficinaos-release')
$pkcs8 = $null; $prot = $null; $encBytes = $null
$oldPass = $null; $newPass = $null; $newPass2 = $null
$ec = [System.Security.Cryptography.ECDsa]::Create()
try {
    # --- 1. decifrar a fonte, só em memória ---
    $sec = Read-Host "Passphrase de $SourcePem" -AsSecureString
    $oldPass = [System.Net.NetworkCredential]::new('k', $sec).Password
    $sec.Dispose()
    try {
        $ec.ImportFromEncryptedPem([IO.File]::ReadAllText($SourcePem), $oldPass)
    } catch {
        Write-Error "passphrase errada ou ficheiro corrompido: $SourcePem"
    }
    $oldPass = $null
    $pkcs8 = $ec.ExportPkcs8PrivateKey()
    $pub = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())
    if ($pub -ne $embedded) {
        Write-Error "a pública derivada de $SourcePem não é a pública embutida no UpdateChecker — chave errada? nada foi gravado"
    }

    # --- 2a. DPAPI: Protect(scope CurrentUser, entropy fixa) + ACL owner-only ---
    $prot = [System.Security.Cryptography.ProtectedData]::Protect(
        $pkcs8, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    New-Item -ItemType Directory -Force (Split-Path $DpapiFile -Parent) | Out-Null
    [IO.File]::WriteAllBytes($DpapiFile, $prot)
    # ACL só para este utilizador — icacls em vez de Set-Acl (Set-Acl pede
    # SeSecurityPrivilege quando o ficheiro já tem ACL endurecida).
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls $DpapiFile /inheritance:r /grant:r "*${sid}:F" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Error "icacls falhou em $DpapiFile" }
    Write-Host "gravada: $DpapiFile (DPAPI, só este utilizador nesta máquina)"

    # --- 2b. backup re-cifrado com passphrase NOVA (duas vezes, sem eco) ---
    $s1 = Read-Host "Passphrase NOVA para o backup $BackupPem" -AsSecureString
    $s2 = Read-Host "Repete a passphrase NOVA" -AsSecureString
    $newPass = [System.Net.NetworkCredential]::new('k', $s1).Password
    $newPass2 = [System.Net.NetworkCredential]::new('k', $s2).Password
    $s1.Dispose(); $s2.Dispose()
    if ($newPass -cne $newPass2 -or $newPass.Length -lt 12) {
        Write-Error "passphrases diferentes (ou < 12 caracteres) — nada foi re-cifrado"
    }
    $newPass2 = $null
    $encBytes = $ec.ExportEncryptedPkcs8PrivateKey($newPass,
        [System.Security.Cryptography.PbeParameters]::new(
            [System.Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256, 600000))
    New-Item -ItemType Directory -Force (Split-Path $BackupPem -Parent) | Out-Null
    [IO.File]::WriteAllText($BackupPem,
        [System.Security.Cryptography.PemEncoding]::WriteString('ENCRYPTED PRIVATE KEY', $encBytes))
    Write-Host "gravado: $BackupPem (cifrado com a passphrase nova)"

    # --- 3. verificação: os DOIS ficheiros derivam a pública embutida ---
    $ec2 = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $un = [System.Security.Cryptography.ProtectedData]::Unprotect(
            [IO.File]::ReadAllBytes($DpapiFile), $entropy,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        $n = 0
        $ec2.ImportPkcs8PrivateKey($un, [ref]$n)
        [Array]::Clear($un, 0, $un.Length)
        $pubDpapi = [Convert]::ToBase64String($ec2.ExportSubjectPublicKeyInfo())
        $ec2.ImportFromEncryptedPem([IO.File]::ReadAllText($BackupPem), $newPass)
        $pubBackup = [Convert]::ToBase64String($ec2.ExportSubjectPublicKeyInfo())
    } finally { $ec2.Dispose() }
    if ($pubDpapi -ne $embedded -or $pubBackup -ne $embedded) {
        Write-Error "verificação falhou — a pública derivada dos ficheiros gravados não bate na embutida; NÃO apago a fonte, corrige e corre outra vez"
    }

    # --- 4. apagar a cópia solta antiga (nunca o próprio backup) ---
    if ([IO.Path]::GetFullPath($SourcePem) -ne [IO.Path]::GetFullPath($BackupPem)) {
        Remove-Item $SourcePem -Force
        Write-Host "removida a cópia antiga: $SourcePem"
    }
    Write-Host ""
    Write-Host "chave protegida — sign-release.ps1 já assina sem prompt (DPAPI), ou com -KeyFile $BackupPem + passphrase"
} finally {
    foreach ($b in @($pkcs8, $prot, $encBytes)) {
        if ($b) { [Array]::Clear($b, 0, $b.Length) }
    }
    $oldPass = $null; $newPass = $null; $newPass2 = $null
    $ec.Dispose()
}
