using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

/// <summary>
/// Espelho de ecrã Android via scrcpy (Apache-2.0, Genymobile). O binário
/// chega por <c>tools/fetch-tools.ps1</c> — zip win64 da release oficial
/// pinado por SHA-256 — e viaja no pacote em <c>tools/scrcpy/</c>; nunca é
/// versionado. É espelho, não controlo remoto: o técnico vê o ecrã do
/// cliente para guiar a reparação, e precisa de depuração USB ativa — por
/// isso a UI só o oferece com um Android <c>ready</c>.
/// </summary>
public static class ScrcpyMirror
{
    /// <summary>
    /// Serial no mesmo alfabeto que o <c>adb devices</c> emite
    /// (<c>[\w.:@-]+</c>) e a mesma guarda do RunAdbAsync da app para o
    /// <c>-s</c>: um valor a começar por '-' seria lido como flag pelo
    /// scrcpy, não como alvo. ASCII explícito — o \w do regex aceita
    /// unicode e não há serial adb que precise dele.
    /// </summary>
    private static readonly Regex SerialValido =
        new(@"^[A-Za-z0-9][A-Za-z0-9._:@-]*$", RegexOptions.Compiled);

    /// <summary>O scrcpy.exe do pacote, ou null se tools/scrcpy não foi trazido.</summary>
    public static string? FindExe(string baseDir)
    {
        var exe = Path.Combine(baseDir, PackagePaths.ScrcpyExeRelativePath);
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Argumentos do scrcpy: <c>-s serial</c> e título de janela opcional.
    /// Devolve null quando o serial não passa na validação — um serial
    /// estranho virava flag do scrcpy (ou do adb lá dentro), nunca um alvo.
    /// </summary>
    public static string? BuildArguments(string? serial, string? windowTitle = null)
    {
        if (serial is null || !SerialValido.IsMatch(serial)) return null;
        var args = $"-s {serial}";
        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            // O título vai entre aspas para o CommandLineToArgvW o manter
            // como um argumento; aspas lá dentro eram injection de args.
            var titulo = windowTitle.Replace("\"", string.Empty).Trim();
            if (titulo.Length > 0) args += $" --window-title=\"{titulo}\"";
        }
        return args;
    }

    /// <summary>
    /// ProcessStartInfo pronto a lançar, ou null quando o exe não está no
    /// pacote ou o serial é inválido. Sem consola nem espera — o scrcpy
    /// desenha a própria janela SDL e vive até ela fechar — e com
    /// stdout/stderr capturados para registar uma saída antecipada.
    ///
    /// O <c>ADB</c> aponta para o adb do platform-tools bundled: sem ele o
    /// scrcpy usava o adb.exe da própria release e, com versões diferentes,
    /// o servidor adb entrava em guerra com o detetor (cada um matava o
    /// server do outro a cada ronda de 2 s).
    /// </summary>
    public static ProcessStartInfo? CreateProcessInfo(
        string baseDir, string adbPath, string? serial, string? windowTitle = null)
    {
        var exe = FindExe(baseDir);
        var args = BuildArguments(serial, windowTitle);
        if (exe is null || args is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["ADB"] = adbPath;
        return psi;
    }
}
