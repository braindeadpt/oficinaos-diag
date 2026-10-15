# Sign a release asset (SHA256SUMS.txt) with the EC P-256 release key.
#
#   .\tools\sign-release.ps1 -File .\SHA256SUMS.txt -KeyFile .\release-signing-key.pem
#   $env:RELEASE_SIGNING_KEY = (Get-Content .\release-signing-key.pem -Raw)
#   .\tools\sign-release.ps1 -File .\SHA256SUMS.txt          # key from env var
#
# Writes <file>.sig next to the input (base64 of the P1363 r||s signature).
# CI uses this via the RELEASE_SIGNING_KEY secret; locally the .pem works.
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$KeyFile,
    [string]$KeyPem
)
$ErrorActionPreference = 'Stop'

# ImportFromPem precisa de .NET Core 3+ — Windows PowerShell 5.1 não chega.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    Write-Error "sign-release.ps1 requires PowerShell 7+ (pwsh) — PEM import isn't available in Windows PowerShell 5.1."
}

if (-not (Test-Path $File)) { Write-Error "file not found: $File" }

if (-not $KeyPem) {
    if ($KeyFile) {
        if (-not (Test-Path $KeyFile)) { Write-Error "key file not found: $KeyFile" }
        $KeyPem = [System.IO.File]::ReadAllText((Resolve-Path $KeyFile))
    } elseif ($env:RELEASE_SIGNING_KEY) {
        $KeyPem = $env:RELEASE_SIGNING_KEY
    } else {
        Write-Error "no key — pass -KeyFile/-KeyPem or set RELEASE_SIGNING_KEY"
    }
}

$ec = [System.Security.Cryptography.ECDsa]::Create()
try {
    $ec.ImportFromPem($KeyPem)
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $File))
    $sig = $ec.SignData(
        $bytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $out = "$File.sig"
    [System.IO.File]::WriteAllText($out, [Convert]::ToBase64String($sig))
    Write-Host "signed $File -> $out"
} finally {
    $ec.Dispose()
}
