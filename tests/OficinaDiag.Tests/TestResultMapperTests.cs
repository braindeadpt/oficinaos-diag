using System.Text.Json;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Tests;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Contrato do resultado que a página on-device devolve. O caso que motivou
/// estes testes: no iOS, sem permissão de movimento, o payload deixa de trazer
/// as chaves <c>accel</c>/<c>gyro</c>/<c>orient</c> — antes vinham como
/// <c>no-data</c> e o relatório dizia «sem leitura» num sensor que nunca foi
/// autorizado. Agora a chave ausente simplesmente não cria linha.
/// </summary>
public sealed class TestResultMapperTests
{
    private static Dictionary<string, JsonElement> Json(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>
    /// Payload partilhado com o teste jsdom (mesmo ficheiro, mesmos valores):
    /// o jsdom prova que a página o produz e este teste prova o que o
    /// mapeador faz com ele. Um literal copiado à mão divergia em silêncio.
    /// </summary>
    private static string Fixture(string nome) =>
        Repo.Read("tests", "device-test", "fixtures", nome);

    private static DeviceReport Map(string json)
    {
        var r = new DeviceReport { Platform = "ios" };
        TestResultMapper.Apply(r, Json(json));
        return r;
    }

    [Fact]
    public void SensorAusenteNaoCriaLinhaNenhuma()
    {
        // iOS sem permissão: só a luz ambiente foi medida.
        var r = Map("""{"sensors":{"light":"ok"}}""");

        Assert.True(r.Results.ContainsKey("sensor.light"));
        Assert.False(r.Results.ContainsKey("sensor.accel"));
        Assert.False(r.Results.ContainsKey("sensor.gyro"));
        Assert.False(r.Results.ContainsKey("sensor.orient"));
        Assert.False(r.Results.ContainsKey("sensor.mic"));
    }

    [Fact]
    public void PayloadDeIphoneSemPermissaoSoTemAsLinhasMedidas()
    {
        // Fixture partilhado com o jsdom (`iPhone sem permissão de movimento
        // entrega payload sem accel/gyro/orient`): sem gesto de permissão, nem
        // accel nem gyro nem orient vêm, e o relatório não inventa linhas.
        var r = Map(Fixture("ios-sem-permissao.json"));

        Assert.False(r.Results.ContainsKey("sensor.accel"));
        Assert.False(r.Results.ContainsKey("sensor.gyro"));
        Assert.False(r.Results.ContainsKey("sensor.orient"));
        Assert.Equal("skipped", r.Results["sensor.light"].Status);
        Assert.Equal("skipped", r.Results["sensor.mic"].Status);
        // O payload já não traz `sensors.multitouch` (era ignorado): a linha
        // `sensor.multitouch` vem do `multiTouch` no topo, que aqui é 0.
        Assert.False(r.Results.ContainsKey("sensor.multitouch"));
        Assert.Equal("pass", r.Results["test.screen"].Status);
    }

    [Fact]
    public void SensorComNoDataViraWarnComSemLeitura()
    {
        var r = Map("""{"sensors":{"accel":"no-data"}}""");
        var c = r.Results["sensor.accel"];

        Assert.Equal("warn", c.Status);
        Assert.Equal(L10n.T("sensor.nodata"), c.Value);
        Assert.Contains(L10n.T("sensor.accel"), c.Detail!);
    }

    [Fact]
    public void SensorNaoExpostoViraSkipped()
    {
        var r = Map("""{"sensors":{"light":"unavailable"}}""");
        var c = r.Results["sensor.light"];

        Assert.Equal("skipped", c.Status);
        Assert.Equal(L10n.T("sensor.noapi"), c.Value);
    }

    [Fact]
    public void SensorOkViraPass()
    {
        var r = Map("""{"sensors":{"gyro":"ok"}}""");
        var c = r.Results["sensor.gyro"];

        Assert.Equal("pass", c.Status);
        Assert.Equal(L10n.T("sensor.responding"), c.Value);
    }

    [Fact]
    public void AusenteENoDataDaoOMesmoGrauMasRelatoriosDiferentes()
    {
        // A distinção é do relatório, não da nota: nenhum dos dois castiga o
        // aparelho, mas só o no-data deixa rasto visível ao técnico.
        var semPermissao = Map("""{"touchHit":60,"touchTotal":60,"sensors":{"light":"ok"}}""");
        var semLeitura = Map("""{"touchHit":60,"touchTotal":60,"sensors":{"light":"ok","accel":"no-data"}}""");

        Assert.Equal(Grader.Compute(semPermissao).Grade, Grader.Compute(semLeitura).Grade);
        Assert.False(semPermissao.Results.ContainsKey("sensor.accel"));
        Assert.Equal("warn", semLeitura.Results["sensor.accel"].Status);
    }

    [Fact]
    public void ToqueBordasMultiToqueESpeaker()
    {
        var r = Map("""
            {"touchHit":58,"touchTotal":60,"edgeHit":27,"edgeTotal":28,
             "multiTouch":3,"speaker":"ok","vibrate":"ok"}
            """);

        Assert.Equal("pass", r.Results["test.touch"].Status);   // 58 >= 60-5
        Assert.Equal("pass", r.Results["test.screen"].Status);
        Assert.Equal("warn", r.Results["test.edges"].Status);   // 27 < 28
        Assert.Equal("warn", r.Results["sensor.multitouch"].Status);
        Assert.Equal("pass", r.Results["sensor.speaker"].Status);
        Assert.Equal("pass", r.Results["sensor.vibrate"].Status);
    }

    [Fact]
    public void ToqueAbaixoDoTetoViraFailEMultiToqueCincoViraPass()
    {
        var r = Map("""
            {"touchHit":40,"touchTotal":60,"edgeHit":28,"edgeTotal":28,"multiTouch":5}
            """);

        Assert.Equal("fail", r.Results["test.touch"].Status);
        Assert.Equal("pass", r.Results["test.edges"].Status);
        Assert.Equal("pass", r.Results["sensor.multitouch"].Status);
    }

    [Fact]
    public void MicrofoneBloqueadoViraSkippedENaoFail()
    {
        // HTTP na LAN não é contexto seguro: o browser não dá microfone. Isso
        // não é defeito de hardware e o Grader não pode descer a nota.
        var r = Map("""{"speaker":"fail","sensors":{"mic":"skip","earpiece":"fail"}}""");

        Assert.Equal("skipped", r.Results["sensor.mic"].Status);
        Assert.Equal("fail", r.Results["sensor.earpiece"].Status);
        Assert.Equal("fail", r.Results["sensor.speaker"].Status);
    }

    [Fact]
    public void VibracaoAusenteEEstadoDesconhecidoNaoCriamLinha()
    {
        var r = Map("""{"vibrate":"unavailable"}""");
        Assert.False(r.Results.ContainsKey("sensor.vibrate"));

        var desconhecido = Map("""{"vibrate":"no-data"}""");
        Assert.Equal("fail", desconhecido.Results["sensor.vibrate"].Status);
    }
}
