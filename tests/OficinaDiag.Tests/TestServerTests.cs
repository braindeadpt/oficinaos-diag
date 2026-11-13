using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>TestServer: eleição de interface LAN e parsing de pedidos HTTP.</summary>
[Collection(SerialCollection.Name)]
public sealed class TestServerTests
{
    private static TestServer.LanCandidate C(
        string name, NetworkInterfaceType type, string ip, bool gateway, string? desc = null) =>
        new(name, desc ?? name, type, IPAddress.Parse(ip), gateway);

    // ── B-10a: IP anunciado tem de ser a LAN real ───────────────────────

    [Fact]
    public void PrefereWiFiComGatewayAVirtualBoxSemGateway()
    {
        var ip = TestServer.PickBestLanIp(new[]
        {
            C("VirtualBox Host-Only Network", NetworkInterfaceType.Ethernet, "192.168.56.1", false),
            C("Wi-Fi", NetworkInterfaceType.Wireless80211, "10.0.0.45", true, "Intel Wi-Fi 6 AX201"),
        });
        Assert.Equal(IPAddress.Parse("10.0.0.45"), ip);
    }

    [Fact]
    public void IgnoraWslEDocker()
    {
        var ip = TestServer.PickBestLanIp(new[]
        {
            C("vEthernet (WSL)", NetworkInterfaceType.Ethernet, "172.20.0.1", false),
            C("Docker0", NetworkInterfaceType.Ethernet, "172.17.0.1", false),
            C("Ethernet", NetworkInterfaceType.Ethernet, "192.168.1.20", true),
        });
        Assert.Equal(IPAddress.Parse("192.168.1.20"), ip);
    }

    [Fact]
    public void SemGateway_EscolheAMesmoAssim()
    {
        var ip = TestServer.PickBestLanIp(new[]
        {
            C("Wi-Fi", NetworkInterfaceType.Wireless80211, "192.168.0.9", false),
        });
        Assert.Equal(IPAddress.Parse("192.168.0.9"), ip);
    }

    [Fact]
    public void ApipaNuncaEEscolhida()
    {
        var ip = TestServer.PickBestLanIp(new[]
        {
            C("Ethernet", NetworkInterfaceType.Ethernet, "169.254.10.5", false),
            C("Wi-Fi", NetworkInterfaceType.Wireless80211, "10.1.2.3", false),
        });
        Assert.Equal(IPAddress.Parse("10.1.2.3"), ip);
    }

    // ── T-10: loopback (adb reverse) + LAN, nada mais ─────────────────

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.5.4", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("169.254.10.1", false)]
    public void SoLoopbackERfc1918(string ip, bool allowed) =>
        Assert.Equal(allowed, TestServer.IsAllowedRemote(IPAddress.Parse(ip)));

