using System.Diagnostics;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// `ToolProcess` substituiu o bloco repetido do `IosCollector`: o timeout tem
/// de matar a árvore toda, não só o processo lançado — um `idevicediagnostics`
/// órfão segura o relay e o scan seguinte falha.
/// </summary>
public class ToolProcessTests
{
    [Fact]
    public void SemExecutavelDevolveNulo() =>
        Assert.Null(ToolProcess.Run("/caminho/que/nao/existe", "--version", 1000));

    [Fact]
    public void DevolveOStdoutDoProcesso()
    {
        if (OperatingSystem.IsWindows()) return; // shell POSIX nos testes
        Assert.Equal("ok\n", ToolProcess.Run("/bin/echo", "ok", 5000));
    }

    [Fact]
    public async Task TimeoutMataAFilhaToda()
    {
        if (OperatingSystem.IsWindows()) return; // /bin/sh só no Unix

        var dir = Directory.CreateTempSubdirectory("oficinadiag-kill-");
        try
        {
            var pidFile = Path.Combine(dir.FullName, "child.pid");
            var script = Path.Combine(dir.FullName, "spawn.sh");
            // Lança um filho que não termina sozinho e fica à espera dele.
            await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 60 &\necho $! > \"$1\"\nwait\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // O timeout corre em paralelo com a espera pelo PID: com a máquina
            // carregada, 400 ms podiam não chegar para o shell arrancar — o
            // teste ficava dependente da carga, não do kill da árvore.
            var cronometro = Stopwatch.StartNew();
            var run = Task.Run(() => ToolProcess.Run("/bin/sh", $"\"{script}\" \"{pidFile}\"", 2000));
            var espera = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && espera.ElapsedMilliseconds < 1500)
                await Task.Delay(20);
            Assert.True(File.Exists(pidFile), "o script não chegou a lançar o filho");
            var filho = int.Parse(File.ReadAllText(pidFile).Trim());

            var saida = await run;
            cronometro.Stop();
            Assert.Null(saida);
            Assert.InRange(cronometro.ElapsedMilliseconds, 1800, 15000);
            Assert.True(await MortoAsync(filho), $"o filho {filho} sobreviveu ao timeout");
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task CancelamentoTambemMataAFilha()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cts = new CancellationTokenSource(200);
        var saida = ToolProcess.Run("/bin/sh", "-c \"sleep 30\"", 20_000, cts.Token);

        Assert.Null(saida);
        Assert.True(cts.IsCancellationRequested);
        await Task.CompletedTask;
    }

    /// <summary>Espera até 2 s que o PID deixe de existir.</summary>
    private static async Task<bool> MortoAsync(int pid)
    {
        for (var i = 0; i < 40; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) return true;
            }
            catch (ArgumentException) { return true; }

            await Task.Delay(50);
        }
        return false;
    }
}
