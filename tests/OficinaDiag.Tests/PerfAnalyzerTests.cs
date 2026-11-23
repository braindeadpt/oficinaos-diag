using System.Text.Json;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Health;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Sessão PERFIL (Fase C) — checks perf.* a partir das amostras do host e
/// das estatísticas da página de stress. As regras honestas são as do
/// PowerAnalyzer: métrica não exposta → skipped, nunca zero nem «tudo bem».
/// </summary>
public sealed class PerfAnalyzerTests
{
    private static PowerSample S(double t, int phase, int? ma = null, double? v = null,
        double? temp = null, int? pct = null, bool? charging = null,
        double? soc = null, double? freq = null) =>
        new(t, phase, ma, v, temp, pct, charging, null, soc, freq);

    private static PerfPageStats Page(string engine = "webgl2", int frames = 9000,
        double secs = 300, double? fpsAvg = 30, double? fpsMin = 24,
        bool lost = false) =>
        new(engine, frames, secs, fpsAvg, fpsMin, lost);

    /// <summary>Sessão típica: 15 s base + 300 s stress + 20 s recuperação, a 2 s.</summary>
    private static List<PowerSample> SessaoCompleta(
        double battTemp = 34, double socTemp = 52, double freqEarly = 1800,
        double freqLate = 1750, int? pctStart = 87, bool charging = false)
    {
        var s = new List<PowerSample>();
        for (var i = 0; i < 8; i++)
            s.Add(S(i * 2, 1, ma: 480, v: 4.95, temp: 30, pct: pctStart, charging: true));
        for (var i = 0; i < 150; i++)
        {
            var t = 16 + i * 2;
            var freq = i < 75 ? freqEarly : freqLate;
            var pct = pctStart is { } p0 ? (int?)Math.Max(0, p0 - i / 30) : null;
            s.Add(S(t, 2, ma: 620, v: 4.80, temp: battTemp, pct: pct,
                charging: charging, soc: socTemp, freq: freq));
        }
        for (var i = 0; i < 10; i++)
            s.Add(S(316 + i * 2, 3, ma: 480, v: 4.95, temp: 33, pct: pctStart, charging: true));
        return s;
    }

    private static DeviceReport RelatorioDe(PerfProfileResult r)
    {
        var report = new DeviceReport();
        r.ApplyTo(report);
        return report;
    }

    private static CheckResult Check(DeviceReport r, string key) => r.Results[key];

    // ── caminho feliz ──────────────────────────────────────────────────

    [Fact]
    public void SessaoCompleta_MedidasInfoEPass()
    {
        var r = PerfAnalyzer.Analyze(SessaoCompleta(), Page(), 300, usbDrops: 0);
        var report = RelatorioDe(r);

        Assert.Equal("info", Check(report, "perf.session").Status);
        Assert.Equal("pass", Check(report, "perf.workload").Status);
        Assert.Equal("info", Check(report, "perf.thermal").Status);
        Assert.Contains("SoC pico", Check(report, "perf.thermal").Value);
        Assert.Equal("info", Check(report, "perf.current").Status);
        Assert.Contains("mA", Check(report, "perf.current").Value);
        Assert.Equal("pass", Check(report, "perf.usb").Status);
        Assert.Equal("info", Check(report, "perf.throttle").Status);
        Assert.Equal("info", Check(report, "perf.battery").Status);
        Assert.Contains("%", Check(report, "perf.battery").Value);
        Assert.Contains("pico", r.Summary);
    }

    [Fact]
    public void PaginaNull_WorkloadSkipped()
    {
        // Sem confirmação da página as medições podem ser de idle — o check
        // diz-o em vez de aprovar um stress que pode não ter corrido.
        var r = PerfAnalyzer.Analyze(SessaoCompleta(), page: null, 300, 0);
        var report = RelatorioDe(r);
        Assert.Equal("skipped", Check(report, "perf.workload").Status);
    }

    [Fact]
    public void MotorNone_WorkloadWarn()
    {
        var r = PerfAnalyzer.Analyze(SessaoCompleta(), Page(engine: "none", frames: 0, secs: 0.1), 300, 0);
        Assert.Equal("warn", RelatorioDe(r).Results["perf.workload"].Status);
    }

    [Fact]
    public void ContextoPerdido_WorkloadWarn()
    {
        var r = PerfAnalyzer.Analyze(SessaoCompleta(), Page(lost: true), 300, 0);
        Assert.Equal("warn", RelatorioDe(r).Results["perf.workload"].Status);
    }

    [Fact]
    public void StressCurto_WorkloadWarn()
    {
        // Correveu menos de metade do pedido — não mede nada útil.
        var r = PerfAnalyzer.Analyze(SessaoCompleta(), Page(secs: 100), 300, 0);
        Assert.Equal("warn", RelatorioDe(r).Results["perf.workload"].Status);
    }

