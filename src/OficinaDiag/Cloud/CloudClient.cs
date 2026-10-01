using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OficinaDiag.Devices;

namespace OficinaDiag.Cloud;

/// <summary>
/// Talks to oficinaos-cloud. Everything here is opt-in (Pro): the free scan
/// path never touches the network.
/// </summary>
public sealed class CloudClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public Uri BaseUri { get; set; } = new("https://oficinaos-cloud.example.invalid/"); // definido nas settings

    /// <summary>
    /// Customer-facing: POST a report to a shop by its public shop code.
    /// Needs the /intake/:shopCode endpoint on the cloud (unauthenticated,
    /// rate-limited, code-gated).
    /// </summary>
    public async Task<(bool ok, string message)> SendToShopAsync(string shopCode, DeviceReport report)
    {
        try
        {
            var res = await Http.PostAsync(
                new Uri(BaseUri, $"intake/{Uri.EscapeDataString(shopCode.Trim().ToUpperInvariant())}"),
                new StringContent(Reports.ReportBuilder.ToCloudJson(report), Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode
                ? (true, "Relatório enviado à loja.")
                : (false, $"Servidor respondeu {(int)res.StatusCode} — {await res.Content.ReadAsStringAsync()}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Technician-facing: AI report via the shop's cloud token (Pro module
    /// ai-reports). The token lives in the app's Settings once paired.
    /// </summary>
    public async Task<(bool ok, string reportOrError)> GenerateAiReportAsync(string shopToken, DeviceReport report)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "reports/diagnostic"))
            {
                Content = new StringContent(Reports.ReportBuilder.ToCloudJson(report), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", shopToken);
            var res = await Http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, $"{(int)res.StatusCode} — {body}");
            using var doc = JsonDocument.Parse(body);
            return (true, doc.RootElement.GetProperty("report").GetString() ?? "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
