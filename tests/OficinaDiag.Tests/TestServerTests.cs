using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>TestServer: eleição de interface LAN e parsing de pedidos HTTP.</summary>
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
