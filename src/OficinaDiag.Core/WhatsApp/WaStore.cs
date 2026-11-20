using Microsoft.Data.Sqlite;

namespace OficinaDiag.WhatsApp;

/// <summary>
/// Variante da base de dados WhatsApp encontrada: o Core Data do iOS
/// (ChatStorage.sqlite) e o msgstore do Android têm esquemas e epochs
/// completamente diferentes — toda a conversão deriva desta deteção.
/// </summary>
internal enum WaStoreVariant
{
    Unknown,
    IosChatStorage,
    AndroidMsgstore,
}

/// <summary>Resultado de uma inspeção read-only a uma BD WhatsApp.</summary>
internal sealed record WaStoreInfo
{
    internal required WaStoreVariant Variant { get; init; }

    /// <summary>
    /// Versão do modelo Core Data (iOS, de Z_METADATA —
    /// ex. «2.12.14») ou do esquema Android quando detetável.
    /// </summary>
    internal string? ModelVersion { get; init; }

    /// <summary>PRAGMA quick_check devolveu "ok".</summary>
    internal bool IntegrityOk { get; init; }

    internal long MessageCount { get; init; }
    internal long ChatCount { get; init; }
    internal long MediaItemCount { get; init; }

    /// <summary>ZMESSAGETYPE → nº de mensagens (iOS).</summary>
    internal IReadOnlyDictionary<int, long> MessageTypes { get; init; } =
        new Dictionary<int, long>();

    /// <summary>Mensagem mais antiga e mais recente, em Unix segundos.</summary>
    internal DateTimeOffset? FirstMessageUtc { get; init; }
    internal DateTimeOffset? LastMessageUtc { get; init; }
}

/// <summary>
/// Inspeção read-only a uma base de dados WhatsApp. Abre sempre em
/// <c>Mode=ReadOnly</c>: a ferramenta nunca escreve na BD do cliente —
/// a conversão escreve noutra base, com a original intacta.
/// </summary>
internal static class WaStore
{
    /// <summary>Offset Core Data → Unix epoch (2001-01-01), em segundos.</summary>
    internal const long CoreDataEpochOffset = 978_307_200;

    internal static SqliteConnection OpenReadOnly(string path) =>
        new($"Data Source={path};Mode=ReadOnly");

    internal static WaStoreInfo Inspect(SqliteConnection db)
    {
        var tables = TableNames(db);
        var variant = DetectVariant(tables);

        var info = new WaStoreInfo
        {
            Variant = variant,
            IntegrityOk = QuickCheck(db),
        };

        if (variant == WaStoreVariant.IosChatStorage)
        {
            info = info with
            {
                ModelVersion = ModelVersionIos(db),
                MessageCount = ScalarLong(db, "SELECT COUNT(*) FROM ZWAMESSAGE"),
                ChatCount = ScalarLong(db, "SELECT COUNT(*) FROM ZWACHATSESSION"),
                MediaItemCount = tables.Contains("ZWAMEDIAITEM")
                    ? ScalarLong(db, "SELECT COUNT(*) FROM ZWAMEDIAITEM") : 0,
                MessageTypes = MessageTypesIos(db),
                FirstMessageUtc = EdgeDateIos(db, "MIN"),
                LastMessageUtc = EdgeDateIos(db, "MAX"),
            };
        }
        else if (variant == WaStoreVariant.AndroidMsgstore)
        {
            info = info with
            {
                MessageCount = tables.Contains("message")
                    ? ScalarLong(db, "SELECT COUNT(*) FROM message")
                    : ScalarLong(db, "SELECT COUNT(*) FROM messages"),
                ChatCount = ScalarLong(db, "SELECT COUNT(*) FROM chat"),
            };
        }

        return info;
    }

    /// <summary>
    /// iOS: presença do par ZWACHATSESSION+ZWAMESSAGE (Core Data).
    /// Android: par chat+message(s) + jid. Nenhum dos dois = ficheiro
    /// qualquer (o inspetor não adivinha — devolve Unknown e o caller decide).
    /// </summary>
    private static WaStoreVariant DetectVariant(HashSet<string> tables)
    {
        if (tables.Contains("ZWAMESSAGE") && tables.Contains("ZWACHATSESSION"))
            return WaStoreVariant.IosChatStorage;
        if (tables.Contains("chat") &&
            (tables.Contains("message") || tables.Contains("messages")) &&
            tables.Contains("jid"))
            return WaStoreVariant.AndroidMsgstore;
        return WaStoreVariant.Unknown;
    }

    /// <summary>
    /// quick_check em vez de integrity_check: numa BD de ~1 GB a verificação
    /// completa demora minutos; a rápida apanha corrupção estrutural em
    /// segundos, que é o que interessa para «a BD abre e lê?».
    /// </summary>
    private static bool QuickCheck(SqliteConnection db) =>
        ScalarString(db, "PRAGMA quick_check(1)") == "ok";

    /// <summary>
    /// Z_METADATA guarda o modelo como plist binário; a versão aparece
    /// como string UTF-8 lá dentro (NSStoreModelVersionIdentifiers).
    /// Mais simples e estável que dar parse ao bplist.
    /// </summary>
    private static string? ModelVersionIos(SqliteConnection db)
    {
        try
        {
            var blob = ScalarBytes(db,
                "SELECT Z_PLIST FROM Z_METADATA LIMIT 1");
            if (blob is null) return null;
            var text = System.Text.Encoding.UTF8.GetString(blob);
            // «2.12.14» — primeiro grupo «X.Y(.Z…)» com ≥2 pontos.
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"\d+\.\d+\.\d+[\d.]*");
            return m.Success ? m.Value : null;
        }
        catch (SqliteException) { return null; }
    }

    private static Dictionary<int, long> MessageTypesIos(SqliteConnection db)
    {
        var map = new Dictionary<int, long>();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT ZMESSAGETYPE, COUNT(*) FROM ZWAMESSAGE GROUP BY ZMESSAGETYPE";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetInt32(0)] = r.GetInt64(1);
        return map;
    }

    private static DateTimeOffset? EdgeDateIos(SqliteConnection db, string fn)
    {
        // MIN/MAX sobre zero linhas devolve NULL: ScalarLong convertia-o para
        // 0 e a conversa mais antiga aparecia como 2001-01-01 (epoch Core
        // Data). Null aqui = «sem mensagens com data», não uma data.
        var v = ScalarLongNullable(db,
            $"SELECT {fn}(ZMESSAGEDATE) FROM ZWAMESSAGE WHERE ZMESSAGEDATE IS NOT NULL");
        if (v is not { } core) return null;
        var unix = core + CoreDataEpochOffset;
        if (unix is < -62135596800 or > 253402300799) return null;
        return DateTimeOffset.FromUnixTimeSeconds(unix);
    }

    private static HashSet<string> TableNames(SqliteConnection db)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(r.GetString(0));
        return set;
    }

    internal static long ScalarLong(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static long? ScalarLongNullable(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    private static string? ScalarString(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static byte[]? ScalarBytes(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as byte[];
    }
}
