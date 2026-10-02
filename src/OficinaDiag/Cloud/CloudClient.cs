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
    // 150s: AI reports carry crash logs and the LLM needs thinking time —
    // 30s was hit in the field. The other calls answer in ms anyway.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };

    public Uri BaseUri { get; set; }

    public CloudClient(DiagConfig config)
    {
        BaseUri = new Uri(config.CloudUrl.TrimEnd('/') + "/");
    }

    /// <summary>
    /// Customer-facing: POST a report to a shop by its public shop code.
    /// Needs the /intake/:shopCode endpoint on the cloud (unauthenticated,
    /// gated by the shop's diag-intake module).
    /// </summary>
    public async Task<(bool ok, string message)> SendToShopAsync(
        string shopCode, DeviceReport report, string name, string phone, string? email, string? aiReport, string? purpose)
    {
        try
        {
            var res = await Http.PostAsync(
                new Uri(BaseUri, $"intake/{Uri.EscapeDataString(shopCode.Trim().ToUpperInvariant())}"),
                new StringContent(Reports.ReportBuilder.ToIntakeJson(report, name, phone, email, aiReport, purpose), Encoding.UTF8, "application/json"));
            if (res.IsSuccessStatusCode)
                return (true, "Relatório enviado à loja — eles veem-no na app.");
            var body = await res.Content.ReadAsStringAsync();
            if ((int)res.StatusCode == 402)
                return (false, "Esta loja não aceita diagnósticos remotos (módulo inativo).");
            if ((int)res.StatusCode == 404)
                return (false, "Código de loja inválido — confirma com a loja.");
            return (false, $"Servidor respondeu {(int)res.StatusCode} — {body}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Opt-in support upload: sends the tail of the local diag.log so we can
    /// debug customer issues. Anonymous (no auth) — the server caps size and
    /// stores it only for our analysis.
    /// </summary>
    public async Task<(bool ok, string message)> SendLogAsync(
        string logTail, string appVersion, string? deviceHint)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                appVersion,
                deviceHint,
                log = logTail,
            });
            var res = await Http.PostAsync(
                new Uri(BaseUri, "diag-logs"),
                new StringContent(payload, Encoding.UTF8, "application/json"));
            if (res.IsSuccessStatusCode)
                return (true, "Log enviado — obrigado, ajuda-nos a corrigir.");
            var body = await res.Content.ReadAsStringAsync();
            return (false, $"Servidor respondeu {(int)res.StatusCode} — {body}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Technician-facing: AI report via the shop's cloud token (Pro module
    /// ai-reports). The token lives in the app's Settings once paired.
    /// </summary>
    public async Task<(bool ok, string reportOrError)> GenerateAiReportAsync(
        string shopToken, DeviceReport report, string lang = "pt")
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "reports/diagnostic"))
            {
                Content = new StringContent(Reports.ReportBuilder.ToCloudJson(report, lang), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", shopToken);
            var res = await Http.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, $"{(int)res.StatusCode} — {body}");
            using var doc = JsonDocument.Parse(body);
            return (true, doc.RootElement.GetProperty("report").GetString() ?? "");
        }
        catch (TaskCanceledException)
        {
            return (false, "O servidor de IA demorou demasiado a responder — tenta outra vez.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
