using OficinaDiag.Maintenance;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Parsers do módulo de manutenção — a regra que os une: sem leitura real,
/// null. Um zero inventado aqui vira um "depois" falso no relatório do cliente.
/// </summary>
public class MaintenanceParsingTests
{
    [Fact]
    public void ParseDf_ExtraiUsadoELivreDaLinhaDoData()
    {
        var df = """
        Filesystem     1K-blocks     Used Available Use% Mounted on
        /dev/block/dm-7  115296000 82345678  30472322  74% /data
        tmpfs              3932160        0   3932160   0% /dev
        """;
        var (used, avail) = MaintenanceParsing.ParseDf(df)!.Value;
        Assert.Equal(82345678, used);
        Assert.Equal(30472322, avail);
    }

    [Fact]
    public void ParseDf_SemLinhaDeData_DevolveNull()
    {
        Assert.Null(MaintenanceParsing.ParseDf("tmpfs 3932160 0 3932160 0% /dev"));
        Assert.Null(MaintenanceParsing.ParseDf(null));
        Assert.Null(MaintenanceParsing.ParseDf(""));
    }

    [Fact]
    public void ParseMemAvailableKb_LeAChaveCerta()
    {
        var mem = "MemTotal:       7700000 kB\nMemAvailable:   3123456 kB\nBuffers:         123456 kB";
        Assert.Equal(3123456, MaintenanceParsing.ParseMemAvailableKb(mem));
        Assert.Null(MaintenanceParsing.ParseMemAvailableKb("MemTotal: 1 kB"));
        Assert.Null(MaintenanceParsing.ParseMemAvailableKb(null));
    }

    [Fact]
    public void ParseUptimeSeconds_PegaOPrimeiroCampo()
    {
        Assert.Equal(12345.67, MaintenanceParsing.ParseUptimeSeconds("12345.67 89012.34"));
        Assert.Null(MaintenanceParsing.ParseUptimeSeconds(""));
        Assert.Null(MaintenanceParsing.ParseUptimeSeconds(null));
    }

    [Fact]
    public void ParseBattery_LeNivelETemperatura()
    {
        var dumpsys = "Current Battery Service state:\n  AC powered: false\n  level: 87\n  temperature: 312\n";
        var (level, temp) = MaintenanceParsing.ParseBattery(dumpsys);
        Assert.Equal(87, level);
        Assert.Equal(31.2, temp);
    }

    [Fact]
    public void ParseBattery_TemperaturaZeroEDaAusenciaDeLeitura()
    {
        var (level, temp) = MaintenanceParsing.ParseBattery("level: 50\ntemperature: 0");
        Assert.Equal(50, level);
        Assert.Null(temp); // 0 °C não é uma leitura real
    }

    [Fact]
    public void ParseSettingsDump_LeEscalasENumOrdemFixa()
    {
        var dump = "1.0\n0.5\nnull\n1";
        var (anim, lowPower) = MaintenanceParsing.ParseSettingsDump(dump);
        Assert.Equal(1.0, anim!.Window);
        Assert.Equal(0.5, anim.Transition);
        Assert.Null(anim.Animator); // "null" = chave nunca escrita, não escala 0
        Assert.Equal(1, lowPower);
    }

    [Fact]
    public void ParseSettingsDump_SemLeituraDevolveAnimacoesDesconhecidas()
    {
        // null (adb não respondeu) tem de ficar distinto de "null" por linha
        // (chave nunca escrita): só o segundo é seguro repor com delete.
        var (anim, lowPower) = MaintenanceParsing.ParseSettingsDump(null);
        Assert.Null(anim);
        Assert.Null(lowPower);
        var (anim2, _) = MaintenanceParsing.ParseSettingsDump("   ");
        Assert.Null(anim2);
    }

    [Fact]
    public void ParseSettingsDump_LeituraParcialDevolveDesconhecido()
    {
        // Menos de 4 linhas = leitura parcial: as posições já não correspondem
        // às chaves, por isso nada é reposto com base nelas.
        var (anim, lowPower) = MaintenanceParsing.ParseSettingsDump("1.0\n0.5");
        Assert.Null(anim);
        Assert.Null(lowPower);
    }

    [Fact]
    public void ParseScale_NullDoSistemaEDistintoDeZero()
    {
        Assert.Null(MaintenanceParsing.ParseScale("null"));
        Assert.Equal(0.0, MaintenanceParsing.ParseScale("0"));
        Assert.Equal(0.5, MaintenanceParsing.ParseScale("0.5"));
        Assert.Null(MaintenanceParsing.ParseScale("lixo"));
    }

    [Fact]
    public void ParseProcessCount_ContaOLinhaDoWc()
    {
        Assert.Equal(123, MaintenanceParsing.ParseProcessCount("123"));
        Assert.Null(MaintenanceParsing.ParseProcessCount(null));
        Assert.Null(MaintenanceParsing.ParseProcessCount("0"));
    }

    [Fact]
    public void ParseUserPackages_SoAppsDeUtilizador()
    {
        var pm = "package:com.whatsapp\npackage:com.instagram.android\npackage:com.android.vending\n";
        var apps = MaintenanceParsing.ParseUserPackages(pm);
        Assert.Equal(["com.whatsapp", "com.instagram.android", "com.android.vending"], apps);
        Assert.Empty(MaintenanceParsing.ParseUserPackages(null));
    }

    [Theory]
    [InlineData("com.whatsapp")]
    [InlineData("com.instagram.android")]
    [InlineData("pt.gov.tribunais.app")]
    public void IsValidPackageName_AceitaNomesReais(string pkg) =>
        Assert.True(MaintenanceParsing.IsValidPackageName(pkg));

    [Theory]
    [InlineData("com.foo; rm -rf /")]
    [InlineData("com.foo$(id)")]
    [InlineData("com.foo|sh")]
    [InlineData("com.foo`id`")]
    [InlineData("1com.foo")]
    [InlineData("com")]
    [InlineData("")]
    [InlineData(null)]
    public void IsValidPackageName_RejeitaInjecaoDeShell(string? pkg) =>
        Assert.False(MaintenanceParsing.IsValidPackageName(pkg));
}
