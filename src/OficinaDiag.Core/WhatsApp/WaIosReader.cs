using Microsoft.Data.Sqlite;

namespace OficinaDiag.WhatsApp;

/// <summary>Uma conversa de um ChatStorage iOS.</summary>
internal sealed record WaChat
{
    internal required long Pk { get; init; }
    internal required string Jid { get; init; }
    internal string? PartnerName { get; init; }
    internal bool IsGroup { get; init; }
    internal long MessageCount { get; init; }
    internal DateTimeOffset? LastMessageUtc { get; init; }
}

/// <summary>Uma mensagem normalizada para leitura/exportação.</summary>
internal sealed record WaMessage
{
    internal required long Pk { get; init; }
    /// <summary>Null quando a data gravada é lixo (BD real tem casos).</summary>
    internal required DateTimeOffset? Utc { get; init; }
    internal required int Type { get; init; }
    internal required bool FromMe { get; init; }
    internal string? Text { get; init; }

    /// <summary>JID do emissor dentro de um grupo (null em 1:1).</summary>
    internal string? SenderJid { get; init; }
    internal string? PushName { get; init; }

    /// <summary>Path local do ficheiro media, quando a mensagem o tem.</summary>
    internal string? MediaLocalPath { get; init; }
    internal long? MediaFileSize { get; init; }
    internal string? MediaTitle { get; init; }
}

/// <summary>
/// Leitura read-only de um <c>ChatStorage.sqlite</c> iOS (WhatsApp ou
/// Business — o esquema é o mesmo, muda o domínio de onde veio).
/// Itens saem por streaming: uma BD real pode ter &gt;1M de mensagens e o
/// export não pode exigir que caibam em memória.
/// </summary>
internal static class WaIosReader
{
    /// <summary>Conversas ordenadas da mais recente para a mais antiga.</summary>
    internal static List<WaChat> Chats(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT s.Z_PK, s.ZCONTACTJID, s.ZPARTNERNAME,
                   CASE WHEN g.Z_PK IS NOT NULL THEN 1 ELSE 0 END,
                   s.ZMESSAGECOUNTER, s.ZLASTMESSAGEDATE
            FROM ZWACHATSESSION s
            LEFT JOIN ZWAGROUPINFO g ON g.ZCHATSESSION = s.Z_PK
            WHERE s.ZCONTACTJID IS NOT NULL AND s.ZREMOVED = 0
            ORDER BY s.ZLASTMESSAGEDATE DESC
            """;
        var chats = new List<WaChat>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            chats.Add(new WaChat
            {
                Pk = r.GetInt64(0),
                Jid = r.GetString(1),
                PartnerName = r.IsDBNull(2) ? null : r.GetString(2),
                IsGroup = r.GetInt64(3) == 1,
                MessageCount = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                LastMessageUtc = r.IsDBNull(5) ? null : FromCoreData(r.GetDouble(5)),
            });
        }
        return chats;
    }

    /// <summary>
    /// Mensagens de uma conversa por ordem cronológica (ZSORT é a ordem que
    /// a app mostra — não a do Z_PK). Emissor: em grupos, o JID vem do
    /// ZWAGROUPMEMBER ligado; ZPUSHNAME é o nome que o contacto anunciava.
    /// </summary>
    internal static IEnumerable<WaMessage> Messages(SqliteConnection db, long chatSessionPk)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT m.Z_PK, m.ZMESSAGEDATE, m.ZMESSAGETYPE, m.ZISFROMME,
                   m.ZTEXT, gm.ZMEMBERJID, m.ZPUSHNAME,
                   mi.ZMEDIALOCALPATH, mi.ZFILESIZE, mi.ZTITLE
            FROM ZWAMESSAGE m
            LEFT JOIN ZWAGROUPMEMBER gm ON gm.Z_PK = m.ZGROUPMEMBER
            LEFT JOIN ZWAMEDIAITEM mi ON mi.Z_PK = m.ZMEDIAITEM
            WHERE m.ZCHATSESSION = $chat
            ORDER BY m.ZSORT, m.Z_PK
            """;
        cmd.Parameters.AddWithValue("$chat", chatSessionPk);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new WaMessage
            {
                Pk = r.GetInt64(0),
                Utc = FromCoreData(r.GetDouble(1)),
                Type = r.GetInt32(2),
                FromMe = r.GetInt32(3) == 1,
                Text = r.IsDBNull(4) ? null : r.GetString(4),
                SenderJid = r.IsDBNull(5) ? null : r.GetString(5),
                PushName = r.IsDBNull(6) ? null : r.GetString(6),
                MediaLocalPath = r.IsDBNull(7) ? null : r.GetString(7),
                MediaFileSize = r.IsDBNull(8) ? null : r.GetInt64(8),
                MediaTitle = r.IsDBNull(9) ? null : r.GetString(9),
            };
        }
    }

    /// <summary>
    /// Epoch Core Data → UTC. Datas corruptas numa BD real (valores fora
    /// do range do epoch) viram null — o export mostra «?» em vez de
    /// rebentar a meio de 1.4M mensagens.
    /// </summary>
    internal static DateTimeOffset? FromCoreData(double t)
    {
        var unix = (long)Math.Round(t) + WaStore.CoreDataEpochOffset;
        if (unix is < -62135596800 or > 253402300799)
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(unix);
    }
}
