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
/// Sem relógio: a resposta do servidor é segurada por um TCS e o atraso entre
/// tentativas é injetado, por isso um retry apareceria como contador, não
/// como espera de 1,5 s.
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
        var retries = 0;
        var firstHit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                Interlocked.Increment(ref hits);
                firstHit.TrySetResult();
                // Só o cancelamento (ou o fim do teste) solta esta resposta.
                await release.Task;
                ctx.Response.StatusCode = 200;
                var body = System.Text.Encoding.UTF8.GetBytes(
                    "{\"shopToken\":\"t\",\"shopName\":\"loja\",\"scope\":\"diag\"}");
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
            catch { /* listener parado ou pedido abortado pelo cliente */ }
        });

        var cfg = new DiagConfig { CloudUrl = $"http://127.0.0.1:{port}" };
        var cloud = new CloudClient(cfg, _ =>
        {
            // Qualquer retry passa por aqui antes de repetir o pedido.
            Interlocked.Increment(ref retries);
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();

        var call = cloud.RedeemPairingAsync("ABCD-EFGH", cts.Token);
        await firstHit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref hits));

        cts.Cancel();
        var (ok, token, shop, error) = await call.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(ok);
        Assert.Equal("", token);
        Assert.Equal("", shop);
        // Cancelar não é uma falha para mostrar ao utilizador.
        Assert.Equal("", error);

        // Determinístico: o hook de retry nunca foi chamado e o servidor viu
        // exatamente um pedido. Não há janela de tempo para "esperar para ver".
        Assert.Equal(0, Volatile.Read(ref retries));
        Assert.Equal(1, Volatile.Read(ref hits));

        // O diálogo só grava depois de um ok == true: nada disto pode mudar.
        // (Em Windows o SetShopToken deixaria ShopTokenScope == "diag".)
        Assert.Equal($"http://127.0.0.1:{port}", cfg.CloudUrl);
        Assert.Null(cfg.Language);
        Assert.Null(cfg.Theme);
        Assert.Null(cfg.ShopTokenProtected);
        Assert.Null(cfg.ShopTokenScope);

        release.TrySetResult();
        listener.Stop();
        await Task.WhenAny(server, Task.Delay(2000));
    }
}