    [Fact]
    public void FpsBaixo_WorkloadWarn()
    {
        var r = PerfAnalyzer.Analyze(SessaoCompleta(), Page(fpsAvg: 4), 300, 0);
        Assert.Equal("warn", RelatorioDe(r).Results["perf.workload"].Status);
    }

    // ── honestidade: métricas não expostas → skipped ───────────────────

    [Fact]
    public void SemCorrenteEmNenhumaAmostra_CurrentSkipped()
    {
        // Mesma regra da #53: driver sem mA não é defeito — é falta de medição.
        var s = SessaoCompleta();
        var semMa = s.Select(x => x with { Milliamps = null }).ToList();
        var report = RelatorioDe(PerfAnalyzer.Analyze(semMa, Page(), 300, 0));
        var c = Check(report, "perf.current");
        Assert.Equal("skipped", c.Status);
        Assert.Equal("—", c.Value);
        Assert.Contains("não exposta", c.Detail);
    }

    [Fact]
    public void SemFrequencia_ThrottleSkipped()
    {
        // iOS nunca expõe scaling_cur_freq — skipped, não «0 MHz».
        var s = SessaoCompleta().Select(x => x with { CpuFreqMhz = null }).ToList();
        var report = RelatorioDe(PerfAnalyzer.Analyze(s, Page(), 300, 0));
        Assert.Equal("skipped", Check(report, "perf.throttle").Status);
    }

    [Fact]
    public void ACarregar_BatterySkipped()
    {
        var s = SessaoCompleta(charging: true);
        var report = RelatorioDe(PerfAnalyzer.Analyze(s, Page(), 300, 0));
        Assert.Equal("skipped", Check(report, "perf.battery").Status);
        Assert.Contains("a carregar", Check(report, "perf.battery").Detail);
    }

    [Fact]
    public void SemTemperaturas_ThermalSkipped()
    {
        var s = SessaoCompleta().Select(x => x with { TempC = null, SocTempC = null }).ToList();
        var report = RelatorioDe(PerfAnalyzer.Analyze(s, Page(), 300, 0));
        Assert.Equal("skipped", Check(report, "perf.thermal").Status);
    }

    // ── warns: anomalias reais ─────────────────────────────────────────

    [Fact]
    public void SocAcimaDe70_ThermalWarn()
    {
        var report = RelatorioDe(PerfAnalyzer.Analyze(SessaoCompleta(socTemp: 72), Page(), 300, 0));
        Assert.Equal("warn", Check(report, "perf.thermal").Status);
    }

    [Fact]
    public void CelulaAcimaDe45_ThermalWarn()
    {
        var report = RelatorioDe(PerfAnalyzer.Analyze(SessaoCompleta(battTemp: 47), Page(), 300, 0));
        Assert.Equal("warn", Check(report, "perf.thermal").Status);
    }

    [Fact]
    public void QuedaUsb_NoPerfil_ViraWarn()
    {
        var report = RelatorioDe(PerfAnalyzer.Analyze(SessaoCompleta(), Page(), 300, usbDrops: 3));
        var c = Check(report, "perf.usb");
        Assert.Equal("warn", c.Status);
        Assert.Contains("3", c.Value);
    }

    [Fact]
    public void FrequenciaCai40PorCento_ThrottleWarn()
    {
        var report = RelatorioDe(PerfAnalyzer.Analyze(
            SessaoCompleta(freqEarly: 2000, freqLate: 1100), Page(), 300, 0));
        Assert.Equal("warn", Check(report, "perf.throttle").Status);
        Assert.Contains("MHz", Check(report, "perf.throttle").Value);
    }

    [Fact]
    public void FrequenciaEstavel_ThrottleInfo()
    {
        var report = RelatorioDe(PerfAnalyzer.Analyze(
            SessaoCompleta(freqEarly: 1800, freqLate: 1780), Page(), 300, 0));
        Assert.Equal("info", Check(report, "perf.throttle").Status);
    }

    [Fact]
    public void BateriaDesce_InfoComDelta()
    {
        var report = RelatorioDe(PerfAnalyzer.Analyze(SessaoCompleta(pctStart: 87), Page(), 300, 0));
        var c = Check(report, "perf.battery");
        Assert.Equal("info", c.Status);
        Assert.Contains("−4%", c.Value); // 87%→83% de bateria em ~5 min de stress
    }

    // ── sessão vazia / parcial ─────────────────────────────────────────

    [Fact]
    public void SessaoVazia_TudoSkippedSemGrauNemScore()
    {
        // Como na bancada: uma sessão sem amostras não pode fabricar
        // verificações positivas nem inflar coverage/saúde.
        var r = PerfAnalyzer.Analyze(new List<PowerSample>(), page: null, 300, 0);
        var report = RelatorioDe(r);

        Assert.Equal("skipped", Check(report, "perf.session").Status);
        Assert.Equal("skipped", Check(report, "perf.workload").Status);
        Assert.Equal("skipped", Check(report, "perf.usb").Status);
        Assert.Equal("—", Grader.Compute(report).Grade);
        Assert.Null(HealthScore.Compute(report).Score);
    }

