# One-time setup: generate the EC P-256 keypair that signs releases.
#
#   .\tools\generate-release-key.ps1
#
# Writes the PRIVATE key to .\release-signing-key.pem (gitignored — move it
# OUT of this machine to a vault/encrypted pen; the key must never live in
# CI or on a host that builds releases — see docs/RELEASE.md) and prints
# the PUBLIC key base64 to paste into UpdateChecker.cs.
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
Write-Host "1) Move it OFF this machine to an encrypted vault/USB — releases are"
Write-Host "   signed offline by tools/sign-release.ps1, never in CI."
Write-Host "2) Never commit the .pem — it is gitignored."
