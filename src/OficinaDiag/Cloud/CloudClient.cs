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
    /// <summary>
    /// <c>Idempotent</c>: repetir é seguro (intake tem Idempotency-Key, logs
    /// duplicados são inofensivos). <c>PaidOnce</c>: cada tentativa pode custar
    /// uma geração de IA — só repete quando o pedido não chegou a sair.
    /// </summary>
    private enum RetryMode { Idempotent, PaidOnce }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> build, TimeSpan attemptTimeout,
        RetryMode mode = RetryMode.Idempotent)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage res;
            using (var req = build())
            using (var cts = new CancellationTokenSource(attemptTimeout))
            {
                try { res = await Http.SendAsync(req, cts.Token); }
                catch (OperationCanceledException) when (attempt == 0
                    && mode == RetryMode.Idempotent)
                {
                    // Timeout num endpoint rápido vale uma segunda tentativa;
                    // nos 150 s da IA não — a geração já pode ter sido cobrada.
                    await Task.Delay(1500);
                    continue;
                }
                catch (HttpRequestException ex) when (attempt == 0 && IsPreSendFailure(ex, mode))
                {
                    await Task.Delay(1500);
                    continue;
                }
            }
            // 5xx → tenta outra vez só em endpoints idempotentes; o resto (4xx)
            // é definitivo. Na IA um 5xx pode vir depois da geração.
            if ((int)res.StatusCode >= 500 && attempt == 0 && mode == RetryMode.Idempotent)
            {
                res.Dispose();
                await Task.Delay(1500);
                continue;
            }
            return res;
        }
    }

    /// <summary>
    /// A falha aconteceu antes de algum byte do pedido ser enviado? Resolver o
    /// nome, abrir a ligação ou o handshake TLS acontecem antes do POST — esses
    /// casos podem repetir com segurança. Resposta cortada a meio, protocolo
    /// inválido ou 5xx já podem ter gerado (e cobrado) do lado da Cloud.
    /// </summary>
    private static bool IsPreSendFailure(HttpRequestException ex, RetryMode mode) =>
        mode == RetryMode.Idempotent
        || ex.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError;

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
            using var res = await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(BaseUri, $"intake/{Uri.EscapeDataString(shopCode.Trim().ToUpperInvariant())}"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }.WithIdempotencyKey(idempotencyKey), TimeSpan.FromSeconds(20));
            if (res.IsSuccessStatusCode)
                return (true, L10n.T("cloud.send.ok"));
            var body = await res.Content.ReadAsStringAsync();
            if ((int)res.StatusCode == 402)
                return (false, L10n.T("cloud.send.nomodule"));
            if ((int)res.StatusCode == 404)
                return (false, L10n.T("cloud.send.badcode"));
            return (false, L10n.F("cloud.status", (int)res.StatusCode, body));
        }
        catch (TaskCanceledException)
        {
            return (false, L10n.T("cloud.timeout"));
        }
        catch (HttpRequestException)
        {
            return (false, L10n.T("cloud.offline"));
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
            using var res = await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Post, new Uri(BaseUri, "diag-logs"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }, TimeSpan.FromSeconds(20));
            if (res.IsSuccessStatusCode)
                return (true, L10n.T("cloud.log.ok"));
            var body = await res.Content.ReadAsStringAsync();
            return (false, L10n.F("cloud.status", (int)res.StatusCode, body));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Troca um código de emparelhamento por um token com scope "diag" — o
    /// PC de bancada fica com um token que só serve relatórios IA; se for
    /// comprometido não pode publicar a montra nem mexer no WhatsApp da loja.
    /// </summary>
    public async Task<(bool ok, string token, string shopName, string error)> RedeemPairingAsync(string code)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                code = code.Trim().ToUpperInvariant(),
                scope = "diag",
                label = "oficinaos-diag",
            });
            using var res = await SendWithRetryAsync(() => new HttpRequestMessage(
                HttpMethod.Post, new Uri(BaseUri, "pairing/redeem"))
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }, TimeSpan.FromSeconds(20));
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                if (body.Contains("INVALID_OR_EXPIRED_CODE"))
                    return (false, "", "", L10n.T("cloud.pair.badcode"));
                if (body.Contains("RATE_LIMITED"))
                    return (false, "", "", L10n.T("cloud.pair.rate"));
                return (false, "", "", L10n.F("cloud.status", (int)res.StatusCode, body));
            }
            using var doc = JsonDocument.Parse(body);
            // Defesa em profundidade: scope declarado na resposta tem de
            // ser "diag". Ausente (cloud antiga) aceitamos — ver IntakeRules.
            if (doc.RootElement.TryGetProperty("scope", out var sc))
            {
                var scope = sc.ValueKind == JsonValueKind.String ? sc.GetString() : "";
                if (IntakeRules.RedeemScopeError(scope) is { } bad)
                    return (false, "", "", L10n.F("cloud.pair.scope", bad));
            }
            var token = doc.RootElement.GetProperty("shopToken").GetString() ?? "";
            var shopName = doc.RootElement.TryGetProperty("shopName", out var s) ? s.GetString() ?? "" : "";
            return (true, token, shopName, "");
        }
        catch (Exception ex) { return (false, "", "", ex.Message); }
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
            // Cada chamada gasta uma geração paga: PaidOnce só repete quando o
            // pedido não chegou a sair (DNS/ligação/TLS).
            using var res = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "reports/diagnostic"))
                {
                    Content = new StringContent(Reports.ReportBuilder.ToCloudJson(report, lang), Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", shopToken);
                return req;
            }, TimeSpan.FromSeconds(150), RetryMode.PaidOnce);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return (false, $"{(int)res.StatusCode} — {body}");
            using var doc = JsonDocument.Parse(body);
            return (true, doc.RootElement.GetProperty("report").GetString() ?? "");
        }
        catch (TaskCanceledException)
        {
            return (false, L10n.T("cloud.ai.timeout"));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
