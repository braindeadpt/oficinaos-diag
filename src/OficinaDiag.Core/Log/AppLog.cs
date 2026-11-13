using System.IO;

namespace OficinaDiag.Log;

/// <summary>
/// Append-only text log — every console line and every unhandled exception
/// lands here so crash reports survive the app. O caminho é do construtor: a
/// app usa %APPDATA%\OficinaDiag\diag.log, os testes um ficheiro temporário.
/// </summary>
public sealed class LogFile
{
    // Sem teto o log crescia para sempre — uploads de suporte iam cada vez
    // maiores e a cauda útil ficava enterrada. Mantém os últimos ~1 MB.
    public const long MaxBytes = 1_000_000;

    private readonly object _gate = new();

    public LogFile(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                TrimIfNeeded();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging nunca derruba a app */ }
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes) return;
        // Ainda raro — corre uma vez por ~1 MB de linhas.
        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var keep = (int)Math.Min(fs.Length, MaxBytes / 2);
        fs.Seek(-keep, SeekOrigin.End);
        var buf = new byte[keep];
        var read = fs.Read(buf, 0, keep);
        // Começar na linha seguinte: cortar a meio deixa um caractere
        // multi-byte partido ("ã", "ç", "—") e a primeira linha sai com �.
        // O '\n' nunca cai dentro de uma sequência UTF-8, por isso saltar até
        // ele (e não só "até ao próximo byte válido") garante linha inteira.
        var start = 0;
        var nl = Array.IndexOf(buf, (byte)'\n', 0, read);
        if (nl >= 0 && nl + 1 < read) start = nl + 1;
        fs.SetLength(0);
        fs.Write(buf, start, read - start);
    }
}

/// <summary>Fachada estática do log da app sobre um único <see cref="LogFile"/>.</summary>
public static class AppLog
{
    private static LogFile _sink = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OficinaDiag", "diag.log"));

    public static string FilePath => _sink.FilePath;

    public static void Write(string line) => _sink.Write(line);

    public static void Exception(string where, System.Exception ex) =>
        Write($"! {where}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>Testes: manda o log para um ficheiro temporário e devolve o anterior.</summary>
    internal static string RedirectTo(string filePath)
    {
        var previous = _sink.FilePath;
        _sink = new LogFile(filePath);
        return previous;
    }
}
