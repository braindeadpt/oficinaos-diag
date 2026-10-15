# fetch-tools.ps1 — downloads the device tools the exe bundles.
# Run once before build/publish. Binaries are git-ignored.
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root "tools"
$pt = Join-Path $tools "platform-tools"

if (Test-Path "$pt\adb.exe") {
    Write-Output "platform-tools already present - skipping download"
} else {
    Write-Output "Downloading Android platform-tools..."
    $zip = Join-Path $env:TEMP "platform-tools.zip"
    Invoke-WebRequest "https://dl.google.com/android/repository/platform-tools-latest-windows.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath $tools -Force
    Remove-Item $zip

    # A URL "-latest-" é mutável — a Google não publica hash. A assinatura
    # Authenticode é a verificação de autenticidade disponível: um zip
    # adulterado/trocado não assina como Google. TODOS os binários são
    # verificados (antes era só o adb.exe) e o emissor tem de ser exatamente
    # O=Google LLC — um match por substring aceitava "O=Googlemal".
    $bad = @()
    Get-ChildItem $pt -Include *.exe,*.dll -Recurse | ForEach-Object {
        $sig = Get-AuthenticodeSignature $_.FullName
        $subject = $sig.SignerCertificate.Subject
        if ($sig.Status -ne "Valid" -or $subject -notmatch "(^|,\s*)O=Google LLC(,|$)") {
            $bad += "$($_.Name) (status: $($sig.Status), signer: $subject)"
        }
    }
    if ($bad.Count -gt 0) {
        Remove-Item $pt -Recurse -Force
        throw "platform-tools signature check failed:`n  $($bad -join "`n  ")`n— refusing to bundle unverified binaries"
    }
    Write-Output "OK: platform-tools — todos os binários assinados por Google LLC"
}

# ── iPhone (imobiledevice-net via NuGet): pin SHA-256 por versão ─────────
# O NuGet não assina os binários nativos do pacote — um pacote adulterado
# ou uma fonte alternativa comprometida passava sem controlo. Os hashes
# abaixo são os oficiais da release upstream de cada versão; se o build
# pedir uma versão sem pin, o script falha até a tabela ser atualizada.
$sha256ByVersion = @{
    "1.3.17" = @{
        "idevice_id.exe" = "40ffeb86fd7f8636180bda37b9f3c5a17a42e9a3791a65afd0ae872ff952e515"
        "ideviceactivation.exe" = "ba8fc9ebcf87d846f7863f102d63f96b040e542c70f718b85ad2072305ab884c"
        "idevicebackup.exe" = "ad9877928aac24ab4df9c82a81f4283ecde1b03fed6689d9fc0d8cd22ef10218"
        "idevicebackup2.exe" = "bbbacc24f16017db0b64b0e2b0589dd69fbc30cc09c755b70eab6de3826bb137"
        "idevicecrashreport.exe" = "afa90df8ab17d7987b2fb07f7a8b287507c730928fe291ae337c27ad6895ef64"
        "idevicedate.exe" = "0f029f038ae149036b2737248b85b997418ef5a803fde61a655d7e174166025d"
        "idevicedebug.exe" = "39747eaf92947aa7d24adfc1333f0fecbddfe0ab2cd92e3442fcef43c1a0d969"
        "idevicedebugserverproxy.exe" = "baefabca47957cb6d343349d8aa41b2a68c116909b18a3c611ae94326767155d"
        "idevicediagnostics.exe" = "5ed6cc92c90f3fc55bfc0ae6a9c902df6888b5dc1ae9700366e5bb799fd3fb0d"
        "ideviceenterrecovery.exe" = "a36f5ee1e85cd37420e2f6e512dd727028bc61b3dd8b9ac4c1925dbd9c223094"
        "ideviceimagemounter.exe" = "9f172273f672f0001ffd3c1e03ad4bc7ca22a71e3db50ee2255ec8a99c62359a"
        "ideviceinfo.exe" = "e96550b23db37e036af5196d639634467cd1fc5efafe467cb137f16df6755676"
        "ideviceinstaller.exe" = "fd4f026cd9b0b8eecddd85d9f2fec8d132ea01f176a32f927e929a0260f84db6"
        "idevicename.exe" = "c09fdbeb8ad6c81728e72214d0926a76190f287ca445609dc34ecea7a3b553ee"
        "idevicenotificationproxy.exe" = "de6e9d8b74fe8a5670244b9b37f9dc0e4625b40fa5cfe129ae31f77f52110db8"
        "idevicepair.exe" = "4fdd02a5515df859d53f36b5085c4064180833b8616e655c9d60bda9f5576b8a"
        "ideviceprovision.exe" = "34ce7573b4467add6fecdec83278e1d127eddc4dc3b71d260a5632f207564cfb"
        "idevicerestore.exe" = "19bf46182cef6c665468372812b79c04292c82e0be8cf6d4aa07cf4ee373f7cf"
        "idevicescreenshot.exe" = "ab0d623ea0e3e496d330f72fa440645563b80abebecc28f2db735c314cf96198"
        "idevicesyslog.exe" = "549a9f848ddb61c5b395b5e823967261dc67a60cd47c432a6a5ab07ca3424b8a"
        "ios_webkit_debug_proxy.exe" = "be19e0e21af718ab8f32ae9d27ee53b7874f93dd18f33fc1416da95295f1be56"
        "iproxy.exe" = "a2e55773119f36725f9ff8a3ae00e7724e5c5e2b6254a5557f8caa5b3f710b86"
        "irecovery.exe" = "efa554254735e89c9d7626ded8dd739ae53bd97fe0f55763c8ac68512bc8ae49"
        "plist_cmp.exe" = "d97b91f16090378a39eea057b9ccd4176d0c709b75f6392cad4d1c9ee9f48bae"
        "plist_test.exe" = "7a939d46a658f7bd571804edbe4720664c19ebd081008f6d7ae00db46cf76713"
        "plistutil.exe" = "801e23c06ffd41f929bf1db1f21ae360ff33e50d37e52d57dc64e4b6276eb9d6"
        "usbmuxd.exe" = "8b75ec8fd37bf59c1445d4b1b576b3092ce578d1ba478eb3b9d6f2cbf04aec9c"
        "bz2.dll" = "d1de667597c11ca5760d3baaa758aaf08ccf6fb2b1e5bae2eace7659337af789"
        "getopt.dll" = "f36763d439aa23ddf110f1a841acd4b0196ae7228db07089cea0d62629b863bb"
        "iconv-2.dll" = "e8a4a09fd639b87927b83d14fe2485a03fe3ecc813c24ee8bdd2b12fa3e425bf"
        "ideviceactivation.dll" = "775174be149419cf203e243220ae44e0e671277f32d4aaabd39bb261e05aa8f8"
        "imobiledevice-net-lighthouse.dll" = "4a9bce0575c2fc46cf12dc01e3e1eb0cb27dd09e7785b5fdb7572f368c964e83"
        "imobiledevice.dll" = "7e4a42c84bc1f8fb5cf23a72090baa5e467d89981226ff6877218aff71200b19"
        "irecovery.dll" = "279ad12a64f0add484df98723bfac3e5261fc748f23079ba2fc3753b2af97306"
        "libcrypto-1_1-x64.dll" = "3f1bb0435a1765a98547553f0111c7f0f8fc26ce3e88ec517c0532952a320970"
        "libcurl.dll" = "f20cc3046966f0bae39a1af164f1d8fed1801a31a583450b1adefe3014deeefa"
        "libssl-1_1-x64.dll" = "47933a7710438c748c1d4996952f2da91a2188d5280c6057b760e9be12ad0f0b"
        "libusb-1.0.dll" = "71934c68d8b9f7cee6db0cfe0c5f23e08571b9127dc51dfb07e3ca74200f6ea4"
        "libusb0.dll" = "35dc5f39c7db74e8f769fc75aece2a842ba20962be853a35b40d05a283202fb6"
        "libxml2.dll" = "d33968eab7035ab6b4d44306b631958419c2f3fc1db97ae768e5c25b95ba7602"
        "lzma.dll" = "d809f75f16c8ceeb343ccc9398f9da74c02bdab270867f747f2372f048b13c25"
        "pcre.dll" = "39c87232f7512e76feb6e42376db00626e237628d5c88a492d088613478a8e12"
        "pcreposix.dll" = "299ac749bda81aa93985141203f603ac880d3fc9f0d8a84e5597a020c24f5353"
        "plist.dll" = "f4921856d858124e6ebddb9c71aaf03182009690d8140c0eb1b54087e4109010"
        "pthreadVC3.dll" = "d3e4e6ca4bd83ca057e874b2c15947b21a90e11600290e61908ef0c73c236712"
        "readline.dll" = "8c3fff60ab4ee2458bcc2ed9638985f6af1a99c061cb60daca3e9a781b7b35ad"
        "usbmuxd.dll" = "349a3d0c855aa6592a581d3161ada03a67527738a38e6c5805f5492bec81ff75"
        "vcruntime140.dll" = "b989172491bcf631322d87d7b812fb5598b8fcdd1e2a30c119f5265080cd13b8"
        "zip.dll" = "5d9cf87eb136e5699fef3df425607153b13efa24b726ea982a1a8f80eb124091"
        "zlib1.dll" = "b45be7fa62570a92bdc0ab5b965b7e2b703b2551b0ab082b753281b9860dc31f"
    }
}

