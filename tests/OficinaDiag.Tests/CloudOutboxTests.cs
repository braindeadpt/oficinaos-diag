using System.Net;
using System.Text.Json;
using OficinaDiag.Cloud;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

public class CloudOutboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oficinaos-outbox-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private CloudOutbox Fila(CloudOutbox.Enviador enviar, Func<DateTimeOffset>? agora = null)
        => new(_dir, enviar, agora);

    /// <summary>Cliente cuja rede falha antes de o pedido sair.</summary>
    private static CloudClient SemRede() => new(
        new DiagConfig { CloudUrl = "http://127.0.0.1:1" },
        new HttpMessageInvoker(new HandlerQueRebenta()),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), _ => Task.CompletedTask);

    private static CloudOutbox.Enviador Falha => (_, _, _, _) =>
        Task.FromResult((false, "sem rede"));

    private static CloudOutbox.Enviador Sucesso => (_, _, _, _) =>
        Task.FromResult((true, "ok"));

    [Fact]
    public async Task SemRedeORelatorioFicaEmFilaEOTecnicoSabe()
    {
        var report = new DeviceReport
        {
            Platform = "android",
            Device = new DeviceIdentity { Brand = "Samsung", Model = "SM-G991B", Serial = "R5X" },
        };
        var cloud = SemRede();
        var fila = Fila(Falha);

        var (ok, msg) = await cloud.SendToShopAsync(
            "LOJA1", report, "Cliente", "912345678", null, null, null, fila);

        Assert.False(ok);
        Assert.Equal(L10n.T("cloud.send.queued"), msg);
        Assert.Equal(1, fila.Pendentes);
    }

    [Fact]
    public async Task ADrenagemUsaAMesmaChaveDeIdempotencia()
    {
        var chaves = new List<string>();
        var gravar = Fila(Falha);
        await gravar.GuardarAsync("LOJA1", "{\"a\":1}", "chave-1");

        CloudOutbox.Enviador registar = (_, _, chave, _) =>
        {
            chaves.Add(chave);
            return Task.FromResult((true, "ok"));
        };
        var enviados = await Fila(registar).DrenarAsync();

        Assert.Equal(1, enviados);
        Assert.Equal("chave-1", Assert.Single(chaves));
        Assert.Equal(0, gravar.Pendentes);
    }

    [Fact]
    public async Task DrenarParaNoPrimeiroFalhancoEPreservaOsRestantes()
    {
        var gravar = Fila(Falha);
        await gravar.GuardarAsync("LOJA1", "{\"a\":1}", "k1");
        await Task.Delay(2); // nome do ficheiro tem milissegundos
        await gravar.GuardarAsync("LOJA1", "{\"a\":2}", "k2");
        Assert.Equal(2, gravar.Pendentes);

        var enviados = await Fila(Falha).DrenarAsync();

        Assert.Equal(0, enviados);
        Assert.Equal(2, gravar.Pendentes);
    }

    [Fact]
    public async Task FicheiroEstragadoNaoTravaAFila()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "20200101000000000-aaaaaaaa.json"), "{ isto não é json");
        var gravar = Fila(Falha);
        await gravar.GuardarAsync("LOJA1", "{\"a\":1}", "k1");

        var enviados = await Fila(Sucesso).DrenarAsync();

        Assert.Equal(1, enviados);
        Assert.Equal(0, gravar.Pendentes);
        Assert.True(Directory.Exists(Path.Combine(_dir, "erro")));
    }

    [Fact]
    public async Task PastaDeErroNaoCresceSemTeto()
    {
        // A quarentena é para diagnóstico: mais de 10 ficheiros corrompidos
        // acumulados começam a ser apagados — não enchem o disco ao longo
        // de anos de uso.
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < 15; i++)
        {
            var f = Path.Combine(_dir, $"202001010000000{i:d2}-bbbbbbbb.json");
            await File.WriteAllTextAsync(f, "{ corrompido");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-i));
        }

        await Fila(Sucesso).DrenarAsync();

        var erro = Path.Combine(_dir, "erro");
        Assert.True(Directory.Exists(erro));
        Assert.True(Directory.GetFiles(erro, "*.json").Length <= 10);
    }

    [Fact]
    public async Task EnvioVelhoNaoSaiEDesaparece()
    {
        var agora = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var velho = Fila(Falha, () => agora.AddDays(-40));
        await velho.GuardarAsync("LOJA1", "{\"a\":1}", "k1");

        var enviados = await Fila(Sucesso, () => agora).DrenarAsync();

        // Um relatório com 40 dias de um cliente não deve sair agora (RGPD):
        // expira em vez de ir para a loja.
        Assert.Equal(0, enviados);
        Assert.Equal(0, velho.Pendentes);
    }

    [Fact]
    public async Task AFilaTemTeto()
    {
        var fila = Fila(Falha);
        for (var i = 0; i < CloudOutbox.Maximo + 5; i++)
            await fila.GuardarAsync("LOJA1", $"{{\"n\":{i}}}", $"k{i}");

        Assert.Equal(CloudOutbox.Maximo, fila.Pendentes);
    }

    [Fact]
    public async Task OCorpoGuardadoEORelatorioInteiro()
    {
        var report = new DeviceReport
        {
            Platform = "android",
            Device = new DeviceIdentity { Brand = "Samsung", Model = "SM-G991B", Serial = "R5X" },
        };
        var cloud = SemRede();
        var fila = Fila(Falha);
        await cloud.SendToShopAsync("LOJA1", report, "Ana", "912345678", "ana@exemplo.pt", null, null, fila);

        var corpo = await File.ReadAllTextAsync(Directory.GetFiles(_dir, "*.json").Single());
        using var doc = JsonDocument.Parse(corpo);
        var envio = doc.RootElement;
        Assert.Equal("LOJA1", envio.GetProperty("ShopCode").GetString());
        // O que o técnico escreveu no diálogo viaja com o envio — não se perde
        // por a app ter sido fechada a seguir.
        Assert.Contains("Ana", envio.GetProperty("Corpo").GetString());
        Assert.Contains("ana@exemplo.pt", envio.GetProperty("Corpo").GetString());
    }

    /// <summary>Handler que falha como uma rede em baixo (antes de sair o pedido).</summary>
    private sealed class HandlerQueRebenta : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("sem ligação");
    }
}
