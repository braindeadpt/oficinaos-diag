using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Health;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>Sessão SCAN+ — análise de carga, resistência interna e térmica.</summary>
public sealed class PowerAnalyzerTests
{
    private static PowerSample S(double t, int phase, int? ma = null, double? v = null,
        double? temp = null, bool? charging = null, double? soc = null) =>
        new(t, phase, ma, v, temp, null, charging, null, soc);

    [Fact]
    public void SemCarga_Warn()
    {
        var s = new List<PowerSample>
        {
            S(1, 1, ma: 0, charging: false), S(2, 1, ma: 0, charging: false),
            S(3, 3, ma: 0, charging: false),
        };
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal("warn", r.ChargeStatus);
        Assert.Contains("não entra carga", r.ChargeDetail);
    }

    [Fact]
    public void SemTelemetriaDeCorrente_NaoEMedicaoSemCarga()
    {
        // Regressão Devin Review: driver que não expõe mA produzia o mesmo
        // warn "não entra carga" de uma porta morta — e o health score
        // descontava 45 pts numa medição que nunca aconteceu. Sem nenhuma
        // amostra de corrente → skipped, neutro no score e na coverage.
        var s = new List<PowerSample>
        {
            S(1, 1, v: 4.95, temp: 30), S(2, 1, v: 4.95, temp: 30),
            S(15, 2, v: 4.95, temp: 31), S(35, 3, v: 4.95, temp: 30),
        };
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal("skipped", r.ChargeStatus);
        Assert.Equal("—", r.ChargeValue); // "0 mA" sugeria leitura real

        var report = new DeviceReport();
        r.ApplyTo(report);
        Assert.Equal("skipped", report.Results["power.charge"].Status);
    }

    [Fact]
    public void CorrenteBaixaMedida_MantemWarn()
    {
        // Contraste: corrente exposta e efetivamente baixa É evidência de
        // não carregar — o warn fica e o desconto no health score é justo.
        var s = new List<PowerSample>
        {
            S(1, 1, ma: 10, charging: true), S(2, 1, ma: 10, charging: true),
            S(35, 3, ma: 10, charging: true),
        };
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal("warn", r.ChargeStatus);
        Assert.Contains("não entra carga", r.ChargeDetail);
    }

    [Fact]
    public void CorrenteSoNaFaseDeEsforco_ContaComoSemTelemetria()
    {
        // Regressão Devin Review: o diagnóstico de carga ignora a fase 2
        // (esforço) mas a verificação de telemetria incluía-na — se só
        // essa fase tivesse mA, a bancada avisava «sem telemetria» com
        // corrente medida. Os dois critérios têm de olhar para o mesmo
        // conjunto de fases.
        var s = new List<PowerSample>
        {
            S(1, 1, v: 4.95, temp: 30), S(15, 2, ma: 500, v: 4.95, temp: 31),
            S(35, 3, v: 4.95, temp: 30),
        };
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal("skipped", r.ChargeStatus);
    }

    [Fact]
    public void UsbDrops_ContaSoDuranteASessao()
    {
        // Regressões Devin Review: (ronda 3) repetir a bancada na mesma
        // janela mantinha as quedas da tentativa anterior; (ronda 4) o
        // contador ficava ativo até a janela fechar — desligar o cabo
        // depois do teste marcava a ligação estável como instável. O
        // relatório mede estabilidade DURANTE os ~60 s de sessão.
        var c = new UsbDropCounter("dev-1");
        // Antes da primeira sessão e depois do fim dela, não conta.
        c.OnDeviceDetached("dev-1");
        Assert.Equal(0, c.Count);

        c.Begin();
        c.OnDeviceDetached("dev-1");
        c.OnDeviceDetached("outro-dev"); // queda de outro aparelho não conta
        c.OnDeviceDetached("dev-1");
        Assert.Equal(2, c.Count);

        c.End(); // fim das medições — o resto já não é do teste
        c.OnDeviceDetached("dev-1");
        Assert.Equal(2, c.Count);

        // Repetição: Begin repõe a contagem da tentativa anterior.
        c.Begin();
        c.OnDeviceDetached("dev-1");
        Assert.Equal(1, c.Count);
    }