$csproj = Get-Content (Join-Path $root "src\OficinaDiag\OficinaDiag.csproj") -Raw
$imVersion = [regex]::Match($csproj, 'iMobileDevice-net" Version="([^"]+)"').Groups[1].Value
if (-not $imVersion) { throw "versão do iMobileDevice-net não encontrada no csproj" }
$imPins = $sha256ByVersion[$imVersion]
if (-not $imPins) {
    throw "sem pin SHA-256 para iMobileDevice-net $imVersion — calcula os hashes da release oficial e acrescenta-os à tabela deste script"
}
$imPins = $imPins.Clone() # o foreach abaixo consome o clone, não a tabela

$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
$native = Join-Path $nugetRoot "imobiledevice-net\$imVersion\runtimes\win-x64\native"
if (-not (Test-Path $native)) {
    dotnet restore (Join-Path $root "src\OficinaDiag\OficinaDiag.csproj") --nologo -v q
}
if (-not (Test-Path $native)) {
    throw "imobiledevice-net $imVersion não está no cache NuGet ($native) — corre 'dotnet restore' primeiro"
}

$badIm = @()
Get-ChildItem $native -Include *.exe,*.dll -Recurse | ForEach-Object {
    $expected = $imPins[$_.Name]
    $actual = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected) {
        $badIm += "$($_.Name): binário não listado no pin — pacote inesperado?"
    } elseif ($actual -ne $expected) {
        $badIm += "$($_.Name): SHA-256 $actual != pin $expected"
    }
    $imPins.Remove($_.Name)
}
foreach ($missing in $imPins.Keys) {
    $badIm += "${missing}: esperado no pacote mas ausente de $native"
}
if ($badIm.Count -gt 0) {
    throw "imobiledevice-net $imVersion falhou a verificação SHA-256:`n  $($badIm -join "`n  ")`n— refusing to build with unverified iPhone tools"
}
Write-Output "OK: imobiledevice-net $imVersion — todos os binários batem no pin SHA-256"
