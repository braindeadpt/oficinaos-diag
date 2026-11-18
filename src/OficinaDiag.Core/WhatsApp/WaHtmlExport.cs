using System.Text;
using Microsoft.Data.Sqlite;

namespace OficinaDiag.WhatsApp;

/// <summary>
/// Exporta conversas de um ChatStorage iOS para HTML legível — um
/// <c>index.html</c> com a lista de conversas e um ficheiro por conversa.
/// Só leitura: a fonte nunca é tocada e não há injeção nem re-encriptação —
/// é o produto seguro que sai do pipeline de leitura (resgate de app morta,
/// prova para o cliente, memória).
/// </summary>
internal static class WaHtmlExport
{
    /// <summary>Nomes de ficheiro previsíveis: chat-1.html, chat-2.html…</summary>
    internal static int Export(SqliteConnection db, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var chats = WaIosReader.Chats(db);

        var index = new StringBuilder();
        index.Append("""
            <!DOCTYPE html><html lang="pt"><head><meta charset="utf-8">
            <title>Conversas WhatsApp</title>
            """).Append(Style).Append("</head><body><h1>Conversas</h1><ul>");

        for (var i = 0; i < chats.Count; i++)
        {
            var chat = chats[i];
            var file = $"chat-{i + 1}.html";
            File.WriteAllText(Path.Combine(outputDir, file), RenderChat(db, chat));

            var name = E(DisplayName(chat));
            index.Append($"""<li><a href="{file}">{name}</a> <small>({chat.MessageCount} mensagens)</small></li>""");
        }

        index.Append("</ul></body></html>");
        File.WriteAllText(Path.Combine(outputDir, "index.html"), index.ToString());
        return chats.Count;
    }

    private static string RenderChat(SqliteConnection db, WaChat chat)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html><html lang="pt"><head><meta charset="utf-8">
            <title>
            """).Append(E(DisplayName(chat))).Append("</title>")
            .Append(Style).Append("</head><body><h1>")
            .Append(E(DisplayName(chat))).Append("</h1><div class=\"chat\">");

        string? lastDay = null;
        foreach (var m in WaIosReader.Messages(db, chat.Pk))
        {
            var local = m.Utc?.ToLocalTime();
            var day = local?.ToString("d 'de' MMMM 'de' yyyy") ?? "data desconhecida";
            if (day != lastDay)
            {
                sb.Append($"""<div class="day">{E(day)}</div>""");
                lastDay = day;
            }

            var cls = m.FromMe ? "me" : "them";
            var who = m.FromMe ? "" :
                $"""<div class="who">{E(m.PushName ?? SenderLabel(m))}</div>""";
            sb.Append($"""<div class="msg {cls}">{who}<div class="body">{Body(m)}</div>""")
              .Append($"""<div class="meta">{local?.ToString("HH:mm") ?? "?"}</div></div>""");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// Texto com escapes e quebras, ou placeholder honesto por tipo — o
    /// export diz «imagem não incluída» em vez de fingir uma imagem que
    /// não está na BD (media vem como ficheiros separados, fora do sqlite).
    /// </summary>
    private static string Body(WaMessage m)
    {
        var text = m.Text is null ? "" :
            E(m.Text).Replace("\n", "<br>");
        var media = m.MediaLocalPath is null ? "" :
            $"""<div class="attach">{E(m.MediaLocalPath.Split('/').Last())}</div>""";

        var label = m.Type switch
        {
            1 => "📷 imagem",
            2 => "🎤 áudio",
            3 => "📄 documento",
            7 => "🎬 vídeo",
            8 => "📍 localização",
            10 => "📞 chamada",
            14 => "👤 contacto",
            15 => "mensagem apagada",
            6 => null,           // evento de grupo: o ZTEXT descreve-o
            0 => null,
            var t => $"tipo {t}",
        };

        var tag = label is null ? "" :
            $"""<div class="kind">[{label}]</div>""";
        return tag + media + text;
    }

    private static string SenderLabel(WaMessage m) =>
        m.SenderJid?.Split('@')[0] ?? "?";

    private static string DisplayName(WaChat chat) =>
        chat.PartnerName ?? chat.Jid.Split('@')[0];

    /// <summary>
    /// Escapa só os 5 caracteres HTML — o ficheiro é UTF-8 e o texto PT
    /// fica legível (WebUtility.HtmlEncode transformaria «á» em «&#xE1;»).
    /// </summary>
    private static string E(string? s) =>
        (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;")
            .Replace(">", "&gt;").Replace("\"", "&quot;")
            .Replace("'", "&#39;");

    private const string Style = """
        <style>
        body{font-family:system-ui,sans-serif;max-width:52em;margin:2em auto;padding:0 1em;background:#ece5dd}
        h1{font-size:1.3em}.chat{display:flex;flex-direction:column;gap:.4em}
        .msg{max-width:75%;padding:.5em .7em;border-radius:.6em;background:#fff}
        .msg.me{align-self:flex-end;background:#dcf8c6}
        .msg.them{align-self:flex-start}
        .who{font-size:.75em;color:#128c7e;font-weight:600}
        .meta{font-size:.68em;color:#999;text-align:right}
        .day{align-self:center;font-size:.75em;color:#555;background:#dde3e7;
             border-radius:1em;padding:.2em .8em;margin:.6em 0}
        .kind{font-size:.8em;color:#888;font-style:italic}
        .attach{font-size:.8em;color:#075e54;border:1px dashed #bbb;
                padding:.2em .4em;border-radius:.3em;margin:.2em 0}
        ul{line-height:1.9}
        </style>
        """;
}
