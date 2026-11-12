using System.Net;
using System.Net.Sockets;
using OficinaDiag.Cloud;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// B-07/T-06: fechar o SettingsDialog a meio do emparelhamento cancela o
/// pedido. Um cancelamento não pode repetir (o retry só existe para timeout
/// pré-resposta) nem devolver mensagem de erro — e o chamador não chega a
/// gravar nada no <see cref="DiagConfig"/>.
/// </summary>
public sealed class CloudClientCancellationTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task RedeemCancelado_NaoRepeteNemTocaNaConfig()
    {
        var port = FreePort();
        var hits = 0;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        // Servidor lento de propósito: o pedido fica pendurado e é o
        // cancelamento (e não a resposta) que o termina.
        var server = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync();
                    Interlocked.Increment(ref hits);
                    await Task.Delay(5000);
                    ctx.Response.StatusCode = 200;
                    var body = "{\"shopToken\":\"t\",\"shopName\":\"loja\",\"scope\":\"diag\"}";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            }
            catch { /* listener parado ou pedido abortado pelo cliente */ }
        });

        var cfg = new DiagConfig { CloudUrl = $"http://127.0.0.1:{port}" };
        var cloud = new CloudClient(cfg);
        using var cts = new CancellationTokenSource();

        var call = cloud.RedeemPairingAsync("ABCD-EFGH", cts.Token);

        for (var i = 0; i < 300 && Volatile.Read(ref hits) == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, Volatile.Read(ref hits));

        cts.Cancel();
        var (ok, token, shop, error) = await call;

        Assert.False(ok);
        Assert.Equal("", token);
        Assert.Equal("", shop);
        // Cancelar não é uma falha para mostrar ao utilizador.
        Assert.Equal("", error);

        // Um retry do SendWithRetryAsync só apareceria depois de 1500 ms.
        await Task.Delay(2500);
        Assert.Equal(1, Volatile.Read(ref hits));

        // O diálogo só grava depois de um ok == true: nada disto pode mudar.
        // (Em Windows o SetShopToken deixaria ShopTokenScope == "diag".)
        Assert.Equal($"http://127.0.0.1:{port}", cfg.CloudUrl);
        Assert.Null(cfg.Language);
        Assert.Null(cfg.Theme);
        Assert.Null(cfg.ShopTokenProtected);
        Assert.Null(cfg.ShopTokenScope);

        listener.Stop();
        await Task.WhenAny(server, Task.Delay(2000));
    }
}
