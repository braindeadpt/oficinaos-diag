using System.Globalization;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>Parses do adb sob a cultura que mais dói: Windows pt-PT.</summary>
public sealed class AdbParsingTests
{
    /// <summary>Corre o teste com CurrentCulture=pt-PT (onde "." é milhar).</summary>
    private static T InPtPt<T>(Func<T> f)
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pt-PT");
            return f();
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // ── B-05: ponto decimal não pode virar separador de milhares ─────────

    [Fact]
    public void SocTemp_PtPt_LePontoComoDecimal()
    {
        var c = InPtPt(() => AdbParsing.ParseSocTemp("cpu-0-0:38.5\nbattery:28.0\n"));
        Assert.Equal(38.5, c);
    }

    [Fact]
    public void SocTemp_Miligraus_ConverteParaCelsius()
    {
        var c = InPtPt(() => AdbParsing.ParseSocTemp("cpu-0-0:38500\nsoc:40250\n"));
        Assert.Equal(40.25, c);
    }

    [Fact]
    public void SocTemp_IgnoraZonasDummy()
    {
        // Kernels Qualcomm expõem 125000 e -273000 em zonas que não são reais.
        var c = InPtPt(() => AdbParsing.ParseSocTemp("cpu-0-0:-273000\ncpu-1-0:125000\nbattery:30\n"));
        Assert.Null(c);
    }

    // ── B-06: polaridade da corrente por OEM ────────────────────────────

    [Fact]
    public void CorrenteNegativaComStatusCharging_ECarga()
    {
        // Samsung/Exynos e MediaTek: -1.2 A (µA) a carregar.
        var (mA, charging) = AdbParsing.ParseProbeCurrent("-1200000", "Charging");
        Assert.Equal(1200, mA);
        Assert.True(charging);
    }

    [Fact]
    public void CorrentePositivaComStatusDischarging_EDescarga()
    {
        var (mA, charging) = AdbParsing.ParseProbeCurrent("850000", "Discharging");
        Assert.Equal(850, mA);
        Assert.False(charging);
    }

    [Fact]
    public void SemStatus_UsaOSinalComoUltimoRecurso()
    {
        var (_, charging) = AdbParsing.ParseProbeCurrent("-500000", "_");
        Assert.False(charging);
    }

    [Fact]
    public void StatusFull_ContaComoCarregando()
    {
        var (_, charging) = AdbParsing.ParseProbeCurrent("0", "Full");
        Assert.True(charging);
    }

    [Fact]
    public void BatteryProbe_LeCincoLinhasComPlaceholders()
    {
        var (mA, charging, volts, tempC, pct) =
            AdbParsing.ParseBatteryProbe("-1200000\n4200000\n350\nCharging\n72\n");
        Assert.Equal(1200, mA);
        Assert.True(charging);
        Assert.Equal(4.2, volts);
        Assert.Equal(35, tempC);
        Assert.Equal(72, pct);
    }

    [Fact]
    public void BatteryProbe_TemperaturaZero_NaoEVirouMedicao()
    {
        var (_, _, _, tempC, _) = AdbParsing.ParseBatteryProbe("_\n_\n0\n_\n_\n");
        Assert.Null(tempC);
    }

    // ── B-11: contas sem duplicar e capacidade em mAh ───────────────────

    [Fact]
    public void ContasRepetidasNoDump_ContamUma()
    {
        var line = "  Account {name=maria@gmail.com, type=com.google}\n";
        var dump = "Accounts: 1\n" + string.Concat(Enumerable.Repeat(line, 12))
            + "  mSyncables: type=com.google\n";
        Assert.Equal(1, AdbParsing.ParseAccountCount(dump));
    }

    [Fact]
    public void ContasDistintas_ContamTodas()
    {
        var dump =
            "  Account {name=a@gmail.com, type=com.google}\n" +
            "  Account {name=b@samsung.com, type=com.samsung.android.account}\n";
        Assert.Equal(2, AdbParsing.ParseAccountCount(dump));
    }

    [Fact]
    public void Capacidade_EmMicroAh_ViraMah()
    {
        Assert.Equal(4850, AdbParsing.NormalizeMah(4_850_000));
        Assert.Equal(5000, AdbParsing.NormalizeMah(5000));
    }

    // ── B-19: MDM só com owner real ─────────────────────────────────────

    [Fact]
    public void DeviceOwnerTypeMenos1_NaoEMdm()
    {
        Assert.False(AdbParsing.HasDeviceOwner("    Device Owner Type: -1\n"));
    }

    [Fact]
    public void DeviceOwnerComComponente_EMdm()
    {
        Assert.True(AdbParsing.HasDeviceOwner("  Device Owner: com.empresa.mdm/.AdminReceiver\n"));
        Assert.True(AdbParsing.HasDeviceOwner("    Device Owner Type: 1\n"));
    }

    // ── B-20: bateria ausente ───────────────────────────────────────────

    [Fact]
    public void PresentFalse_EBancada()
    {
        Assert.False(AdbParsing.ParsePresent("present: false\nlevel: 0\n"));
        Assert.True(AdbParsing.ParsePresent("present: true\nlevel: 80\n"));
        Assert.Null(AdbParsing.ParsePresent("level: 80\n"));
    }
}
