using System.Diagnostics;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// `ToolProcess` substituiu o bloco repetido do `IosCollector`: o timeout tem
/// de matar a árvore toda, não só o processo lançado — um `idevicediagnostics`
/// órfão segura o relay e o scan seguinte falha. O cenário é montado com um
/// lançador nativo de cada sistema (`sh` no Unix, `powershell.exe` no Windows),
/// por isso a prova vale nos dois e não passa em vazio em nenhum.
/// </summary>
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

        Assert.Equal("ok\n".Replace("\n", Environment.NewLine), ToolProcess.Run(exe, args, 5000));
    }

    [Fact]
    public async Task TimeoutMataAFilhaToda()
    {
        var dir = Directory.CreateTempSubdirectory("oficinadiag-kill-");
        try
        {
            var launch = await StartChildAsync(dir, timeoutMs: 2000);

            var saida = await launch.Run;
            launch.Clock.Stop();

            Assert.Null(saida);
            Assert.InRange(launch.Clock.ElapsedMilliseconds, 1800, 15000);
            Assert.True(await MortoAsync(launch.ChildPid),
                $"o filho {launch.ChildPid} sobreviveu ao timeout");
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
            var launch = await StartChildAsync(dir, timeoutMs: 20_000, cts.Token);

            cts.Cancel();
            Assert.Null(await launch.Run);
            Assert.True(await MortoAsync(launch.ChildPid),
                $"o filho {launch.ChildPid} sobreviveu ao cancelamento");
        }
        finally { dir.Delete(true); }
    }

    private sealed record Launch(Task<string?> Run, int ChildPid, Stopwatch Clock);

    /// <summary>
    /// Arranca um processo que cria um filho de longa duração e escreve o PID
    /// do filho antes de esperar por ele. O `Run` corre em paralelo com a espera
    /// pelo PID: com a máquina carregada, o lançador pode demorar e o teste não
    /// pode depender disso.
    /// </summary>
    private static async Task<Launch> StartChildAsync(
        DirectoryInfo dir, int timeoutMs, CancellationToken ct = default)
    {
        var pidFile = Path.Combine(dir.FullName, "child.pid");
        var (exe, args) = Spawner(dir, pidFile);

        var clock = Stopwatch.StartNew();
        var run = Task.Run(() => ToolProcess.Run(exe, args, timeoutMs, ct), CancellationToken.None);

        int? child = null;
        var espera = Stopwatch.StartNew();
        while (child is null && espera.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(20);
            try
            {
                if (File.Exists(pidFile)
                    && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) && pid > 0)
                    child = pid;
            }
            catch (IOException) { /* ficheiro a ser escrito */ }
        }

        Assert.True(child is not null, "o lançador não chegou a escrever o PID do filho");
        return new Launch(run, child!.Value, clock);
    }

    /// <summary>Lançador nativo: `sh` no Unix, `powershell.exe` no Windows.</summary>
    private static (string Exe, string Args) Spawner(DirectoryInfo dir, string pidFile)
    {
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(dir.FullName, "spawn.ps1");
            File.WriteAllText(ps1, $"""
                $p = Start-Process -FilePath cmd.exe -ArgumentList '/c','ping -n 60 127.0.0.1 >NUL' -PassThru -WindowStyle Hidden
                Set-Content -Path '{pidFile}' -Value $p.Id
                Wait-Process -Id $p.Id
                """);
            return ("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"");
        }

        var sh = Path.Combine(dir.FullName, "spawn.sh");
        File.WriteAllText(sh, "#!/bin/sh\nsleep 60 &\necho $! > \"$1\"\nwait\n");
        File.SetUnixFileMode(sh,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return ("/bin/sh", $"\"{sh}\" \"{pidFile}\"");
    }

    /// <summary>Espera até 5 s que o PID deixe de existir.</summary>
    private static async Task<bool> MortoAsync(int pid)
    {
        for (var i = 0; i < 100; i++)
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
