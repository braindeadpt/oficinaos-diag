using OficinaDiag.Devices;
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
