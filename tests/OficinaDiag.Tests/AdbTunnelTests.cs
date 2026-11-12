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
