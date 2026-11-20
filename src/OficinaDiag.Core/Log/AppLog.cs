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
    // maiores e a cauda útil ficava enterrada. Acima de ~1 MB o ficheiro roda
    // para `diag.log.1` e o atual recomeça vazio: guarda-se sempre ~2 MB no
    // máximo (atual + anterior) e cada escrita fica O(linha), sem reescritas.
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
                RotateIfNeeded();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging nunca derruba a app */ }
    }

    private void RotateIfNeeded()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length <= MaxBytes) return;
        // Uma vez por ~1 MB de linhas: rename atómico em vez de copiar a cauda.
        File.Move(FilePath, FilePath + ".1", overwrite: true);
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
