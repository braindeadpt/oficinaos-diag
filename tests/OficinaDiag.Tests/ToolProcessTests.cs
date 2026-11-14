using System.Diagnostics;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// `ToolProcess` substituiu o bloco repetido do `IosCollector`: o timeout tem
/// de matar a árvore toda, não só o processo lançado — um `idevicediagnostics`
/// órfão segura o relay e o scan seguinte falha.
/// O cenário é montado com um lançador nativo (`sh` no Unix, `cmd` no Windows)
/// que cria um filho de longa duração; o filho **bate** um ficheiro enquanto
/// vive, e é a paragem das batidas que prova a morte dele. Sem PowerShell e sem
/// PID: o arranque do `powershell.exe` foi o único componente cujo tempo não
/// controlávamos, e foi ele que deixou dois testes pendurados aos 30 s no
/// Windows sem diagnóstico.
/// </summary>
[Collection(SerialCollection.Name)]
public class ToolProcessTests
{
    [Fact]
    public void SemExecutavelDevolveNulo() =>
        Assert.Null(ToolProcess.Run("/caminho/que/nao/existe", "--version", 1000));

    [Fact]
    public void DevolveOStdoutDoProcesso()
    {
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c echo ok")
            : ("/bin/echo", "ok");

        Assert.Equal("ok", ToolProcess.Run(exe, args, 5000)?.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task TimeoutMataAFilhaToda()
    {
        var dir = Directory.CreateTempSubdirectory("oficinadiag-kill-");
        try
        {
            // 5 s, não 2: o lançador e o filho arrancam em ms, mas dar folga
            // ao *arranque* não enfraquece a asserção — ela é sobre o kill.
            var launch = await StartChildAsync(dir, timeoutMs: 5000);

            var saida = await launch.Run;
            launch.Clock.Stop();

            Assert.Null(saida);
            Assert.InRange(launch.Clock.ElapsedMilliseconds, 4500, 20000);
            Assert.True(await ParouDeBaterAsync(launch.BeatFile),
                $"o filho continuou vivo depois do timeout — {Diagnostico(dir)}");
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task CancelamentoTambemMataAFilha()
    {
        var dir = Directory.CreateTempSubdirectory("oficinadiag-cancel-");
        try
        {
            using var cts = new CancellationTokenSource();
            var launch = await StartChildAsync(dir, timeoutMs: 60_000, cts.Token);

            cts.Cancel();

            // O limite de 5 s é o que dá sentido ao teste: sem ele, um
            // cancelamento que não mata nada passava na mesma, porque o
            // lançador acaba sozinho aos 60 s e o `Run` devolve null.
            string? saida;
            try
            {
                saida = await launch.Run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Assert.True(false, $"o cancelamento não matou o processo em 5 s — {Diagnostico(dir)}");
                return;
            }

            Assert.Null(saida);
            Assert.True(await ParouDeBaterAsync(launch.BeatFile),
                $"o filho continuou vivo depois do cancelamento — {Diagnostico(dir)}");
        }
        finally { dir.Delete(true); }
    }

    private sealed record Launch(Task<string?> Run, string BeatFile, Stopwatch Clock);

    /// <summary>
    /// Arranca o lançador e espera que o filho dê sinal de vida. O `Run` corre
    /// em paralelo com essa espera: o teste não depende de o filho aparecer
    /// depressa, só de aparecer.
    /// </summary>
    private static async Task<Launch> StartChildAsync(
        DirectoryInfo dir, int timeoutMs, CancellationToken ct = default)
    {
        var beat = Path.Combine(dir.FullName, "beat.txt");
        var (exe, args) = Spawner(dir);

        var clock = Stopwatch.StartNew();
        var run = Task.Run(() => ToolProcess.Run(exe, args, timeoutMs, ct), CancellationToken.None);

        var espera = Stopwatch.StartNew();
        while (!File.Exists(beat) && espera.ElapsedMilliseconds < 30_000)
            await Task.Delay(20);

        Assert.True(File.Exists(beat), $"o filho não chegou a bater — {Diagnostico(dir)}");
        return new Launch(run, beat, clock);
    }

    /// <summary>
    /// O que ficou no diretório. Sem isto, uma falha dizia só «não apareceu»;
    /// com isto distingue-se «o lançador nunca correu» de «correu e o filho
    /// não arrancou».
    /// </summary>
    private static string Diagnostico(DirectoryInfo dir)
    {
        string Ler(string nome)
        {
            var p = Path.Combine(dir.FullName, nome);
            try { return File.Exists(p) ? File.ReadAllText(p).Trim() : "(ausente)"; }
            catch (IOException) { return "(bloqueado)"; }
        }

        return $"started={Ler("launcher-started.txt")}; beat={Ler("beat.txt")}; " +
               $"ficheiros={string.Join(",", dir.GetFileSystemInfos().Select(f => f.Name))}";
    }

    /// <summary>
    /// Espera 2,5 s e diz se o ficheiro parou de mudar. O filho bate a cada
    /// ~0,5 s (Unix) / ~1 s (Windows), por isso quatro batidas perdidas são
    /// morte, não lentidão.
    /// </summary>
    private static async Task<bool> ParouDeBaterAsync(string beatFile)
    {
        static DateTime Mtime(string path)
        {
            try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
            catch (IOException) { return DateTime.MinValue; }
        }

        var antes = Mtime(beatFile);
        await Task.Delay(2500);
        return Mtime(beatFile) == antes;
    }

    /// <summary>
    /// Lançador nativo: `sh` no Unix, `cmd` no Windows. O lançador deixa um
    /// marcador, arranca o filho e fica vivo até ser morto — é ele que o
    /// `ToolProcess` lança e cuja árvore tem de morrer.
    /// </summary>
    private static (string Exe, string Args) Spawner(DirectoryInfo dir)
    {
        var beat = Path.Combine(dir.FullName, "beat.txt");
        var marcador = Path.Combine(dir.FullName, "launcher-started.txt");

        if (OperatingSystem.IsWindows())
        {
            var filho = Path.Combine(dir.FullName, "child.cmd");
            File.WriteAllText(filho, $"""
                @echo off
                :loop
                echo %RANDOM% > "{beat}"
                ping -n 2 127.0.0.1 >NUL
                goto loop
                """);

            var lancador = Path.Combine(dir.FullName, "launcher.cmd");
            File.WriteAllText(lancador, $"""
                @echo off
                echo ok > "{marcador}"
                start "" /b cmd /c "{filho}"
                ping -n 60 127.0.0.1 >NUL
                """);
            return ("cmd.exe", $"/c \"\"{lancador}\"\"");
        }

        var shFilho = Path.Combine(dir.FullName, "child.sh");
        File.WriteAllText(shFilho, $"#!/bin/sh\nwhile :; do echo $$ > \"{beat}\"; sleep 0.5; done\n");
        File.SetUnixFileMode(shFilho,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var sh = Path.Combine(dir.FullName, "launcher.sh");
        File.WriteAllText(sh,
            $"#!/bin/sh\necho ok > \"{marcador}\"\n\"{shFilho}\" &\nwait\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return ("/bin/sh", $"\"{sh}\"");
    }
}
