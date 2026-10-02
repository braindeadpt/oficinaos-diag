using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public static string ToJson(DeviceReport r) => JsonSerializer.Serialize(r, JsonOpts);

    /// <summary>Payload for cloud /reports/diagnostic (AI report, shop-side).</summary>
    public static string ToCloudJson(DeviceReport r, string lang = "pt")
    {
        var payload = new
        {
            lang,
            device = new { brand = r.Device.Brand, model = r.Device.Model, os = r.Device.Os, osVersion = r.Device.OsVersion },
            results = r.Results,
            logs = r.LogsText,
            notes = r.Notes,
        };
        return JsonSerializer.Serialize(payload, JsonOpts);
    }

    /// <summary>Payload for cloud POST /intake/:shopCode (customer → shop).</summary>
    public static string ToIntakeJson(DeviceReport r, string name, string phone, string? email)
    {
        var payload = new
        {
            customerName = name,
            customerPhone = phone,
            customerEmail = email,
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
        sb.AppendLine("<div class=\"footer\">oficinaos-diag · open source (MIT) · relatório gerado localmente</div></body></html>");
        return sb.ToString();
    }
}
