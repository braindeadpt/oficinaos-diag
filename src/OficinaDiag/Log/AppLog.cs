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

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(File)!);
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging nunca derruba a app */ }
    }

    public static void Exception(string where, System.Exception ex) =>
        Write($"! {where}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
