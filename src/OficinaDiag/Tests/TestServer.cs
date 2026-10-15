using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OficinaDiag.Tests;

/// <summary>
/// Tiny HTTP server on the LAN that serves the on-device test page
/// (screen colours + touch grid) and receives its results back.
/// Pure LAN — no cloud, no account.
/// Raw TcpListener: HttpListener com prefixo "+" precisaria de reserva
/// urlacl/admin — socket simples basta aceitar a regra da firewall.
///
/// Porta aleatória + token por sessão no path — sem ele, qualquer um na LAN
/// podia POSTar resultados falsos para um relatório que vai à seguradora.
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _token = Guid.NewGuid().ToString("N")[..12];
    private Task? _loop;
    private bool _disposed;
    private volatile bool _done;
    private int _activeClients;

    // A página faz ~2 pedidos no total (GET + POST). 60/min por IP é folgado
    // para reloads e trava um flood de um peer na LAN. 16 handlers em
    // simultâneo chegam para telemóvel + browser de conferência.
    private const int MaxRequestsPerMinutePerIp = 60;
    private const int MaxConcurrentClients = 16;
    private readonly Dictionary<IPAddress, (DateTimeOffset windowStart, int count)> _rate = new();
    private readonly object _rateLock = new();

    public int Port =>
        _listener.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;

    /// <summary>Results posted back by the on-device test page.</summary>
    public event Action<Dictionary<string, JsonElement>>? ResultReceived;

    public TestServer()
    {
        // Bind ao IP LAN que anunciamos — não a Any. Com IPAddress.Any a
        // página também respondia nas outras interfaces da máquina (NIC
        // pública, VPN, adaptadores VMware). Só o telemóvel na LAN da loja
        // precisa de chegar aqui.
        _listener = new TcpListener(ResolveLanAddress() ?? IPAddress.Any, 0);
    }

    private static IPAddress? ResolveLanAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                && !a.ToString().StartsWith("169.254.")) // APIPA — sem rede
            // preferir a LAN típica da loja; VPNs/VMs ficam para o fim
            .OrderBy(a => a.ToString().StartsWith("192.168.") ? 0
                : a.ToString().StartsWith("10.") ? 1 : 2)
            .FirstOrDefault();

    public string LanUrl =>
        $"http://{ResolveLanAddress()?.ToString() ?? "localhost"}:{Port}/{_token}/";

    /// <summary>Returns false se a página de teste não existir junto ao exe.</summary>
    public bool Start()
    {
        if (!File.Exists(
                Path.Combine(AppContext.BaseDirectory, "Tests", "wwwroot", "device-test.html")))
            return false;
        _listener.Start();
        _loop = Task.Run(LoopAsync);
        return true;
    }

    private async Task LoopAsync()
    {
        var page = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Tests", "wwwroot", "device-test.html"));
        while (!_cts.IsCancellationRequested && !_done)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }

            _ = Task.Run(() => HandleClient(client, page));
        }
    }

    /// <summary>Cap de concorrência + rate por IP — a LAN não é sempre amigável.</summary>
    private bool AllowClient(TcpClient client)
    {
        if (Interlocked.Increment(ref _activeClients) > MaxConcurrentClients)
        {
            Interlocked.Decrement(ref _activeClients);
            return false;
        }
        if (client.Client.RemoteEndPoint is not IPEndPoint ep) return true;
        lock (_rateLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_rate.TryGetValue(ep.Address, out var r)
                && now - r.windowStart < TimeSpan.FromMinutes(1))
            {
                if (r.count >= MaxRequestsPerMinutePerIp)
                {
                    Interlocked.Decrement(ref _activeClients);
                    return false;
                }
                _rate[ep.Address] = (r.windowStart, r.count + 1);
            }
            else
            {
                _rate[ep.Address] = (now, 1);
            }
        }
        return true;
    }

    /// <summary>
    /// O teste é one-shot: depois do primeiro resultado válido a sessão
    /// morreu — fecha o listener para não ficar uma porta aberta na LAN.
    /// </summary>
    private void Complete()
    {
        if (_done) return;
        _done = true;
        try { _listener.Stop(); } catch { }
        // Dispose() pode já ter feito dispose do _cts — a corrida é benigna.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    // O JSON de resultado da página tem ~1–2 KB — 256 KB chega com folga e
    // impede um cliente da LAN de declarar um Content-Length gigante e
    // encher a memória (o body ia parar a uma lista sem teto).
    private const int MaxBodyBytes = 256 * 1024;

    private void HandleClient(TcpClient client, string page)
    {
        if (!AllowClient(client)) { try { client.Close(); } catch { } return; }
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                // Sem isto um socket que liga e nunca envia nada prendia uma
                // thread para sempre (scanner de portas, peers curiosos na LAN).
                client.ReceiveTimeout = 20_000;
                client.SendTimeout = 20_000;

                // Lê o pedido até ao fim dos headers (\r\n\r\n)
                var buf = new List<byte>();
                var b = new byte[4096];
                int headerEnd = -1;
                while (headerEnd < 0 && buf.Count < 64 * 1024)
                {
                    var n = stream.Read(b, 0, b.Length);
                    if (n <= 0) return;
                    for (var i = 0; i < n; i++) buf.Add(b[i]);
                    var text = Encoding.Latin1.GetString(buf.ToArray());
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0) break;
                }
                if (buf.Count == 0) return;

                var request = Encoding.Latin1.GetString(buf.ToArray());
                var firstLineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
                var firstLine = firstLineEnd > 0 ? request[..firstLineEnd] : request;
                var parts = firstLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "";
                var path = parts.Length > 1 ? parts[1] : "/";

                if (method == "POST" && path == $"/{_token}/result")
                {
                    // lê o corpo conforme Content-Length (com teto — ver const acima)
                    var lenMatch = System.Text.RegularExpressions.Regex.Match(
                        request, "Content-Length:\\s*(\\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!lenMatch.Success
                        || !int.TryParse(lenMatch.Groups[1].Value, out var contentLen)
                        || contentLen > MaxBodyBytes)
                    {
                        Respond(stream, "413 Content Too Large", "too large", "text/plain");
                        return;
                    }
                    var bodyStart = headerEnd + 4;
                    var have = buf.Count - bodyStart;
                    while (have < contentLen)
                    {
                        var n = stream.Read(b, 0, b.Length);
                        if (n <= 0) break;
                        for (var i = 0; i < n; i++) buf.Add(b[i]);
                        have += n;
                    }
                    var body = Encoding.UTF8.GetString(buf.ToArray(), bodyStart, Math.Min(contentLen, have));
                    var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                    Respond(stream, "200 OK", "ok", "text/plain");
                    // 200 primeiro — o cliente tem de ver o ACK; só depois se
                    // dispara o evento e se fecha o servidor (one-shot).
                    if (data is not null)
                    {
                        ResultReceived?.Invoke(data);
                        Complete();
                    }
                }
                else if (method == "GET" && path == $"/{_token}/")
                {
                    Respond(stream, "200 OK", page, "text/html; charset=utf-8");
                }
                else
                {
                    Respond(stream, "404 Not Found", "not found", "text/plain");
                }
            }
        }
        catch { }
        finally { Interlocked.Decrement(ref _activeClients); }
    }

    private static void Respond(NetworkStream stream, string status, string body, string contentType)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(header);
        stream.Write(payload);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
