using System.Diagnostics;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

public class AdbTunnelTests
{
    [Fact]
    public async Task RemoveComSucessoNaoAvisa()
    {
        var calls = new List<string>();
        var warnings = new List<string>();
        var tunnel = new AdbTunnel((serial, args) =>
        {
            calls.Add($"{serial} {args}");
            return Task.FromResult(true);
        }, warnings.Add);

        tunnel.Mount("R58M12345", 8801);
        Assert.True(await tunnel.ClearAsync());

        Assert.Equal(new[] { "R58M12345 reverse --remove tcp:8801" }, calls);
        Assert.Empty(warnings);
        Assert.False(tunnel.IsMounted);
    }

    [Fact]
    public async Task RemoveQueFalhaRegistaAvisoComAPorta()
    {
        // adb falso que recusa o remove (telefone desligado / adb morto).
        var argsVistos = new List<string>();
        var warnings = new List<string>();
        var tunnel = new AdbTunnel((_, args) =>
        {
            argsVistos.Add(args);
            return Task.FromResult(false);
        }, warnings.Add);

        tunnel.Mount("emulator-5554", 8801);
        Assert.False(await tunnel.ClearAsync());

        Assert.Equal("reverse --remove tcp:8801", Assert.Single(argsVistos));
        var aviso = Assert.Single(warnings);
        // Não fica silencioso: diz o que ficou montado e como confirmar.
        // (só a porta e o comando, que existem nas duas línguas: o idioma
        // activo é global e outros testes podem tê-lo trocado)
        Assert.Contains("tcp:8801", aviso);
        Assert.Contains("adb reverse --list", aviso);
        Assert.False(tunnel.IsMounted);
    }

    [Fact]
    public async Task AvisoApareceUmaVezMesmoQueSeLimpeDuasVezes()
    {
        var warnings = new List<string>();
        var tunnel = new AdbTunnel((_, _) => Task.FromResult(false), warnings.Add);

        tunnel.Mount("R58M12345", 8802);
        Assert.False(await tunnel.ClearAsync());
        Assert.True(await tunnel.ClearAsync()); // segunda chamada é no-op

        Assert.Single(warnings);
    }

    [Fact]
    public async Task AdbQueNuncaRespondeEstouraNoTimeoutEAvisa()
    {
        // adb pendurado: a task nunca completa (telefone a dormir a meio do
        // reverse). Sem timeout o ClearAsync ficava à espera para sempre.
        var pendurado = new TaskCompletionSource<bool>();
        var warnings = new List<string>();
        var tunnel = new AdbTunnel(
            (_, _) => pendurado.Task,
            warnings.Add,
            timeout: TimeSpan.FromMilliseconds(100));

        tunnel.Mount("R58M12345", 8803);
        var inicio = Stopwatch.StartNew();
        Assert.False(await tunnel.ClearAsync());
        inicio.Stop();

        Assert.True(inicio.Elapsed < TimeSpan.FromSeconds(3), $"demorou {inicio.Elapsed}");
        var aviso = Assert.Single(warnings);
        Assert.Contains("tcp:8803", aviso);
        Assert.False(tunnel.IsMounted);
    }

    [Fact]
    public async Task AdbQueLancaContaComoFalhaEAvisa()
    {
        // adb que rebenta (binário em falta, acesso negado) em vez de
        // devolver false: tem de acabar em aviso, não em exceção.
        var warnings = new List<string>();
        var tunnel = new AdbTunnel(
            (_, _) => throw new InvalidOperationException("adb desapareceu"),
            warnings.Add);

        tunnel.Mount("R58M12345", 8804);
        Assert.False(await tunnel.ClearAsync());

        var aviso = Assert.Single(warnings);
        Assert.Contains("tcp:8804", aviso);
        Assert.False(tunnel.IsMounted);
    }

    [Fact]
    public async Task SemTunelNaoChamaAdbNemAvisa()
    {
        var chamou = false;
        var warnings = new List<string>();
        var tunnel = new AdbTunnel((_, _) => { chamou = true; return Task.FromResult(false); }, warnings.Add);

        Assert.True(await tunnel.ClearAsync());
        Assert.False(chamou);
        Assert.Empty(warnings);
    }
}
