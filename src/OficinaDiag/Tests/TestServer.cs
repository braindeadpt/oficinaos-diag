using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OficinaDiag.Devices;

namespace OficinaDiag.Tests;

/// <summary>
/// Tiny HTTP server on the LAN that serves the on-device test page
/// (screen colours + touch grid) and receives its results back.
/// Pure LAN — no cloud, no account.
/// Raw TcpListener: HttpListener com prefixo "+" precisaria de reserva
/// urlacl/admin — socket simples basta aceitar a regra da firewall.
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; } = 8734;

    /// <summary>Results posted back by the on-device test page.</summary>
    public event Action<Dictionary<string, JsonElement>>? ResultReceived;

    public TestServer()
    {
        _listener = new TcpListener(IPAddress.Any, Port);
    }

    public string LanUrl
    {
        get
        {
            var ip = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return $"http://{ip?.ToString() ?? "localhost"}:{Port}/";
        }
    }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var page = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Tests", "wwwroot", "device-test.html"));
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }

            _ = Task.Run(() => HandleClient(client, page));
        }
    }

    private void HandleClient(TcpClient client, string page)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
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

                if (method == "POST" && path == "/result")
                {
                    // lê o corpo conforme Content-Length
                    var lenMatch = System.Text.RegularExpressions.Regex.Match(
                        request, "Content-Length:\\s*(\\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var contentLen = lenMatch.Success ? int.Parse(lenMatch.Groups[1].Value) : 0;
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
                    if (data is not null) ResultReceived?.Invoke(data);
                    Respond(stream, "200 OK", "ok", "text/plain");
                }
                else
                {
                    Respond(stream, "200 OK", page, "text/html; charset=utf-8");
                }
            }
            catch { }
        }
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
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