    [Fact]
    public void SessaoVazia_NaoEscreveChecksPositivos()
    {
        // Regressão Devin Review: uma bancada sem amostras gravava
        // usb.stability=pass («ligação estável») e power.session=info —
        // o relatório saía com grau A e saúde 100/100 sem medir nada.
        // Agora tudo fica skipped e o efeito dominó fecha: sem checks com
        // dados não há grau nem score.
        var r = PowerAnalyzer.Analyze(new List<PowerSample>());
        var report = new DeviceReport();
        r.ApplyTo(report);

        Assert.Equal("skipped", report.Results["power.charge"].Status);
        Assert.Equal("skipped", report.Results["usb.stability"].Status);
        Assert.Equal("skipped", report.Results["power.session"].Status);
        Assert.Equal("—", Grader.Compute(report).Grade);
        Assert.Null(HealthScore.Compute(report).Score);
    }

    [Fact]
    public void CargaPcNormal_InfoEWatts()
    {
        var s = new List<PowerSample>();
        // porta USB do PC: ~500 mA @ 5 V ≈ 2.5 W — saudável
        for (var i = 0; i < 5; i++) s.Add(S(i, 1, ma: 480, v: 4.95, charging: true));
        for (var i = 0; i < 5; i++) s.Add(S(15 + i, 2, ma: 480, v: 4.95, charging: true));
        for (var i = 0; i < 5; i++) s.Add(S(35 + i, 3, ma: 480, v: 4.95, charging: true));
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal("info", r.ChargeStatus);
        Assert.NotNull(r.RealWatts);
        Assert.InRange(r.RealWatts!.Value, 2.0, 2.8);
    }

    [Fact]
    public void CargaFraca_Warn()
    {
        var s = new List<PowerSample>();
        for (var i = 0; i < 5; i++) s.Add(S(i, 1, ma: 100, v: 4.9, charging: true));
        for (var i = 0; i < 5; i++) s.Add(S(35 + i, 3, ma: 100, v: 4.9, charging: true));
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal("warn", r.ChargeStatus); // ~0.49 W < 0.8
    }

    [Fact]
    public void ResistenciaInterna_SagMedido()
    {
        var s = new List<PowerSample>();
        // base: carrega a 500 mA @ ~4.2 V
        for (var i = 0; i < 5; i++) s.Add(S(i, 1, ma: 500, v: 4.20, charging: true));
        // carga: telemóvel puxa da bateria (charging false → net negative), V afunda
        for (var i = 0; i < 6; i++) s.Add(S(15 + i, 2, ma: 800, v: 3.95, charging: false));
        for (var i = 0; i < 5; i++) s.Add(S(35 + i, 3, ma: 500, v: 4.20, charging: true));
        var r = PowerAnalyzer.Analyze(s);
        Assert.NotNull(r.InternalResistanceMohm);
        // ΔV ≈ 0.25 V · ΔI ≈ 1.3 A → ~190 mΩ
        Assert.InRange(r.InternalResistanceMohm!.Value, 120, 400);
    }

    [Fact]
    public void Termal_CelulaAqueceMaisQueSoc()
    {
        var s = new List<PowerSample>
        {
            S(0, 1, temp: 25, soc: 28), S(1, 1, temp: 25, soc: 28),
            S(20, 2, temp: 33, soc: 30), S(40, 3, temp: 34, soc: 30),
        };
        var r = PowerAnalyzer.Analyze(s);
        Assert.Equal(9, r.BatteryRiseC);   // 34 - 25
        Assert.Equal(2, r.SocRiseC);       // 30 - 28

        var report = new DeviceReport();
        r.ApplyTo(report);
        Assert.True(report.Results.TryGetValue("power.thermal", out var t));
        Assert.Equal("warn", t!.Status); // célula +7 sobre SoC ≥ 3
    }

    [Fact]
    public void UsbDrops_ViraWarn()
    {
        var r = PowerAnalyzer.Analyze(new List<PowerSample> { S(0, 1, ma: 100, charging: true) });
        r.UsbDrops = 2;
        var report = new DeviceReport();
        r.ApplyTo(report);
        Assert.Equal("warn", report.Results["usb.stability"].Status);
        Assert.Contains("2", report.Results["usb.stability"].Value);
    }
}
