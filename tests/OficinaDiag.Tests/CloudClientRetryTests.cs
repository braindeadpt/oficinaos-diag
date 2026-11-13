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

    /// <summary>Falha a primeira tentativa como a rede falha; depois responde.</summary>
    private sealed class Queda(HttpRequestError erro) : HttpMessageHandler
    {
        public int Pedidos;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Pedidos) == 1)
                throw new HttpRequestException(erro, "sem ligação");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"report\":\"laudo\"}"),
            });
        }
    }

    private static readonly TimeSpan LimiteDoTeste = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Um ciclo de repetições falha com esta mensagem em segundos, em vez de
    /// prender a suite até o runner desistir sem dizer porquê.
    /// </summary>
    private static async Task<T> ComLimite<T>(Task<T> tarefa)
    {
        try
        {
            return await tarefa.WaitAsync(LimiteDoTeste);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"o CloudClient não terminou em {LimiteDoTeste.TotalSeconds} s — repetições sem limite?");
        }
    }

    private static CloudClient Cliente(
        HttpMessageHandler handler, TimeSpan? fast = null, TimeSpan? ai = null, Action? onRetry = null)
    {
        var cfg = new DiagConfig { CloudUrl = "http://127.0.0.1:1" };
        return new CloudClient(cfg, new HttpMessageInvoker(handler),
            fast ?? TimeSpan.FromSeconds(20), ai ?? TimeSpan.FromSeconds(150), async _ =>
            {
                onRetry?.Invoke();
                // 1 ms em vez dos 1,5 s reais: o teste não espera, e um ciclo
                // de repetições (o defeito que o limite procura) fica travado
                // o suficiente para o limite de 5 s conseguir disparar.
                await Task.Delay(1);
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
        var (ok, msg) = await ComLimite(cloud.SendToShopAsync(
            "LOJA", new DeviceReport { Platform = "ios" }, "Ana", "912345678", null, null, null));
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
        var (ok, err) = await ComLimite(cloud.GenerateAiReportAsync("token", new DeviceReport { Platform = "ios" }));
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

        var (ok, msg) = await ComLimite(cloud.SendLogAsync("linha de log", "1.0.0", null));

        Assert.True(ok);
        Assert.Equal(L10n.T("cloud.log.ok"), msg);
        Assert.Equal(2, handler.Pedidos);
        Assert.Equal(1, esperas);
    }

    // O guião repete o último código sem fim. Se o cliente insistisse, o
    // timeout de 20 s acabava por o travar — daí só as contagens provarem.
    [Theory]
    [InlineData(400, 1, 0)] // definitivo: repetir dava o mesmo
    [InlineData(503, 2, 1)] // repete uma vez, não faz ciclo
    public async Task ErroDoServidor_NoCaminhoIdempotente_RespeitaAsRepeticoes(
        int codigo, int pedidos, int esperasEsperadas)
    {
        var handler = new Guiao((HttpStatusCode)codigo);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, msg) = await ComLimite(cloud.SendLogAsync("linha de log", "1.0.0", null));

        Assert.False(ok);
        Assert.Equal(L10n.F("cloud.status", codigo, "indisponivel"), msg);
        Assert.Equal(pedidos, handler.Pedidos);
        Assert.Equal(esperasEsperadas, esperas);
    }

    [Fact]
    public async Task ExcecaoDeRede_NoCaminhoIdempotente_RepeteEDevolveAResposta()
    {
        // Qualquer HttpRequestException vale uma repetição: o endpoint é
        // idempotente e repetir nunca custa dinheiro.
        var handler = new Queda(HttpRequestError.Unknown);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, msg) = await ComLimite(cloud.SendLogAsync("linha de log", "1.0.0", null));

        Assert.True(ok);
        Assert.Equal(L10n.T("cloud.log.ok"), msg);
        Assert.Equal(2, handler.Pedidos);
        Assert.Equal(1, esperas);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task ExcecaoAntesDeEnviar_NaIa_Repete(HttpRequestError erro)
    {
        var handler = new Queda(erro);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, report) = await ComLimite(cloud.GenerateAiReportAsync("token", new DeviceReport { Platform = "ios" }));

        // Pedido nunca saiu (DNS/ligação/TLS): repetir não pode cobrar duas vezes.
        Assert.True(ok);
        Assert.Equal("laudo", report);
        Assert.Equal(2, handler.Pedidos);
        Assert.Equal(1, esperas);
    }

    [Fact]
    public async Task ExcecaoDepoisDeEnviar_NaIa_NaoRepete()
    {
        // Resposta cortada a meio: o pedido já pode ter gerado o relatório.
        var handler = new Queda(HttpRequestError.ResponseEnded);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, err) = await ComLimite(cloud.GenerateAiReportAsync("token", new DeviceReport { Platform = "ios" }));

        Assert.False(ok);
        Assert.Equal("sem ligação", err);
        Assert.Equal(1, handler.Pedidos);
        Assert.Equal(0, esperas);
    }

    [Fact]
    public async Task CincoXX_NaIa_NaoRepeteEDevolveOErro()
    {
        var handler = new Guiao(HttpStatusCode.ServiceUnavailable);
        var esperas = 0;
        var cloud = Cliente(handler, onRetry: () => esperas++);

        var (ok, err) = await ComLimite(cloud.GenerateAiReportAsync("token", new DeviceReport { Platform = "ios" }));

        Assert.False(ok);
        // O 5xx pode vir depois da geração: repetir podia pagar duas vezes.
        Assert.Equal("503 — indisponivel", err);
        Assert.Equal(1, handler.Pedidos);
        Assert.Equal(0, esperas);
    }
}
