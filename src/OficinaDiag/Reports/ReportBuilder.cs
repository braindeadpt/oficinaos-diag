using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Markdig;
using OficinaDiag.Devices;

namespace OficinaDiag.Reports;

/// <summary>
/// Renders a DeviceReport as a self-contained retro-styled HTML file
/// (printable → PDF) plus the raw JSON that the cloud endpoints accept.
/// </summary>
public static class ReportBuilder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Pipeline do relatório IA: CommonMark como antes, mas com HTML em bruto desligado.</summary>
    internal static readonly Markdig.MarkdownPipeline AiMarkdownPipeline =
        new Markdig.MarkdownPipelineBuilder().DisableHtml().Build();

    public static string ToJson(DeviceReport r) => JsonSerializer.Serialize(r, JsonOpts);

    /// <summary>Payload for cloud /reports/diagnostic (AI report, shop-side).</summary>
    public static string ToCloudJson(DeviceReport r, string lang = "pt")
    {
        var payload = new
        {
            lang,
            // "repair" | "sale" — o mesmo enquadramento que o intake e a app usam.
            purpose = r.Purpose is "sale" or "repair" ? r.Purpose : null,
            device = new { brand = r.Device.Brand, model = r.Device.Model, os = r.Device.Os, osVersion = r.Device.OsVersion },
            results = r.Results,
            logs = r.LogsText,
            notes = r.Notes,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>Payload for cloud POST /intake/:shopCode (customer → shop).</summary>
    public static string ToIntakeJson(DeviceReport r, string name, string phone, string? email, string? aiReport, string? purpose)
    {
        var payload = new
        {
            customerName = name,
            customerPhone = phone,
            customerEmail = email,
            aiReport,
            purpose,
            device = new
            {
                brand = r.Device.Brand,
                model = r.Device.Model,
                os = r.Device.Os,
                osVersion = r.Device.OsVersion,
                serial = r.Device.Serial,
            },
            results = r.Results,
            notes = r.Notes,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    public static string ToHtml(DeviceReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
        <!DOCTYPE html><html lang="pt"><head><meta charset="utf-8">
        <title>OficinaDiag — relatório</title><style>
        body{background:#0a0f0a;color:#33ff33;font-family:'Cascadia Mono',Consolas,monospace;max-width:760px;margin:2em auto;padding:0 1em}
        h1{border-bottom:1px solid #33ff33;padding-bottom:.4em}
        .meta{color:#7fd77f;font-size:.85em;margin-bottom:2em}
        table{width:100%;border-collapse:collapse}
        td,th{border:1px solid #1d4a1d;padding:.4em .7em;text-align:left;font-size:.9em}
        th{background:#0d1f0d}
        .pass{color:#33ff33}.warn{color:#ffd23f}.fail{color:#ff5555}.info{color:#7fd77f}.skipped{color:#666}
        .footer{margin-top:3em;font-size:.75em;color:#4a7a4a}
        @media print{body{background:#fff;color:#000}.meta,.footer{color:#555}th{background:#eee}.pass,.info{color:#000}.warn{color:#a60}.fail{color:#c00}}
        </style></head><body>
        """);

        sb.AppendLine("<h1>OFICINA-OS // DIAG</h1>");
        sb.AppendLine($"<div class=\"meta\">{System.Net.WebUtility.HtmlEncode(r.Device.Brand)} " +
            $"{System.Net.WebUtility.HtmlEncode(r.Device.Model)} · {System.Net.WebUtility.HtmlEncode(r.Device.Os)} " +
            $"{System.Net.WebUtility.HtmlEncode(r.Device.OsVersion)} · s/n {System.Net.WebUtility.HtmlEncode(r.Device.Serial)}<br>" +
            $"recolhido {r.CollectedAt:dd-MM-yyyy HH:mm} UTC · tool v{r.ToolVersion}</div>");

        // Bloco de grau + veredicto no topo — a frase de fecho que a loja lê primeiro.
        if (r.Results.TryGetValue("grade.overall", out var g))
        {
            var gradeClass = g.Status == "fail" ? "fail" : g.Status == "warn" ? "warn" : "pass";
            sb.AppendLine($"<div style=\"border:2px solid;padding:.8em;margin-bottom:1.5em\" class=\"{gradeClass}\">" +
                $"<b style=\"font-size:1.6em\">GRAU {System.Net.WebUtility.HtmlEncode(g.Value)}</b><br>" +
                $"{System.Net.WebUtility.HtmlEncode(g.Detail ?? "")}</div>");
        }

        sb.AppendLine("<table><tr><th>verificação</th><th>estado</th><th>valor</th><th>detalhe</th></tr>");
        foreach (var (key, c) in r.Results.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"<tr><td>{System.Net.WebUtility.HtmlEncode(key)}</td>" +
                $"<td class=\"{c.Status}\">{c.Status.ToUpperInvariant()}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(c.Value ?? "—")}</td>" +
                $"<td>{System.Net.WebUtility.HtmlEncode(c.Detail ?? "")}</td></tr>");
        }
        sb.AppendLine("</table>");
        if (!string.IsNullOrWhiteSpace(r.LogsText))
            sb.AppendLine($"<h1 style=\"font-size:1em\">LOGS DE CRASH/PANIC</h1>" +
                $"<pre style=\"font-size:.75em;white-space:pre-wrap;max-height:300px;overflow:auto\">" +
                $"{System.Net.WebUtility.HtmlEncode(r.LogsText)}</pre>");
        if (!string.IsNullOrWhiteSpace(r.Notes))
            sb.AppendLine($"<h1 style=\"font-size:1em\">NOTAS</h1><p>{System.Net.WebUtility.HtmlEncode(r.Notes)}</p>");
        AppendPhotos(sb, r);
        sb.AppendLine("<div class=\"footer\">oficinaos-diag · open source (MIT) · relatório gerado localmente</div></body></html>");
        return sb.ToString();
    }

    /// <summary>Fotos de bancada embutidas em base64 — o HTML fica autocontido.</summary>
    private static void AppendPhotos(StringBuilder sb, DeviceReport r)
    {
        if (r.PhotoPaths.Count == 0) return;
        var imgs = new StringBuilder();
        foreach (var path in r.PhotoPaths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var mime = ext is ".png" ? "image/png" : ext is ".webp" ? "image/webp" : "image/jpeg";
                var b64 = Convert.ToBase64String(File.ReadAllBytes(path));
                imgs.Append($"<img src=\"data:{mime};base64,{b64}\" " +
                    "style=\"max-width:230px;margin:4px;border:1px solid #1d4a1d\">");
            }
            catch { /* foto inacessível — salta */ }
        }
        if (imgs.Length > 0)
            sb.AppendLine($"<h1 style=\"font-size:1em\">FOTOS DE BANCADA</h1><div>{imgs}</div>");
    }

    /// <summary>
    /// Renders the AI-generated markdown report as a self-contained HTML file
    /// with the same retro style — printable/handable to the customer.
    /// </summary>
    public static string AiReportToHtml(DeviceReport r, string markdown)
    {
        // O texto vem de um LLM alimentado por dados do dispositivo (nome,
        // crash logs) → possível prompt injection. Sem HTML em bruto: tags
        // e blocos HTML saem escapados, nunca como <script>/<img onerror>.
        var body = Markdig.Markdown.ToHtml(markdown, AiMarkdownPipeline);
        return $$"""
        <!DOCTYPE html><html lang="pt"><head><meta charset="utf-8">
        <title>OficinaDiag — relatório IA</title><style>
        body{background:#0a0f0a;color:#33ff33;font-family:'Cascadia Mono',Consolas,monospace;max-width:760px;margin:2em auto;padding:0 1em}
        h1{border-bottom:1px solid #33ff33;padding-bottom:.4em}
        h2,h3{color:#7fd77f;font-size:1em;margin-top:1.6em}
        .meta{color:#7fd77f;font-size:.85em;margin-bottom:2em}
        table{width:100%;border-collapse:collapse}
        td,th{border:1px solid #1d4a1d;padding:.4em .7em;text-align:left;font-size:.9em}
        th{background:#0d1f0d}
        li{margin:.3em 0}
        .footer{margin-top:3em;font-size:.75em;color:#4a7a4a}
        @media print{body{background:#fff;color:#000}.meta,.footer,h2,h3{color:#555}th{background:#eee} }
        </style></head><body>
        <h1>OFICINA-OS // RELATÓRIO IA</h1>
        <div class="meta">{{System.Net.WebUtility.HtmlEncode(r.Device.Brand)}} {{System.Net.WebUtility.HtmlEncode(r.Device.Model)}} · {{System.Net.WebUtility.HtmlEncode(r.Device.Os)}} {{System.Net.WebUtility.HtmlEncode(r.Device.OsVersion)}} · s/n {{System.Net.WebUtility.HtmlEncode(r.Device.Serial)}}<br>
        gerado {{r.CollectedAt:dd-MM-yyyy HH:mm}} UTC · tool v{{r.ToolVersion}} · via OficinaOS Cloud</div>
        {{body}}
        <div class="footer">oficinaos-diag · relatório gerado por IA — valida sempre com inspeção física</div></body></html>
        """;
    }

    /// <summary>Chave técnica → rótulo legível para o documento da seguradora.</summary>
    private static string FriendlyLabel(string key) => key switch
    {
        "battery.capacity" => "Bateria — capacidade",
        "battery.capacity_learned" => "Bateria — capacidade estimada",
        "battery.charge_now" => "Bateria — carga atual",
        "battery.charger" => "Bateria — carregador",
        "battery.current" => "Bateria — corrente",
        "battery.cycles" => "Bateria — ciclos",
        "battery.health" => "Bateria — estado de saúde",
        "battery.level" => "Bateria — nível",
        "battery.mfg" => "Bateria — fabricante",
        "battery.serial" => "Bateria — nº de série",
        "battery.status" => "Bateria — estado",
        "battery.technology" => "Bateria — tecnologia",
        "battery.temp" or "battery.temperature" => "Bateria — temperatura",
        "battery.voltage" => "Bateria — tensão",
        "checklist.frame" => "Inspeção física — moldura / corpo",
        "checklist.backglass" => "Inspeção física — vidro traseiro",
        "checklist.screen_visual" => "Inspeção física — ecrã (riscos / manchas)",
        "checklist.buttons" => "Inspeção física — botões",
        "checklist.sim_tray" => "Inspeção física — gaveta SIM",
        "checklist.charge_port" => "Inspeção física — porta de carga",
        "checklist.speaker" => "Inspeção física — altifalante",
        "checklist.earpiece" => "Inspeção física — auricular",
        "checklist.biometrics" => "Inspeção física — biometria (Face/Touch ID)",
        "checklist.cameras" => "Inspeção física — câmaras",
        "checklist.liquid" => "Inspeção física — sinais de líquido / humidade",
        "checklist.mic" => "Inspeção física — microfone",
        "device.activation" => "Bloqueio de ativação (iCloud)",
        "device.baseband" => "Modem (baseband)",
        "device.bluetooth" => "Bluetooth",
        "device.sim" => "Cartão SIM",
        "device.telephony" => "Rede móvel",
        "device.wifi" => "Wi-Fi",
        "display" or "screen.specs" => "Ecrã",
        "grade.overall" => "Classificação geral (grau)",
        "identity.color" => "Cor",
        "identity.fingerprint" => "Fingerprint do sistema",
        "identity.iccid" => "ICCID",
        "identity.imei" => "IMEI",
        "identity.model" => "Modelo",
        "identity.partno" => "Part number",
        "identity.region" => "Região",
        "identity.regmodel" => "Modelo regulamentar",
        "identity.serial" => "Nº de série",
        "logs.crashes" => "Registos de crash",
        "logs.panics" => "Registos de panic (hardware)",
        "memory" => "Memória",
        "os.version" => "Versão do sistema operativo",
        "power.charge" => "Carregamento",
        "power.resistance" => "Resistência de carga",
        "power.session" => "Sessão de potência",
        "power.temp" => "Temperatura em carga",
        "power.thermal" => "Aquecimento — célula vs SoC",
        "scan" => "Diagnóstico",
        "security.accounts" => "Contas no aparelho",
        "security.bootloader" => "Bootloader",
        "security.build" => "Build do sistema",
        "security.crypto" => "Encriptação",
        "security.findmy" => "Find My iPhone",
        "security.mdm" => "Gestão MDM",
        "security.oemunlock" => "Desbloqueio OEM",
        "security.password" => "Código de bloqueio",
        "security.root" => "Root / jailbreak",
        "security.selinux" => "SELinux",
        "security.warranty" => "Bit de garantia (Knox)",
        "sensors" => "Sensores",
        "sensor.accel" => "Acelerómetro",
        "sensor.gyro" => "Giroscópio",
        "sensor.light" => "Sensor de luz ambiente",
        "sensor.multitouch" => "Multi-toque",
        "sensor.orient" => "Sensor de orientação",
        "sensor.vibrate" => "Motor de vibração",
        "sensor.speaker" => "Altifalante",
        "storage" => "Armazenamento",
        "storage.wear" => "Armazenamento — desgaste (NAND)",
        "suggest.parts" => "Peças prováveis",
        "test.screen" => "Ecrã — teste visual",
        "test.touch" => "Toque — teste funcional",
        "test.edges" => "Toque — zonas de borda",
        "usb.stability" => "Ligação USB — estabilidade",
        _ => key,
    };

    /// <summary>Estado legível para o cliente — sem jargão.</summary>
    private static string ClientStatus(string status) => status switch
    {
        "pass" => "OK",
        "warn" => "Atenção",
        "fail" => "Problema",
        "skipped" => "Não verificado",
        _ => "Info",
    };

    /// <summary>
    /// Vista cliente do mesmo relatório — tema claro, grau e veredicto em
    /// destaque, checklist físico e fotos. Sem chaves técnicas nem logs:
    /// é o papel que sai com o cliente, não a grelha da bancada.
    /// </summary>
    public static string ToClientHtml(DeviceReport r)
    {
        var e = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var model = string.IsNullOrWhiteSpace(r.Device.MarketingName)
            ? r.Device.Model
            : r.Device.MarketingName;
        var grade = r.Results.TryGetValue("grade.overall", out var g) ? g : null;
        var parts = r.Results.TryGetValue("suggest.parts", out var p) ? p.Value : null;

        var sb = new StringBuilder();
        sb.AppendLine("""
        <!DOCTYPE html><html lang="pt"><head><meta charset="utf-8">
        <title>Relatório do equipamento</title><style>
        body{font-family:'Segoe UI',Arial,sans-serif;color:#1a1a1a;max-width:720px;margin:2em auto;padding:0 1.5em}
        h1{font-size:1.3em;border-bottom:2px solid #1a1a1a;padding-bottom:.4em}
        .meta{color:#555;font-size:.85em;margin-bottom:1.5em}
        .grade{border:2px solid #1a1a1a;padding:1em 1.2em;margin-bottom:1.5em}
        .grade .letter{font-size:2.4em;font-weight:bold}
        .grade .verdict{font-size:1em;margin-top:.4em}
        .grade-a{border-color:#1a7a1a;color:#1a7a1a}
        .grade-b{border-color:#9a6a00;color:#9a6a00}
        .grade-c{border-color:#b06a00;color:#b06a00}
        .grade-d{border-color:#b00;color:#b00}
        .verdict{color:#333}
        h2{font-size:.9em;text-transform:uppercase;letter-spacing:.06em;color:#444;border-bottom:1px solid #999;padding-bottom:.3em;margin:1.6em 0 .6em}
        table{width:100%;border-collapse:collapse;font-size:.9em}
        td,th{border:1px solid #bbb;padding:.35em .6em;text-align:left}
        th{background:#eee}
        .ok{color:#1a7a1a}.attention{color:#9a6a00}.problem{color:#b00;font-weight:bold}.unknown{color:#888}
        .photos img{max-width:220px;margin:4px;border:1px solid #ccc}
        .footer{margin-top:2.5em;font-size:.75em;color:#777;border-top:1px solid #ccc;padding-top:.8em}
        @media print{body{margin:0;max-width:none}@page{margin:15mm}}
        </style></head><body>
        """);

        sb.AppendLine("<h1>Relatório do equipamento</h1>");
        sb.AppendLine($"<div class=\"meta\">{e(r.Device.Brand)} {e(model)} · " +
            $"s/n {e(r.Device.Serial)} · inspecionado {r.CollectedAt:dd-MM-yyyy HH:mm}</div>");

        if (grade is not null)
        {
            var cls = $"grade-{(grade.Value ?? "d").ToLowerInvariant()}";
            sb.AppendLine($"<div class=\"grade {cls}\"><span class=\"letter\">GRAU {e(grade.Value)}</span>" +
                $"<div class=\"verdict\">{e(grade.Detail)}</div></div>");
        }

        if (parts is not null)
            sb.AppendLine($"<p><b>Peças prováveis para reparação:</b> {e(parts)}</p>");

        // Verificações relevantes para o cliente — agrupadas por estado.
        var shown = r.Results
            .Where(kv => !kv.Key.StartsWith("logs.", StringComparison.Ordinal)
                      && !kv.Key.StartsWith("grade.", StringComparison.Ordinal)
                      && !kv.Key.StartsWith("suggest.", StringComparison.Ordinal)
                      && kv.Key is not "identity.fingerprint" and not "security.selinux"
                             and not "security.build" and not "security.crypto"
                             and not "identity.iccid" and not "identity.partno"
                             and not "identity.regmodel" and not "identity.region")
            .OrderBy(kv => kv.Key)
            .ToList();
        if (shown.Count > 0)
        {
            sb.AppendLine("<h2>O que verificámos</h2>" +
                "<table><tr><th>Verificação</th><th>Resultado</th><th>Observação</th></tr>");
            foreach (var (key, c) in shown)
            {
                var cls = c.Status switch
                {
                    "pass" => "ok",
                    "warn" => "attention",
                    "fail" => "problem",
                    _ => "unknown",
                };
                var obs = string.Join(" — ", new[] { c.Value, c.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
                sb.AppendLine($"<tr><td>{e(FriendlyLabel(key))}</td>" +
                    $"<td class=\"{cls}\">{ClientStatus(c.Status)}</td><td>{e(obs)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (!string.IsNullOrWhiteSpace(r.Notes))
            sb.AppendLine($"<h2>Notas do técnico</h2><p>{e(r.Notes)}</p>");

        if (r.PhotoPaths.Count > 0)
        {
            var imgs = new StringBuilder();
            foreach (var path in r.PhotoPaths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    var mime = ext is ".png" ? "image/png" : ext is ".webp" ? "image/webp" : "image/jpeg";
                    imgs.Append($"<img src=\"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}\">");
                }
                catch { }
            }
            if (imgs.Length > 0)
                sb.AppendLine($"<h2>Fotos</h2><div class=\"photos\">{imgs}</div>");
        }

        sb.AppendLine("<div class=\"footer\">Inspeção realizada por oficinaos-diag na data indicada. " +
            "Este documento descreve o estado observado do equipamento — não substitui garantia " +
            "nem certificação oficial.</div></body></html>");
        return sb.ToString();
    }

    private static string InsuranceStatus(string status) => status switch
    {
        "pass" => "Conforme",
        "warn" => "A verificar",
        "fail" => "Anomalia detetada",
        "skipped" => "Não verificado",
        _ => "Observado",
    };

    /// <summary>
    /// Relatório formal A4 para seguradora — estado do equipamento, dano
    /// reportado, estimativa e carimbo da loja. Imprime → PDF no browser.
    /// </summary>
    public static string ToInsuranceHtml(DeviceReport r, InsuranceForm f)
    {
        var e = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var model = string.IsNullOrWhiteSpace(r.Device.MarketingName)
            ? r.Device.Model
            : $"{r.Device.MarketingName} ({r.Device.Model})";
        var shopLine = string.Join(" · ", new[]
        {
            f.ShopNif is null ? null : $"NIF {f.ShopNif}",
            f.ShopAddress,
            f.ShopPhone,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var serialTag = (r.Device.Serial ?? "dev").ToUpperInvariant();
        var docRef = $"SEG-{r.CollectedAt:yyyyMMdd}-{serialTag[..Math.Min(6, serialTag.Length)]}";

        var sb = new StringBuilder();
        sb.AppendLine("""
        <!DOCTYPE html><html lang="pt"><head><meta charset="utf-8">
        <title>Relatório técnico para seguradora</title><style>
        body{font-family:'Segoe UI',Arial,sans-serif;color:#1a1a1a;max-width:800px;margin:2em auto;padding:0 2em}
        .shop{border-bottom:3px double #1a1a1a;padding-bottom:.8em}
        .shop h1{font-size:1.5em;margin:0}
        .shop .contact{color:#444;font-size:.85em;margin-top:.3em}
        .doctitle{display:flex;justify-content:space-between;align-items:baseline;margin:1.2em 0 .6em}
        .doctitle h2{font-size:1.15em;margin:0;letter-spacing:.02em}
        .doctitle .ref{color:#666;font-size:.8em;font-family:Consolas,monospace}
        .subtitle{color:#555;font-size:.85em;margin-bottom:1.4em}
        h3{font-size:.8em;text-transform:uppercase;letter-spacing:.08em;color:#444;border-bottom:1px solid #999;padding-bottom:.3em;margin:1.8em 0 .7em}
        .kv{width:100%;border-collapse:collapse}
        .kv td{padding:.25em .4em;font-size:.92em;vertical-align:top}
        .kv td:first-child{color:#555;width:38%}
        .damage{background:#fafafa;border:1px solid #ccc;padding:.8em;font-size:.95em;white-space:pre-wrap}
        table.checks{width:100%;border-collapse:collapse;font-size:.82em}
        table.checks th{background:#eee;text-align:left}
        table.checks td,table.checks th{border:1px solid #bbb;padding:.35em .6em}
        .st-pass{color:#1a7a1a}.st-warn{color:#9a6a00}.st-fail{color:#b00;font-weight:bold}.st-skip{color:#888}
        .sign{margin-top:4em;display:flex;justify-content:space-between;font-size:.85em}
        .sign .line{border-top:1px solid #1a1a1a;width:45%;padding-top:.4em}
        .legal{margin-top:2em;font-size:.72em;color:#666;border-top:1px solid #ccc;padding-top:.8em}
        @media print{body{margin:0;max-width:none}@page{margin:15mm}}
        </style></head><body>
        """);

        sb.AppendLine($"<div class=\"shop\"><h1>{e(f.ShopName)}</h1>" +
            (string.IsNullOrWhiteSpace(shopLine) ? "" : $"<div class=\"contact\">{e(shopLine)}</div>") +
            "</div>");
        sb.AppendLine($"<div class=\"doctitle\"><h2>RELATÓRIO TÉCNICO DE AVARIA</h2>" +
            $"<span class=\"ref\">{docRef}</span></div>");
        sb.AppendLine("<div class=\"subtitle\">Documento emitido a pedido do segurado para efeitos de participação de sinistro.</div>");

        sb.AppendLine("<h3>Segurado</h3><table class=\"kv\">" +
            $"<tr><td>Nome</td><td>{e(f.InsuredName)}</td></tr>" +
            (f.Insurer is null ? "" : $"<tr><td>Seguradora</td><td>{e(f.Insurer)}</td></tr>") +
            (f.PolicyNo is null ? "" : $"<tr><td>Apólice / Sinistro</td><td>{e(f.PolicyNo)}</td></tr>") +
            "</table>");

        sb.AppendLine("<h3>Equipamento inspecionado</h3><table class=\"kv\">" +
            $"<tr><td>Marca / Modelo</td><td>{e(r.Device.Brand)} {e(model)}</td></tr>" +
            $"<tr><td>Sistema operativo</td><td>{e(r.Device.Os)} {e(r.Device.OsVersion)}</td></tr>" +
            $"<tr><td>Nº de série / IMEI</td><td>{e(r.Device.Serial)}</td></tr>" +
            $"<tr><td>Data da inspeção</td><td>{r.CollectedAt:dd-MM-yyyy HH:mm}</td></tr>" +
            "</table>");

        sb.AppendLine($"<h3>Dano reportado</h3><div class=\"damage\">{e(f.DamageDescription)}</div>");

        if (r.Results.Count > 0)
        {
            sb.AppendLine("<h3>Verificações técnicas</h3>" +
                "<table class=\"checks\"><tr><th>Verificação</th><th>Estado</th><th>Observação</th></tr>");
            foreach (var (key, c) in r.Results.OrderBy(kv => kv.Key))
            {
                var obs = string.Join(" — ", new[] { c.Value, c.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var cls = c.Status switch
                {
                    "pass" => "st-pass",
                    "warn" => "st-warn",
                    "fail" => "st-fail",
                    _ => "st-skip",
                };
                sb.AppendLine($"<tr><td>{e(FriendlyLabel(key))}</td>" +
                    $"<td class=\"{cls}\">{InsuranceStatus(c.Status)}</td>" +
                    $"<td>{e(obs)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (f.RepairEstimate is not null || f.EstimatedCost is not null)
        {
            sb.AppendLine("<h3>Estimativa de reparação</h3><table class=\"kv\">" +
                (f.RepairEstimate is null ? "" : $"<tr><td>Reparação proposta</td><td>{e(f.RepairEstimate)}</td></tr>") +
                (f.EstimatedCost is null ? "" : $"<tr><td>Custo estimado</td><td>{e(f.EstimatedCost)} €</td></tr>") +
                "</table>");
        }

        sb.AppendLine("<div class=\"sign\">" +
            $"<div class=\"line\">Local e data<br>{e(f.ShopAddress ?? "")}<br>{r.CollectedAt:dd-MM-yyyy}</div>" +
            $"<div class=\"line\">O técnico<br>{e(f.Technician ?? "")}</div>" +
            "</div>");

        sb.AppendLine("<div class=\"legal\">Relatório gerado a partir de verificações técnicas automatizadas " +
            "(oficinaos-diag) complementadas por inspeção do técnico responsável. Descreve o estado " +
            "do equipamento observado à data da inspeção.</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
