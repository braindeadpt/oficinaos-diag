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
    /// Um retry com espera curta para falhas transitórias (rede/5xx/timeout).
    /// Atenção: a primeira tentativa pode ter chegado à Cloud mesmo quando
    /// vemos timeout/5xx. <paramref name="build"/> é chamado em cada tentativa,
    /// por isso quem precisa de deduplicação tem de pôr no pedido uma chave
    /// estável gerada UMA vez por envio (ver <see cref="IntakeRules.WithIdempotencyKey"/>):
    /// o POST /intake envia <c>Idempotency-Key</c> e a Cloud responde ao
    /// duplicado sem criar outro pedido. Os diag-logs não levam chave — um log
    /// duplicado é inofensivo.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> build)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            using (var req = build())
            {
                try { res = await Http.SendAsync(req); }
                catch (Exception) when (attempt == 0)
                {
                    await Task.Delay(1500);
                    continue;
                }
            }
            // 5xx → tenta outra vez; o resto (4xx) é definitivo.
            if ((int)res.StatusCode >= 500 && attempt == 0)
            {
                res.Dispose();
                await Task.Delay(1500);
                continue;
            }
            return res;
        }
    }

    /// <summary>
    /// Customer-facing: POST a report to a shop by its public shop code.
    /// Needs the /intake/:shopCode endpoint on the cloud (unauthenticated,
    /// gated by the shop's diag-intake module).
    /// </summary>
    public async Task<(bool ok, string message)> SendToShopAsync(
        string shopCode, DeviceReport report, string name, string phone, string? email, string? aiReport, string? purpose)
    {
        // Validação local espelhada da Cloud — erro legível em vez de 400 em bruto.
        if (email is not null && !IntakeRules.IsValidEmail(email))
            return (false, L10n.T("send.err.email"));
        if (!IntakeRules.NotesWithinLimit(report.Notes))
            return (false, L10n.F("err.notes.long", IntakeRules.NotesMaxLength));
        // Uma chave por envio, igual em todas as tentativas: se o 1.º POST
        // chegou e só a resposta se perdeu, o retry não cria um 2.º pedido.
        var idempotencyKey = IntakeRules.NewIdempotencyKey();
        var json = Reports.ReportBuilder.ToIntakeJson(report, name, phone, email?.Trim(), aiReport, purpose);
        try
        {
            var res = await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(BaseUri, $"intake/{Uri.EscapeDataString(shopCode.Trim().ToUpperInvariant())}"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }.WithIdempotencyKey(idempotencyKey));
            if (res.IsSuccessStatusCode)
                return (true, "Relatório enviado à loja — eles veem-no na app.");
            var body = await res.Content.ReadAsStringAsync();
            if ((int)res.StatusCode == 402)
                return (false, "Esta loja não aceita diagnósticos remotos (módulo inativo).");
            if ((int)res.StatusCode == 404)
                return (false, "Código de loja inválido — confirma com a loja.");
            return (false, $"Servidor respondeu {(int)res.StatusCode} — {body}");
        }
        catch (TaskCanceledException)
        {
            return (false, "O servidor demorou demasiado — verifica a net e tenta outra vez.");
        }
        catch (HttpRequestException)
        {
            return (false, "Sem ligação ao servidor — verifica a net e tenta outra vez.");
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
            var res = await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Post, new Uri(BaseUri, "diag-logs"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
            if (res.IsSuccessStatusCode)
                return (true, "Log enviado — obrigado, ajuda-nos a corrigir.");
            var body = await res.Content.ReadAsStringAsync();
            return (false, $"Servidor respondeu {(int)res.StatusCode} — {body}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Technician-facing: AI report via the shop's cloud token (Pro module
    /// ai-reports). O token vem das Opções (guardado cifrado) ou é pedido
    /// mascarado à vez — nunca fica em claro em ficheiros.
    /// </summary>
    public async Task<(bool ok, string reportOrError)> GenerateAiReportAsync(
        string shopToken, DeviceReport report, string lang = "pt")
    {
        if (!IntakeRules.NotesWithinLimit(report.Notes))
            return (false, L10n.F("err.notes.long", IntakeRules.NotesMaxLength));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "reports/diagnostic"))
            {
                Content = new StringContent(Reports.ReportBuilder.ToCloudJson(report, lang), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", shopToken);
            var res = await Http.SendAsync(req); // sem retry — cada chamada gasta uma geração paga
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
