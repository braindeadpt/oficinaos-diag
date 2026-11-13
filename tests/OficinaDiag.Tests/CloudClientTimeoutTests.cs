using System.Diagnostics;
using System.Net.Http;
using OficinaDiag.Cloud;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// T-04: o CloudClient não pode ficar pendurado num servidor que aceita a
/// ligação e nunca responde. Os 20 s dos endpoints rápidos desistem e valem
/// uma repetição; os 150 s da IA desistem sem repetir (cada tentativa pode
/// custar uma geração paga).
/// Sem relógio: um handler falso fica pendurado até o token do pedido ser
/// cancelado, e os timeouts entram acelerados pela seam interna. Esperar 20 s
/// ou 150 s num teste não é viável — os valores reais ficam provados pelos
/// campos por defeito que o próprio cliente usa.
/// </summary>
public sealed class CloudClientTimeoutTests
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

    private static CloudClient Cliente(Pendurado handler, TimeSpan fast, TimeSpan ai, Action onRetry)
    {
        var cfg = new DiagConfig { CloudUrl = "http://127.0.0.1:1" };
        return new CloudClient(cfg, new HttpMessageInvoker(handler), fast, ai, _ =>
        {
            onRetry();
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
        var cloud = Cliente(handler, TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(150),
            () => esperas++);

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
        var cloud = Cliente(handler, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(60),
            () => esperas++);

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
}
