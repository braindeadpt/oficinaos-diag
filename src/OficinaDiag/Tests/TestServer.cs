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
/// </summary>
public sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int Port { get; } = 8734;

    /// <summary>Results posted back by the on-device test page.</summary>
    public event Action<Dictionary<string, JsonElement>>? ResultReceived;

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
        _listener.Prefixes.Add($"http://+:{Port}/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var page = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Tests", "wwwroot", "device-test.html"));
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            _ = Task.Run(() =>
            {
                try
                {
                    if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url?.AbsolutePath == "/result")
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        var body = reader.ReadToEnd();
                        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                        if (data is not null) ResultReceived?.Invoke(data);
                        Respond(ctx, "ok", "text/plain");
                    }
                    else
                    {
                        Respond(ctx, page, "text/html; charset=utf-8");
                    }
                }
                catch { }
            });
        }
    }

    private static void Respond(HttpListenerContext ctx, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
