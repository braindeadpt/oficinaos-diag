# One-time setup: generate the EC P-256 keypair that signs releases.
#
#   .\tools\generate-release-key.ps1
#
# Writes the PRIVATE key to .\release-signing-key.pem (gitignored — keep a
# copy in a safe offline place and NEVER commit it) and prints:
#   - the PUBLIC key base64 to paste into UpdateChecker.cs
#   - where to register the private key (GitHub secret RELEASE_SIGNING_KEY)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$privPath = Join-Path $repo 'release-signing-key.pem'

if (Test-Path $privPath) {
    Write-Error "$privPath already exists — refusing to overwrite an existing signing key. Delete it manually if you really want to rotate."
}

$ec = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

$privPem = $ec.ExportPkcs8PrivateKeyPem()
$pubB64  = [Convert]::ToBase64String($ec.ExportSubjectPublicKeyInfo())
$ec.Dispose()

# Private key file must not be world-readable — restrict to the owner.
[System.IO.File]::WriteAllText($privPath, $privPem)
$acl = Get-Acl $privPath
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    [System.Security.Principal.WindowsIdentity]::GetCurrent().Name,
    'FullControl', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl $privPath $acl

Write-Host ""
Write-Host "=== PUBLIC key — paste into UpdateChecker.ReleaseSigningPublicKey ==="
Write-Host $pubB64
Write-Host ""
Write-Host "=== PRIVATE key — written to $privPath (owner-only ACL) ==="
Write-Host "1) Add its contents as the GitHub secret  RELEASE_SIGNING_KEY  in:"
Write-Host "   https://github.com/braindeadpt/oficinaos-diag/settings/secrets/actions"
Write-Host "2) Copy the .pem somewhere safe offline (password manager / USB vault)."
Write-Host "3) Never commit the .pem — it is gitignored."
