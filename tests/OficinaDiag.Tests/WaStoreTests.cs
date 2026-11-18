using Microsoft.Data.Sqlite;
using OficinaDiag.WhatsApp;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Inspetor + export WhatsApp sobre uma fixture sintética com o esquema
/// real do ChatStorage iOS (colunas confirmadas numa BD Business real,
/// modelo 2.12.14) — a BD do dispositivo de teste é dado pessoal e nunca
/// entra no repo nem nos testes.
/// </summary>
public sealed class WaStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"wa-test-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly SqliteConnection _db;

    public WaStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "ChatStorage.sqlite");
        BuildChatStorage(_dbPath);
        _db = WaStore.OpenReadOnly(_dbPath);
        _db.Open();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Inspect_DetectsIosChatStorage()
    {
        var info = WaStore.Inspect(_db);

        Assert.Equal(WaStoreVariant.IosChatStorage, info.Variant);
        Assert.True(info.IntegrityOk);
        Assert.Equal("2.12.14", info.ModelVersion);
        Assert.Equal(3, info.MessageCount);
        Assert.Equal(1, info.ChatCount);
        Assert.Equal(1, info.MediaItemCount);
        Assert.Equal(2, info.MessageTypes[0]);
        Assert.Equal(1, info.MessageTypes[1]);
    }

    [Fact]
    public void Inspect_ConvertsCoreDataEpoch()
    {
        var info = WaStore.Inspect(_db);

        // Fixture: mensagens a 2024-01-01 e 2024-01-02 (Core Data epoch).
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero),
            info.FirstMessageUtc);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 10, 0, 0, TimeSpan.Zero),
            info.LastMessageUtc);
    }

    [Fact]
    public void Reader_ReturnsChatsAndMessages()
    {
        var chats = WaIosReader.Chats(_db);

        var chat = Assert.Single(chats);
        Assert.Equal("351912345678@s.whatsapp.net", chat.Jid);
        Assert.Equal("Cliente Teste", chat.PartnerName);
        Assert.False(chat.IsGroup);

        var msgs = WaIosReader.Messages(_db, chat.Pk).ToList();
        Assert.Equal(3, msgs.Count);
        Assert.Equal("olá <mundo>", msgs[0].Text);
        Assert.False(msgs[0].FromMe);
        Assert.True(msgs[1].FromMe);
        Assert.Equal(1, msgs[2].Type);
        Assert.Equal("Media/123/abc.jpg", msgs[2].MediaLocalPath);
    }

    [Fact]
    public void Export_WritesEscapedHtmlPerChat()
    {
        var outDir = Path.Combine(_dir, "out");
        var count = WaHtmlExport.Export(_db, outDir);

        Assert.Equal(1, count);
        var html = File.ReadAllText(Path.Combine(outDir, "chat-1.html"));
        Assert.Contains("olá &lt;mundo&gt;", html); // HTML escaped
        Assert.Contains("Cliente Teste", html);
        Assert.Contains("📷 imagem", html);
        Assert.True(File.Exists(Path.Combine(outDir, "index.html")));
    }

    [Fact]
    public void Inspect_UnknownDatabase_ReturnsUnknown()
    {
        var other = Path.Combine(_dir, "other.sqlite");
        using (var c = new SqliteConnection($"Data Source={other}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x)";
            cmd.ExecuteNonQuery();
        }

        using var ro = WaStore.OpenReadOnly(other);
        ro.Open();
        Assert.Equal(WaStoreVariant.Unknown, WaStore.Inspect(ro).Variant);
    }

    // Esquema mínimo mas fiel: mesmas tabelas e nomes de coluna que a BD
    // real. Duas mensagens de texto (dia 1 e 2) + uma imagem.
    private static void BuildChatStorage(string path)
    {
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        Exec(db, """
            CREATE TABLE Z_METADATA (Z_VERSION INTEGER, Z_UUID VARCHAR, Z_PLIST BLOB);
            CREATE TABLE Z_PRIMARYKEY (Z_ENT INTEGER, Z_NAME VARCHAR, Z_SUPER INTEGER, Z_MAX INTEGER);
            CREATE TABLE ZWACHATSESSION (Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER,
                ZMESSAGECOUNTER INTEGER, ZREMOVED INTEGER, ZSESSIONTYPE INTEGER, ZUNREADCOUNT INTEGER,
                ZGROUPINFO INTEGER, ZLASTMESSAGE INTEGER, ZLASTMESSAGEDATE TIMESTAMP,
                ZCONTACTJID VARCHAR, ZLASTMESSAGETEXT VARCHAR, ZPARTNERNAME VARCHAR);
            CREATE TABLE ZWAGROUPINFO (Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZCHATSESSION INTEGER);
            CREATE TABLE ZWAGROUPMEMBER (Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER,
                ZISADMIN INTEGER, ZISACTIVE INTEGER, ZCHATSESSION INTEGER, ZMEMBERJID VARCHAR, ZCONTACTNAME VARCHAR);
            CREATE TABLE ZWAMESSAGE (Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER,
                ZISFROMME INTEGER, ZMESSAGESTATUS INTEGER, ZMESSAGETYPE INTEGER, ZSORT INTEGER,
                ZCHATSESSION INTEGER, ZGROUPMEMBER INTEGER, ZMEDIAITEM INTEGER, ZMESSAGEINFO INTEGER,
                ZPARENTMESSAGE INTEGER, ZMESSAGEDATE TIMESTAMP, ZSENTDATE TIMESTAMP,
                ZFROMJID VARCHAR, ZPUSHNAME VARCHAR, ZSTANZAID VARCHAR, ZTEXT VARCHAR, ZTOJID VARCHAR);
            CREATE TABLE ZWAMEDIAITEM (Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER,
                ZFILESIZE INTEGER, ZMOVIEDURATION INTEGER, ZMESSAGE INTEGER,
                ZMEDIALOCALPATH VARCHAR, ZMEDIAURL VARCHAR, ZTITLE VARCHAR, ZMEDIAKEY BLOB);
            """);
        Exec(db, "INSERT INTO Z_METADATA VALUES (1,'u',$plist)",
            ("$plist", System.Text.Encoding.UTF8.GetBytes(
                "bplist00NSStoreModelVersionIdentifiersX2.12.14")));

        Exec(db, "INSERT INTO ZWACHATSESSION (Z_PK,Z_ENT,Z_OPT,ZMESSAGECOUNTER,ZREMOVED,ZSESSIONTYPE,ZUNREADCOUNT,ZGROUPINFO,ZCONTACTJID,ZPARTNERNAME) VALUES (1,4,2,3,0,0,0,NULL,'351912345678@s.whatsapp.net','Cliente Teste')");

        const double d1 = 725_796_000; // 2024-01-01 10:00 UTC (Core Data epoch)
        const double d2 = 725_882_400; // 2024-01-02 10:00 UTC
        Exec(db, $"""
            INSERT INTO ZWAMEDIAITEM (Z_PK,Z_ENT,Z_OPT,ZFILESIZE,ZMEDIALOCALPATH,ZTITLE) VALUES (1,9,1,1024,'Media/123/abc.jpg','foto.jpg');
            INSERT INTO ZWAMESSAGE (Z_PK,Z_ENT,Z_OPT,ZISFROMME,ZMESSAGESTATUS,ZMESSAGETYPE,ZSORT,ZCHATSESSION,ZMEDIAITEM,ZMESSAGEDATE,ZFROMJID,ZTEXT,ZTOJID) VALUES
             (1,7,1,0,0,0,1,1,NULL,{d1},'351912345678@s.whatsapp.net','olá <mundo>','me@s.whatsapp.net'),
             (2,7,1,1,0,0,2,1,NULL,{d1}+60,'me@s.whatsapp.net','resposta','351912345678@s.whatsapp.net'),
             (3,7,1,0,0,1,3,1,1,{d2},'351912345678@s.whatsapp.net','ve esta foto','me@s.whatsapp.net');
            """);
    }

    private static void Exec(SqliteConnection db, string sql,
        params (string, object?)[] ps)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
