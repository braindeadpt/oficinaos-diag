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
    public void EcranSemCoresConfirmadasViraSkippedNaoPass()
    {
        // colorsViewed=0: o técnico saltou a fase de cores — «pass» aqui era
        // um falso positivo num relatório que vai à seguradora.
        var r = Map("""{"touchHit":60,"touchTotal":60,"colorsViewed":0}""");
        Assert.Equal("skipped", r.Results["test.screen"].Status);

        var confirmado = Map("""{"colorsViewed":13}""");
        Assert.Equal("pass", confirmado.Results["test.screen"].Status);
    }

    [Fact]
    public void VibracaoAusenteEEstadoDesconhecidoNaoCriamLinha()
    {
        var r = Map("""{"vibrate":"unavailable"}""");
        Assert.False(r.Results.ContainsKey("sensor.vibrate"));

        var desconhecido = Map("""{"vibrate":"no-data"}""");
        Assert.Equal("fail", desconhecido.Results["sensor.vibrate"].Status);
    }

    [Fact]
    public void TiposErradosNoJsonNaoRebentamOMapeamento()
    {
        // O POST vem da LAN — um peer malicioso/bugado que mande strings onde
        // a página envia números não pode matar a sessão com uma exceção.
        var r = Map("""
            {"touchHit":"dez","touchTotal":{},"edgeHit":[1],"multiTouch":"sim","sensors":{"accel":5}}
            """);

        Assert.False(r.Results.ContainsKey("test.touch"));
        Assert.False(r.Results.ContainsKey("test.edges"));
        Assert.False(r.Results.ContainsKey("sensor.multitouch"));
        Assert.False(r.Results.ContainsKey("sensor.accel"));
    }

    // ── testes avançados (Fase B) ─────────────────────────────────────

    [Fact]
    public void CameraFrontalETrazeira_okFailESkip()
    {
        var r = Map("""{"cameraFront":"ok","cameraBack":"fail"}""");

        Assert.Equal("pass", r.Results["test.camera_front"].Status);
        Assert.Equal("fail", r.Results["test.camera_back"].Status);
        Assert.Equal(L10n.T("test.camera.ok"), r.Results["test.camera_front"].Value);
        Assert.Equal(L10n.T("test.camera.fail"), r.Results["test.camera_back"].Value);
    }

    [Fact]
    public void CameraSkipNuncaEViraFail()
    {
        // Permissão recusada, câmara em falta ou HTTP sem contexto seguro —
        // limitação de plataforma/browsers, nunca defeito de hardware.
        var r = Map("""{"cameraFront":"skip","cameraBack":"skip"}""");

        Assert.Equal("skipped", r.Results["test.camera_front"].Status);
        Assert.Equal("skipped", r.Results["test.camera_back"].Status);
        Assert.Equal(L10n.T("test.camera.skip"), r.Results["test.camera_front"].Value);
    }

    [Fact]
    public void GpuOkWarnFailESkip()
    {
        var ok = Map("""{"gpu":"ok","gpuFps":58}""");
        Assert.Equal("pass", ok.Results["test.gpu"].Status);
        Assert.Contains("58", ok.Results["test.gpu"].Value!);

        // fps a rasar do mínimo — warn, não fail (RAF estrangulado).
        var warn = Map("""{"gpu":"warn","gpuFps":3}""");
        Assert.Equal("warn", warn.Results["test.gpu"].Status);
        Assert.Contains("3", warn.Results["test.gpu"].Value!);

        var fail = Map("""{"gpu":"fail"}""");
        Assert.Equal("fail", fail.Results["test.gpu"].Status);

        var skip = Map("""{"gpu":"skip"}""");
        Assert.Equal("skipped", skip.Results["test.gpu"].Status);
        Assert.Equal(L10n.T("test.gpu.skip"), skip.Results["test.gpu"].Value);
    }

    [Fact]
    public void GpuOkSemFpsCaiNoTextoSimples()
    {
        // Payload malformado (sem gpuFps) não pode mostrar «WebGL não
        // exposto» num check que passou — cai no valor simples.
        var r = Map("""{"gpu":"ok"}""");
        Assert.Equal("pass", r.Results["test.gpu"].Status);
        Assert.Equal(L10n.T("test.gpu.okplain"), r.Results["test.gpu"].Value);
    }

    [Fact]
    public void GpsOkLeAccuracyESkipEmErroPadrao()
    {
        var ok = Map("""{"gps":"ok","gpsAccuracyM":14}""");
        Assert.Equal("pass", ok.Results["test.gps"].Status);
        Assert.Contains("14", ok.Results["test.gps"].Value!);

        // «interior sem sinal» e permissão recusada chegam como skip.
        var skip = Map("""{"gps":"skip"}""");
        Assert.Equal("skipped", skip.Results["test.gps"].Status);

        // Só um erro fora do padrão dos 3 códigos vira fail.
        var fail = Map("""{"gps":"fail"}""");
        Assert.Equal("fail", fail.Results["test.gps"].Status);
    }

    [Fact]
    public void BrightnessOkLePercentagemESkipSemAPI()
    {
        var ok = Map("""{"brightness":"ok","brightnessValue":80}""");
        Assert.Equal("pass", ok.Results["test.brightness"].Status);
        Assert.Contains("80", ok.Results["test.brightness"].Value!);

        var skip = Map("""{"brightness":"skip","brightnessValue":null}""");
        Assert.Equal("skipped", skip.Results["test.brightness"].Status);
    }

    [Fact]
    public void TouchrateOkWarnESkip()
    {
        var ok = Map("""{"touchSample":"ok","touchHz":120}""");
        Assert.Equal("pass", ok.Results["test.touchrate"].Status);
        Assert.Contains("120", ok.Results["test.touchrate"].Value!);

        var warn = Map("""{"touchSample":"warn","touchHz":45}""");
        Assert.Equal("warn", warn.Results["test.touchrate"].Status);
        Assert.Equal(L10n.T("test.touchrate.warn.detail"), warn.Results["test.touchrate"].Detail);

        var skip = Map("""{"touchSample":"skip","touchHz":null}""");
        Assert.Equal("skipped", skip.Results["test.touchrate"].Status);
    }

    [Fact]
    public void TestesAvancadosAusentesNaoCriamLinha()
    {
        // Payload de página antiga (ou teste que não chegou à fase
        // avançada): chave ausente = não medido, sem linha no relatório.
        var r = Map("""{"sensors":{"light":"ok"}}""");

        Assert.False(r.Results.ContainsKey("test.camera_front"));
        Assert.False(r.Results.ContainsKey("test.camera_back"));
        Assert.False(r.Results.ContainsKey("test.gpu"));
        Assert.False(r.Results.ContainsKey("test.gps"));
        Assert.False(r.Results.ContainsKey("test.brightness"));
        Assert.False(r.Results.ContainsKey("test.touchrate"));
    }

    [Fact]
    public void TestesAvancadosComTiposErradosNaoRebentam()
    {
        var r = Map("""
            {"cameraFront":5,"gpu":"ok","gpuFps":"cem","gps":true,
             "brightness":"ok","brightnessValue":{},"touchSample":"ok","touchHz":"oitenta"}
            """);

        // Status fora do padrão ou tipo errado → skipped, nunca exceção.
        Assert.Equal("skipped", r.Results["test.camera_front"].Status);
        Assert.False(r.Results.ContainsKey("test.camera_back")); // chave ausente
        Assert.Equal("pass", r.Results["test.gpu"].Status);       // ok sem fps válido → texto simples
        Assert.Equal(L10n.T("test.gpu.okplain"), r.Results["test.gpu"].Value);
        Assert.Equal("skipped", r.Results["test.gps"].Status);    // gps não é string
        Assert.Equal("pass", r.Results["test.brightness"].Status);
        Assert.Equal(L10n.T("test.brightness.plain"), r.Results["test.brightness"].Value);
        Assert.Equal("pass", r.Results["test.touchrate"].Status);
        Assert.Equal(L10n.T("test.touchrate.plain"), r.Results["test.touchrate"].Value);
    }
}
