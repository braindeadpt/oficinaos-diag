using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Regras do <see cref="AndroidDecisions"/>: o que o coletor escreve no
/// relatório quando não há hardware para perguntar. Cobrem os critérios do
/// `T-03` (bootloader/MDM/bateria ausente) e do `T-09` (capacidade Samsung).
/// </summary>
public class AndroidDecisionsTests
{
    [Theory]
    [InlineData(null, "skipped", "prop ausente")]
    [InlineData("green", "pass", "green")]
    [InlineData("orange", "warn", "orange")]
    [InlineData("yellow", "warn", "yellow")]
    public void Bootloader_PropAusenteESkippedEDesbloqueadoEWarn(
        string? prop, string status, string value)
    {
        var r = AndroidDecisions.Bootloader(prop);

        Assert.Equal(status, r.Status);
        Assert.Equal(value, r.Value);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Bootloader_AusenteExplicaPorqueNaoEVerificavel() =>
        Assert.Contains("não verificável", AndroidDecisions.Bootloader(null).Note);

    [Theory]
    [InlineData(false, "pass", "sem gestão remota")]
    [InlineData(true, "warn", "gestão remota ativa")]
    public void Mdm_SoComDeviceOwnerReal(bool hasOwner, string status, string value)
    {
        var r = AndroidDecisions.Mdm(hasOwner);

        Assert.Equal(status, r.Status);
        Assert.Equal(value, r.Value);
    }

    [Fact]
    public void Mdm_SemGestaoNaoTemNotaDeRisco() =>
        Assert.Null(AndroidDecisions.Mdm(false).Note);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public void BatteryPresence_SoSaltaComPresentFalse(bool? present) =>
        Assert.Equal(present == false, AndroidDecisions.BatteryPresence(present) is not null);

    [Fact]
    public void BatteryPresence_AusenteEDescritaComoSkipped()
    {
        var r = AndroidDecisions.BatteryPresence(false)!.Value;

        Assert.Equal("skipped", r.Status);
        Assert.Contains("present=false", r.Note);
    }

    [Theory]
    [InlineData(86, "pass", "86%")]
    [InlineData(80, "pass", "80%")]
    [InlineData(79, "warn", "79%")]
    [InlineData(60, "warn", "60%")]
    [InlineData(59, "fail", "59%")]
    public void CapacityFromSavedAsoc_LeASaudeDoFuelGauge(
        int asoc, string status, string value)
    {
        var r = AndroidDecisions.CapacityFromSavedAsoc(asoc)!.Value;

        Assert.Equal(status, r.Status);
        Assert.Equal(value, r.Value);
        Assert.Contains("mSavedBatteryAsoc", r.Note);
    }

    [Fact]
    public void CapacityFromSavedAsoc_SemLeituraNaoEscreveCapacidade() =>
        Assert.Null(AndroidDecisions.CapacityFromSavedAsoc(null));
}
