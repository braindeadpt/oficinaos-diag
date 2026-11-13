using System.Diagnostics;
using System.Net;
using System.Net.Http;
using OficinaDiag.Cloud;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// T-04: o que acontece quando o servidor não responde ou responde mal.
/// Timeout: 20 s nos endpoints rápidos (com uma repetição), 150 s na IA (sem
/// repetir — cada tentativa pode ser cobrada). 5xx segue a mesma divisão.
/// Sem relógio: um handler falso fica pendurado até o token ser cancelado, e
/// os timeouts entram acelerados pela seam interna.
/// </summary>
public sealed class CloudClientRetryTests
{
    /// <summary>Aceita o pedido e nunca responde: só o token o desbloqueia.</summary>
    private sealed class Pendurado : HttpMessageHandler
    {
        public int Pedidos;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Pedidos);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Task.Delay infinito só termina por cancelamento");
        }
    }

    /// <summary>Responde segundo um guião; a última resposta repete-se.</summary>
    private sealed class Guiao(params HttpStatusCode[] codigos) : HttpMessageHandler
    {
        public int Pedidos;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var i = Math.Min(Interlocked.Increment(ref Pedidos) - 1, codigos.Length - 1);
            return Task.FromResult(new HttpResponseMessage(codigos[i])
            {
                Content = new StringContent(codigos[i] == HttpStatusCode.OK ? "{}" : "indisponivel"),
            });
        }
    }

    private static CloudClient Cliente(
        HttpMessageHandler handler, TimeSpan? fast = null, TimeSpan? ai = null, Action? onRetry = null)
    {
        var cfg = new DiagConfig { CloudUrl = "http://127.0.0.1:1" };
        return new CloudClient(cfg, new HttpMessageInvoker(handler),
            fast ?? TimeSpan.FromSeconds(20), ai ?? TimeSpan.FromSeconds(150), _ =>
            {
                onRetry?.Invoke();
                return Task.CompletedTask;
            });
    }

    [Fact]
    public void TimeoutsPorDefeito_SaoVinteEDuzentosECinquentaSegundos()
    {
        var cloud = new CloudClient(new DiagConfig { CloudUrl = "http://127.0.0.1:1" });

        Assert.Equal(TimeSpan.FromSeconds(20), cloud.FastTimeout);
        Assert.Equal(TimeSpan.FromSeconds(150), cloud.AiTimeout);
    }

    [Fact]
    public async Task PedidoRapido_DesisteAoFimDoTimeoutERepeteUmaVez()
    {
        var handler = new Pendurado();
        var esperas = 0;
        var cloud = Cliente(handler, TimeSpan.FromMilliseconds(60), null, () => esperas++);

        var sw = Stopwatch.StartNew();
        var (ok, msg) = await cloud.SendToShopAsync(
            "LOJA", new DeviceReport { Platform = "ios" }, "Ana", "912345678", null, null, null);
        sw.Stop();

        Assert.False(ok);
        Assert.Equal(L10n.T("cloud.timeout"), msg);
        // 20 s (aqui 60 ms) estoura → o idempotente repete uma vez e desiste.
        Assert.Equal(2, handler.Pedidos);
        Assert.Equal(1, esperas);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"demorou {sw.Elapsed}");
    }

    [Fact]
    public async Task Ia_DesisteAoFimDoTimeoutSemRepetir()
    {
        var handler = new Pendurado();
        var esperas = 0;
        var cloud = Cliente(handler, null, TimeSpan.FromMilliseconds(60), () => esperas++);

        var sw = Stopwatch.StartNew();
        var (ok, err) = await cloud.GenerateAiReportAsync("token", new DeviceReport { Platform = "ios" });
        sw.Stop();

        Assert.False(ok);
        Assert.Equal(L10n.T("cloud.ai.timeout"), err);
        // 150 s (aqui 60 ms) estoura e NÃO repete: a geração pode ter sido cobrada.
        Assert.Equal(1, handler.Pedidos);
        Assert.Equal(0, esperas);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"demorou {sw.Elapsed}");
    }

    [Fact]
    public async Task CincoXX_NoCaminhoIdempotente_RepeteUmaVezEDevolveAResposta()
    {
        var handler = new Guiao(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, msg) = await cloud.SendLogAsync("linha de log", "1.0.0", null);

        Assert.True(ok);
        Assert.Equal(L10n.T("cloud.log.ok"), msg);
        Assert.Equal(2, handler.Pedidos);
        Assert.Equal(1, esperas);
    }

    [Fact]
    public async Task CincoXX_NaIa_NaoRepeteEDevolveOErro()
    {
        var handler = new Guiao(HttpStatusCode.ServiceUnavailable);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, err) = await cloud.GenerateAiReportAsync("token", new DeviceReport { Platform = "ios" });

        Assert.False(ok);
        // O 5xx pode vir depois da geração: repetir podia pagar duas vezes.
        Assert.StartsWith("503", err);
        Assert.Equal(1, handler.Pedidos);
        Assert.Equal(0, esperas);
    }
}
