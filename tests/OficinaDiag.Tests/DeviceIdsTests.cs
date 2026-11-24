using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// DeviceIds.Same — o DeviceDetector sufixa o Id com '!' nos estados
/// pendentes (unauthorized/recovery/sideload/fastboot) e tira-o no ready.
/// O mesmo telemóvel muda de Id à autorização: as guardas de relatório e
/// a preservação no re-scan comparam pelo id canónico, nunca pelo cru.
/// </summary>
public sealed class DeviceIdsTests
{
    [Fact]
    public void MesmoAparelhoIgnoraOSufixoDeEstadoPendente()
    {
        // O caso real: checklist feito com o Android por autorizar
        // ("ABC123!") e o mesmo aparelho já ready ("ABC123") depois de o
        // técnico aceitar a depuração — não é outro telemóvel.
        Assert.True(DeviceIds.Same("ABC123!", "ABC123"));
        Assert.True(DeviceIds.Same("ABC123", "ABC123!"));
        Assert.True(DeviceIds.Same("ABC123!", "ABC123!"));
        Assert.True(DeviceIds.Same("ABC123", "ABC123"));
    }

    [Fact]
    public void AparelhosDiferentesNuncaCoincidem()
    {
        Assert.False(DeviceIds.Same("ABC123!", "XYZ789"));
        Assert.False(DeviceIds.Same("ABC123", "ABC124"));
        Assert.False(DeviceIds.Same("ABC123!", "ABC1234")); // prefixo não basta
    }

    [Fact]
    public void CanonicalRemoveTodosOsSufixosPendente()
    {
        Assert.Equal("ABC123", DeviceIds.Canonical("ABC123!"));
        Assert.Equal("ABC123", DeviceIds.Canonical("ABC123"));
        Assert.Equal("000011122233334444", DeviceIds.Canonical("000011122233334444")); // UDID iOS intacto
        Assert.Null(DeviceIds.Canonical(null));
        Assert.Equal("", DeviceIds.Canonical(""));
    }

    [Fact]
    public void SameTrataNulosSemInventarCoincidencias()
    {
        Assert.True(DeviceIds.Same(null, null));   // paridade com == anterior
        Assert.False(DeviceIds.Same(null, "ABC123"));
        Assert.False(DeviceIds.Same("ABC123", null));
    }
}