    [Fact]
    public void SoBase_SemFaseStress_MetricasSkipped()
    {
        // A página nunca arrancou: só há amostras de base — as métricas de
        // stress ficam skipped em vez de medir idle como se fosse carga.
        var s = SessaoCompleta().Where(x => x.Phase == 1).ToList();
        var report = RelatorioDe(PerfAnalyzer.Analyze(s, page: null, 300, 0));
        Assert.Equal("skipped", Check(report, "perf.thermal").Status);
        Assert.Equal("skipped", Check(report, "perf.current").Status);
        Assert.Equal("skipped", Check(report, "perf.throttle").Status);
        Assert.Equal("skipped", Check(report, "perf.battery").Status);
        Assert.Equal("info", Check(report, "perf.session").Status);
    }

    // ── efeito no grau e na saúde ──────────────────────────────────────

    [Fact]
    public void PerfUsbWarn_CapaGrauComoOBanc()
    {
        var report = new DeviceReport();
        report.Set("battery.capacity", "pass", "91%");
        report.Set("battery.health", "pass", "good");
        report.Set("identity.imei", "pass", "35…");
        report.Set("security.bootloader", "pass", "locked");
        Copiar(PerfAnalyzer.Analyze(SessaoCompleta(), Page(), 300, usbDrops: 2), report);

        Assert.Equal("C", Grader.Compute(report).Grade);
        Assert.Contains(Grader.Compute(report).Reasons, r => r.Contains("USB"));
    }

    [Fact]
    public void PerfThermalWarn_CapaGrau()
    {
        var report = new DeviceReport();
        report.Set("battery.capacity", "pass", "91%");
        report.Set("battery.health", "pass", "good");
        report.Set("identity.imei", "pass", "35…");
        report.Set("security.bootloader", "pass", "locked");
        Copiar(PerfAnalyzer.Analyze(SessaoCompleta(socTemp: 75), Page(), 300, 0), report);

        Assert.Equal("C", Grader.Compute(report).Grade);
    }

    [Fact]
    public void PerfUsbPass_ContribuiParaACategoriaEstabilidade()
    {
        // perf.usb está na categoria estabilidade do health score — sem ele
        // a categoria ficava sem dados; com o perfil estável pontua 100.
        var report = new DeviceReport();
        Copiar(PerfAnalyzer.Analyze(SessaoCompleta(), Page(), 300, 0), report);

        var h = HealthScore.Compute(report);
        var stability = h.Categories.First(c => c.Key == "stability");
        Assert.True(stability.Score == 100, $"esperado 100, veio {stability.Score}");
    }

    /// <summary>Grava os checks perf.* de uma sessão num relatório — como o ApplyTo.</summary>
    private static void Copiar(PerfProfileResult r, DeviceReport report)
    {
        var tmp = new DeviceReport();
        r.ApplyTo(tmp);
        foreach (var kv in tmp.Results)
            report.Results[kv.Key] = kv.Value;
    }

    // ── PerfPageStats.FromJson ─────────────────────────────────────────

    private static Dictionary<string, JsonElement> Payload(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void PageStats_ParseCompleto()
    {
        var p = PerfPageStats.FromJson(Payload(
            "{\"perfEngine\":\"webgl\",\"perfFrames\":9120,\"perfSecs\":298.4,"
            + "\"perfFpsAvg\":30.6,\"perfFpsMin\":21.2,\"perfContextLost\":false}"));
        Assert.NotNull(p);
        Assert.Equal("webgl", p!.Engine);
        Assert.Equal(9120, p.Frames);
        Assert.Equal(298.4, p.Secs);
        Assert.Equal(30.6, p.FpsAvg);
        Assert.Equal(21.2, p.FpsMin);
        Assert.False(p.ContextLost);
    }

    [Fact]
    public void PageStats_SemFramesouSecs_Null()
    {
        // O mínimo que distingue medição de ruído: frames e segundos.
        Assert.Null(PerfPageStats.FromJson(Payload("{\"perfEngine\":\"webgl\"}")));
        Assert.Null(PerfPageStats.FromJson(Payload("{\"perfFrames\":\"muitos\",\"perfSecs\":300}")));
        Assert.Null(PerfPageStats.FromJson(Payload("{}")));
    }

    [Fact]
    public void PageStats_OpcionaisAusentes_NaoRebentam()
    {
        var p = PerfPageStats.FromJson(Payload("{\"perfFrames\":10,\"perfSecs\":30}"));
        Assert.NotNull(p);
        Assert.Null(p!.Engine);
        Assert.Null(p.FpsAvg);
        Assert.False(p.ContextLost);
    }
}
