namespace OficinaDiag.Devices;

/// <summary>
/// Caminhos relativos à pasta da app para as ferramentas empacotadas. Usam
/// '/' — o Windows aceita-a na API de ficheiros e no PowerShell, e a mesma
/// string compara diretamente com a linha de <c>tools/package-files.txt</c>
/// (o <c>PackageFilesTests</c> falha se divergirem).
/// </summary>
internal static class ToolPaths
{
    /// <summary>adb do platform-tools trazido por <c>tools/fetch-tools.ps1</c>.</summary>
    internal const string AdbRelativePath = "tools/platform-tools/adb.exe";

    /// <summary>Tabela de nomes de modelo — dados, alargáveis sem rebuild.</summary>
    internal const string ModelsJsonRelativePath = "Data/models.json";

    /// <summary>Página servida ao telemóvel pelo <c>TestServer</c>.</summary>
    internal const string DeviceTestPageRelativePath = "Tests/wwwroot/device-test.html";
}
