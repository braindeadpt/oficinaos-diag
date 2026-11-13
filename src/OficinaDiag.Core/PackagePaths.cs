namespace OficinaDiag;

/// <summary>
/// Ficheiros do pacote de release, relativos à pasta da app. São a única
/// origem no código: o <c>PackageFilesTests</c> compara-os com as linhas de
/// <c>tools/package-files.txt</c> e falha se divergirem. Usam '/' — o Windows
/// aceita-a na API de ficheiros e no PowerShell, e a string compara
/// diretamente com a lista.
/// </summary>
internal static class PackagePaths
{
    /// <summary>Exe da app; tem de bater com o &lt;AssemblyName&gt; do csproj.</summary>
    internal const string AppExeName = "OficinaDiag.exe";

    /// <summary>adb do platform-tools trazido por <c>tools/fetch-tools.ps1</c>.</summary>
    internal const string AdbRelativePath = "tools/platform-tools/adb.exe";

    /// <summary>Tabela de nomes de modelo — dados, alargáveis sem rebuild.</summary>
    internal const string ModelsJsonRelativePath = "Data/models.json";

    /// <summary>Página servida ao telemóvel pelo <c>TestServer</c>.</summary>
    internal const string DeviceTestPageRelativePath = "Tests/wwwroot/device-test.html";
}
