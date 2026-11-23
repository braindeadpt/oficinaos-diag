using System.Net;
using System.Net.Http;
using System.Text.Json;
using OficinaDiag.Cloud;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

public sealed class CloudClientAiReportTests
{
    private sealed class Captura : HttpMessageHandler
    {
        public string? Body;
        public string? Auth;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Auth = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"report\":\"ok\"}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task Maintenance_EnviaPurposeSemGravarNoRelatorio()
    {
        var handler = new Captura();
        var cloud = new CloudClient(new DiagConfig { CloudUrl = "http://127.0.0.1:1" },
            new HttpMessageInvoker(handler), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            _ => Task.CompletedTask);
        var report = new DeviceReport { Platform = "android" };

        var (ok, text) = await cloud.GenerateAiReportAsync("token", report, "pt", "maintenance");

        Assert.True(ok);
        Assert.Equal("ok", text);
        Assert.Equal("Bearer token", handler.Auth);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("maintenance", doc.RootElement.GetProperty("purpose").GetString());
        Assert.Null(report.Purpose);
    }
}