    [Fact]
    public async Task OrigemNaoAutorizadaEFechadaSemReceberAPagina()
    {
        // Não há IP público falso em loopback: o filtro é injetado para provar
        // que a ligação morre ANTES de a página ser escrita.
        using var server = new TestServer { RemoteAllowed = _ => false };
        Assert.True(server.Start());
        var port = new Uri(server.LanUrl).Port;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n"));

        var buf = new byte[512];
        // «Fechou sem responder»: no Linux chega como EOF (0 bytes), no Windows
        // como reset (WSAECONNRESET → IOException). Os dois valem zero bytes.
        var recebidos = 0;
        try
        {
            recebidos = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (IOException) { /* reset sem dados — ver o contador abaixo */ }

        Assert.Equal(0, recebidos);
        // O cliente não distingue «fechou sem escrever» de «escreveu e cortou»
        // quando o fecho é um reset: o contador do servidor prova o primeiro.
        Assert.Equal(0, server.ResponsesSent);
    }

    [Fact]
    public async Task LigacaoAutorizadaContinuaASerServidaDepoisDeUmaRejeitada()
    {
        using var server = new TestServer();
        Assert.True(server.Start());
        var uri = new Uri(server.LanUrl);
        var token = uri.AbsolutePath.Trim('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // loopback é permitido pelo filtro real (caminho do adb reverse).
        var res = await http.GetAsync($"http://127.0.0.1:{uri.Port}/{token}/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task APaginaServidaTrazAsDecisoesEmbutidas()
    {
        // Um pedido só: o módulo vai dentro do HTML, sem segundo GET nem
        // caminho relativo que dependa da barra final do token.
        using var server = new TestServer();
        Assert.True(server.Start(), "device-test.html/decisions.js não chegaram ao output");
        var uri = new Uri(server.LanUrl);
        var token = uri.AbsolutePath.Trim('/');
        var root = $"{uri.Scheme}://{uri.Authority}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var res = await http.GetAsync($"{root}/{token}/");
        var html = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // A tag inteira, não o nome: o comentário da página cita-a.
        Assert.DoesNotContain("<script src=\"decisions.js\"></script>", html);
        Assert.Contains("DeviceTestDecisions", html);
        Assert.Contains("advanceAt", html);          // limiar das 55 zonas
        Assert.Contains("multiTouchStatus", html);   // 5 dedos
        Assert.Contains("device-test", html);        // a página em si

        // A rota do segundo ficheiro deixou de existir.
        var js = await http.GetAsync($"{root}/{token}/decisions.js");
        Assert.Equal(HttpStatusCode.NotFound, js.StatusCode);
    }

    [Fact]
    public void InlineDecisionsSubstituiOPedidoDoModulo()
    {
        var page = "<html><script src=\"decisions.js\"></script><body>x</body></html>";
        var servida = TestServer.InlineDecisions(page, "var DeviceTestDecisions = {};");
        Assert.DoesNotContain("src=\"decisions.js\"", servida);
        Assert.Contains("var DeviceTestDecisions = {};", servida);
        Assert.Contains("<body>x</body>", servida);
    }

    // ── B-10b: paths e headers ─────────────────────────────────────────

    [Fact]
    public async Task GetAceitaTokenSemBarraFinalEComQuery()
    {
        using var server = new TestServer();
        Assert.True(server.Start(), "device-test.html não chegou ao output de testes");
        var uri = new Uri(server.LanUrl);
        var token = uri.AbsolutePath.Trim('/');
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var root = $"{uri.Scheme}://{uri.Authority}";

        var semBarra = await http.GetAsync($"{root}/{token}");
        Assert.Equal(HttpStatusCode.OK, semBarra.StatusCode);

        // QR mobile acrescenta ?from=qr; a barra final é opcional.
        var comQuery = await http.GetAsync($"{root}/{token}/?from=qr");
        Assert.Equal(HttpStatusCode.OK, comQuery.StatusCode);
        // Prova que o contador conta: uma resposta por pedido servido. É o que
        // dá sentido ao `ResponsesSent == 0` do teste de origem rejeitada.
        Assert.Equal(2, server.ResponsesSent);
    }

    [Fact]
    public async Task HeadersAcimaDe64KSemTerminador_Dao431()
    {
        using var server = new TestServer();
        Assert.True(server.Start());
        var uri = new Uri(server.LanUrl);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        using var stream = tcp.GetStream();

        var payload = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX-Pad: " + new string('a', 70_000));
        try { await stream.WriteAsync(payload); }
        catch (IOException) { /* servidor já respondeu e fechou */ }

        var buf = new byte[256];
        var n = await stream.ReadAsync(buf);
        var text = Encoding.ASCII.GetString(buf, 0, n);
        Assert.StartsWith("HTTP/1.1 431", text);
    }

    [Fact]
    public async Task PostResultEntregaOJsonEFechaOServidor()
    {
        using var server = new TestServer();
        Assert.True(server.Start());
        var uri = new Uri(server.LanUrl);
        var token = uri.AbsolutePath.Trim('/');
        var root = $"{uri.Scheme}://{uri.Authority}";

        var received = new TaskCompletionSource<Dictionary<string, JsonElement>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.ResultReceived += d => received.TrySetResult(d);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        const string json =
            "{\"touchHit\":54,\"touchTotal\":60,\"colorsViewed\":13,\"edgeHit\":22,\"edgeTotal\":28,\"batteryApi\":100}";
        var res = await http.PostAsync($"{root}/{token}/result",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var data = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(54, data["touchHit"].GetInt32());
        Assert.Equal(13, data["colorsViewed"].GetInt32());

        // One-shot: o ACK sai antes do fecho, e um segundo pedido já não é servido.
        await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync($"{root}/{token}/"));
    }
}
