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

    private readonly IPAddress _bindAddress;

    public int Port =>
        _listener.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;

    /// <summary>Results posted back by the on-device test page.</summary>
    public event Action<Dictionary<string, JsonElement>>? ResultReceived;

    /// <summary>
    /// True quando não havia IP de LAN e o servidor ficou em loopback — a UI
    /// avisa que o QR não vai abrir no telemóvel.
    /// </summary>
    public bool IsLoopbackOnly => _bindAddress.Equals(IPAddress.Loopback);

    public TestServer()
    {
        // Escuta em Any para servir os dois caminhos na mesma sessão:
        // 127.0.0.1 (túnel `adb reverse`, com Secure Context para o microfone)
        // e o IP LAN do QR (iPhone/Wi-Fi). O que não é loopback nem RFC1918 é
        // rejeitado em AllowClient — a página nunca fica exposta na NIC
        // pública nem numa VPN.
        _bindAddress = ResolveLanAddress() ?? IPAddress.Loopback;
        _listener = new TcpListener(IPAddress.Any, 0);
    }

    /// <summary>URL do túnel USB (`adb reverse tcp:<port>`) — sem Wi-Fi nenhum.</summary>
    public string LoopbackUrl => $"http://127.0.0.1:{Port}/{_token}/";

    /// <summary>
    /// Só loopback e RFC1918 (10/8, 172.16/12, 192.168/16). VPNs e NIC
    /// públicas ficam de fora: a página devolve resultados de hardware de um
    /// cliente e não pode andar na rede aberta.
    /// </summary>
    /// <summary>
    /// Filtro de origem aplicado antes de ler um único byte do pedido.
    /// Injetável porque não há IP público falso em localhost: os testes
    /// provam assim que a ligação é fechada sem receber a página.
    /// </summary>
    internal Func<IPAddress, bool> RemoteAllowed { get; set; } = IsAllowedRemote;

    internal static bool IsAllowedRemote(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Uma interface candidata a servir a página — dados puros para o ranking.</summary>
    public readonly record struct LanCandidate(
        string Name, string Description, NetworkInterfaceType Type, IPAddress Address, bool HasGateway);

    /// <summary>
    /// Nomes de adaptadores que parecem LAN mas não chegam à rede da loja:
    /// VirtualBox (192.168.56.1), VMware, Hyper-V, WSL2, Docker, VPNs. Sem esta
    /// lista, a interface virtual ganhava a eleição e o QR do telemóvel abria
    /// no vazio.
    /// </summary>
    private static readonly string[] VirtualHints =
    [
        "virtual", "vethernet", "wsl", "vmware", "vbox", "virtualbox", "hyper-v",
        "docker", "loopback", "tap", "tun", "tailscale", "zerotier", "wireguard",
    ];

    private static bool IsVirtual(LanCandidate c)
    {
        var text = $"{c.Name} {c.Description}".ToLowerInvariant();
        return VirtualHints.Any(text.Contains);
    }

    private static IPAddress? ResolveLanAddress() =>
        PickBestLanIp(NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n =>
            {
                var props = n.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any));
                return props.UnicastAddresses.Select(a => new LanCandidate(
                    n.Name, n.Description, n.NetworkInterfaceType, a.Address, hasGateway));
            }));

    /// <summary>
    /// Interfaces com gateway têm rota real para a rede; Wi-Fi antes de
    /// Ethernet; sem gateway só como último recurso. Se todas forem virtuais,
    /// ainda escolhe uma (loopback é o último recurso do construtor).
    /// </summary>
    internal static IPAddress? PickBestLanIp(IEnumerable<LanCandidate> candidates)
    {
        var list = candidates.ToList();
        return Rank(list.Where(c => !IsVirtual(c))) ?? Rank(list);

        static IPAddress? Rank(IEnumerable<LanCandidate> source) =>
            source
                .Where(c => c.Address.AddressFamily == AddressFamily.InterNetwork)
                .Where(c => !c.Address.ToString().StartsWith("169.254.")) // APIPA — sem rede
                .OrderByDescending(c => c.HasGateway)
                .ThenBy(c => c.Type switch
                {
                    NetworkInterfaceType.Wireless80211 => 0,
                    NetworkInterfaceType.Ethernet => 1,
                    _ => 2,
                })
                .ThenBy(c => c.Address.ToString().StartsWith("192.168.") ? 0
                    : c.Address.ToString().StartsWith("10.") ? 1 : 2)
                .Select(c => c.Address)
                .FirstOrDefault();
    }

    /// <summary>URL com o IP do bind real — se a NIC mudar depois do Start o QR não aponta para um IP que já não escuta.</summary>
    public string LanUrl =>
        $"http://{_bindAddress}:{Port}/{_token}/";

    /// <summary>Página servida ao telemóvel; no pacote é uma entrada de tools/package-files.txt.</summary>
    private static readonly string PagePath =
        Path.Combine(AppContext.BaseDirectory, PackagePaths.DeviceTestPageRelativePath);

    /// <summary>Returns false se a página de teste não existir junto ao exe.</summary>
    public bool Start()
    {
        if (!File.Exists(PagePath))
            return false;
        _listener.Start();
        _loop = Task.Run(LoopAsync);
        return true;
    }

    private async Task LoopAsync()
    {
        var page = File.ReadAllText(PagePath);
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
        if (!RemoteAllowed(ep.Address)) return false;
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

                // 64 KB sem \r\n\r\n: rejeita de imediato. Antes seguia com
                // headerEnd = -1 e bodyStart = 3, a processar headers como JSON.
                if (headerEnd < 0)
                {
                    Respond(stream, "431 Request Header Fields Too Large",
                        "headers too large", "text/plain");
                    return;
                }

                var raw = buf.ToArray();
                // Só headers: o regex do Content-Length não pode ver o body.
                var request = Encoding.Latin1.GetString(raw, 0, headerEnd);
                var firstLineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
                var firstLine = firstLineEnd > 0 ? request[..firstLineEnd] : request;
                var parts = firstLine.Split(' ');
                var method = parts.Length > 0 ? parts[0] : "";
                var rawPath = parts.Length > 1 ? parts[1] : "/";
                // Leitores de QR acrescentam ?from=qr e alguns browsers cortam
                // a barra final — os dois servem a mesma página.
                var cleanPath = rawPath.Split('?')[0].TrimEnd('/');

                if (method == "POST" && cleanPath == $"/{_token}/result")
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
                    var body = Encoding.UTF8.GetString(raw, bodyStart, Math.Min(contentLen, have));
                    var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                    // Shape check antes de fechar: a página envia sempre
                    // touchHit/touchTotal/colorsViewed. Um JSON qualquer de
                    // um peer da LAN (scanner, curl curioso) não pode matar
                    // a sessão antes de o telemóvel entregar o resultado.
                    if (!IsResultShape(data))
                    {
                        Respond(stream, "400 Bad Request", "unexpected payload", "text/plain");
                        return;
                    }
                    Respond(stream, "200 OK", "ok", "text/plain");
                    // 200 primeiro — o cliente tem de ver o ACK; só depois se
                    // dispara o evento e se fecha o servidor (one-shot).
                    ResultReceived?.Invoke(data!);
                    Complete();
                }
                else if (method == "GET" && cleanPath == $"/{_token}")
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

    /// <summary>
    /// A página de teste define sempre estes três campos numéricos — o mínimo
    /// que distingue um resultado real de ruído JSON de outro peer da LAN.
    /// </summary>
    private static bool IsResultShape(Dictionary<string, JsonElement>? data) =>
        data is not null
        && data.TryGetValue("touchHit", out var th) && th.ValueKind == JsonValueKind.Number
        && data.TryGetValue("touchTotal", out var tt) && tt.ValueKind == JsonValueKind.Number
        && data.TryGetValue("colorsViewed", out var cv) && cv.ValueKind == JsonValueKind.Number;

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
