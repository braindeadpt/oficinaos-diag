using System.IO;

namespace OficinaDiag.Log;

/// <summary>
/// Append-only text log at %APPDATA%\OficinaDiag\diag.log — every console line
/// and every unhandled exception lands here so crash reports survive the app.
/// </summary>
public static class AppLog
{
    private static readonly string File = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OficinaDiag", "diag.log");
    private static readonly object Gate = new();

    public static string FilePath => File;

    // Sem teto o log crescia para sempre — uploads de suporte iam cada vez
    // maiores e a cauda útil ficava enterrada. Mantém os últimos ~1 MB.
    private const long MaxBytes = 1_000_000;

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
                TrimIfNeeded();
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging nunca derruba a app */ }
    }

    private static void TrimIfNeeded()
    {
        var info = new FileInfo(File);
        if (!info.Exists || info.Length <= MaxBytes) return;
        // Ainda raro — corre uma vez por ~1 MB de linhas.
        using (var fs = new FileStream(File, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var keep = (int)Math.Min(fs.Length, MaxBytes / 2);
            fs.Seek(-keep, SeekOrigin.End);
            var buf = new byte[keep];
            var read = fs.Read(buf, 0, keep);
            fs.SetLength(0);
            fs.Write(buf, 0, read);
        }
    }

    public static void Exception(string where, System.Exception ex) =>
        Write($"! {where}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
