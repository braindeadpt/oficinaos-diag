using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// T-09: nomes comerciais que a loja lê (iPhone por ProductType, Samsung com
/// sufixo regional), saúde de bateria OEM sem sysfs e panics de hardware.
/// </summary>
public sealed class OemHealthTests
{
    // ── catálogo ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("iPhone15,2", "iPhone 14 Pro")]
    [InlineData("iPhone13,3", "iPhone 12 Pro")]
    [InlineData("iPhone17,5", "iPhone 16e")]
    [InlineData("iPhone12,8", "iPhone SE (2ª geração)")]
    [InlineData("iPhone14,6", "iPhone SE (3ª geração)")]
    [InlineData("iPhone18,1", "iPhone 17 Pro")]
    [InlineData("iPhone18,3", "iPhone 17")]
    [InlineData("iPhone18,4", "iPhone Air")]
    [InlineData("iPad14,1", "iPad mini (6ª geração)")]
    public void CatalogoResolveProductTypeApple(string code, string expected) =>
        Assert.Equal(expected, ModelNameTable.Lookup(code));

    [Fact]
    public void SamsungComSufixoRegionalCaiNaBaseEuropeia()
    {
        // A tabela guarda SM-S918B; a loja recebe U1 (EUA), F/DS (dual-SIM)…
        Assert.StartsWith("Galaxy S23 Ultra", ModelNameTable.Lookup("SM-S918U1"));
        Assert.Equal("Galaxy S23 Ultra 5G", ModelNameTable.Lookup("SM-S918B/DS"));
        Assert.Equal("Galaxy S23 Ultra 5G", ModelNameTable.Lookup("sm-s918b"));
        Assert.StartsWith("Galaxy S22", ModelNameTable.Lookup("SM-S901U"));
    }

    [Fact]
    public void CodigoDesconhecidoContinuaNulo()
    {
        Assert.Null(ModelNameTable.Lookup("ZZ-NAO-EXISTE"));
        Assert.Null(ModelNameTable.Lookup(""));
        Assert.Null(ModelNameTable.Lookup(null));
    }

    // ── saúde OEM Samsung ──────────────────────────────────────────────

    [Fact]
    public void SavedAsocDoFuelGaugeEViraPercentagem()
    {
        const string batt = """
              AC powered: false
              USB powered: true
              status: 2
              level: 84
              mSavedBatteryAsoc: 86
              mSavedBatteryUsage: 342
            """;
        Assert.Equal(86, AdbParsing.ParseSavedAsoc(batt));
    }

    [Theory]
    [InlineData("mSavedBatteryAsoc: 0")]     // ausência de leitura
    [InlineData("mSavedBatteryAsoc: 143")]   // lixo do vendor
    [InlineData("level: 84")]                // sem a chave
    [InlineData(null)]
    public void SavedAsocInvalidoDevolveNulo(string? batt) =>
        Assert.Null(AdbParsing.ParseSavedAsoc(batt));

    // ── panic de hardware Apple ────────────────────────────────────────

    [Fact]
    public void ThermalmonitordSemCheckinsEEPanicDeFlex()
    {
        const string panic = """
            {"bug_type":"210","panicString":"panic(cpu 2 caller 0xfffffff): userspace watchdog timeout: no successful checkins from thermalmonitord in 180 seconds"}
            """;
        Assert.True(IosCollector.IsThermalPanic(panic));
        Assert.Contains("thermalmonitord", IosCollector.ThermalPanicHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flex de carga", IosCollector.ThermalPanicHint);
    }

    [Theory]
    [InlineData("panic(cpu 0 caller 0xfffffff): userspace watchdog timeout: no successful checkins from SpringBoard")]
    [InlineData("panicString: ANS2 Recoverable Panic")]
    [InlineData("")]
    public void OutrosPanicsNaoSaoThermal(string panic) =>
        Assert.False(IosCollector.IsThermalPanic(panic));
}
