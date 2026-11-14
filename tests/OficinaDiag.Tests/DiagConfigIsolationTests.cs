using System.Diagnostics;
using System.Text.Json;
using OficinaDiag.Cloud;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// O `DiagConfig` gravava sempre no bin do projeto. Duas suites
/// `dotnet test` em paralelo partilhavam esse ficheiro: uma apagava-o no
/// construtor enquanto a outra o lia, e a falha aparecia em `DiagConfigTests`
/// sem nada de errado no código.
/// Aqui a raiz é injetada e prova-se com dois **processos** de verdade, em
/// paralelo, cada um no seu root — o cenário que falhava, não uma imitação.
/// </summary>
// A raiz do config é estática ao processo: correr estas duas classes em
// paralelo era repetir o bug que este ficheiro fecha.
[Collection(SerialCollection.Name)]
public sealed class DiagConfigIsolationTests
{
    /// <summary>
    /// Worker: grava e relê no root indicado por `OFICINAOS_CONFIG_DIR`, com o
    /// nome de loja vindo de `OFICINAOS_TEST_SHOP`. Corre sozinho (root
    /// temporário próprio) e dentro dos dois processos do teste de isolamento.
    /// </summary>
    [Fact]
    public void SaveELoad_FazemRoundTripNoRootAtual()
    {
        // No processo filho a raiz vem do ambiente e **não** se usa o seam:
        // é o caminho de produção (Load/Save com a raiz por omissão) que tem
        // de ficar isolado. Em corrida solitária, root temporário via seam.
        var doAmbiente = Environment.GetEnvironmentVariable("OFICINAOS_CONFIG_DIR");
        var anterior = doAmbiente is null
            ? DiagConfig.RedirectTo(Path.Combine(Path.GetTempPath(), $"config-solo-{Guid.NewGuid():N}"))
            : null;

        try
        {
            var loja = Environment.GetEnvironmentVariable("OFICINAOS_TEST_SHOP") ?? "loja-solo";
            var cfg = new DiagConfig { ShopName = loja, HistoryRetentionDays = 7 };
            cfg.Save();

            var lida = DiagConfig.Load();
            Assert.Equal(loja, lida.ShopName);
            Assert.Equal(7, lida.HistoryRetentionDays);
        }
        finally { if (anterior is not null) DiagConfig.RedirectTo(anterior); }
    }

    [Fact]
    public async Task DuasSuitesEmParalelo_NaoPartilhamFicheiroDeConfig()
    {
        var dirA = Directory.CreateTempSubdirectory("config-proc-a-").FullName;
        var dirB = Directory.CreateTempSubdirectory("config-proc-b-").FullName;
        try
        {
            var a = CorrerSuiteAsync(dirA, "loja-A");
            var b = CorrerSuiteAsync(dirB, "loja-B");
            var (okA, saidaA) = await a;
            var (okB, saidaB) = await b;

            Assert.True(okA, $"o processo A falhou:\n{saidaA}");
            Assert.True(okB, $"o processo B falhou:\n{saidaB}");

            // Cada processo leu o seu: se partilhassem ficheiro, um via o nome
            // do outro (ou nenhum, se apagado a meio pelo construtor).
            Assert.Equal("loja-A", LojaGravada(dirA));
            Assert.Equal("loja-B", LojaGravada(dirB));
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    private static string LojaGravada(string dir)
    {
        var path = Path.Combine(dir, "oficinaos-diag.config.json");
        Assert.True(File.Exists(path), $"sem config em {dir}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("ShopName").GetString() ?? "";
    }

    /// <summary>
    /// Corre a suite filtrada ao worker num processo novo, com a raiz e o nome
    /// de loja no ambiente. `--no-build`: o binário já existe (o processo pai
    /// compilou-o) e dois MSBuild a escrever no mesmo obj seria outra corrida.
    /// </summary>
    private static async Task<(bool Ok, string Saida)> CorrerSuiteAsync(string root, string loja)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Repo.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[]
        {
            "test", "tests/OficinaDiag.Tests", "-c", "Release", "--no-build",
            "--filter", "FullyQualifiedName~SaveELoad_FazemRoundTripNoRootAtual",
        }) psi.ArgumentList.Add(arg);

        psi.Environment["OFICINAOS_CONFIG_DIR"] = root;
        psi.Environment["OFICINAOS_TEST_SHOP"] = loja;

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var limite = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(limite.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (false, "a suite não terminou em 3 min");
        }

        return (p.ExitCode == 0, $"exit={p.ExitCode}\n{await stdout}\n{await stderr}");
    }
}
