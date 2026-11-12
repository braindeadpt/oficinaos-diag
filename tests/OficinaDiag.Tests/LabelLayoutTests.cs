using OficinaDiag.Reports;
using Xunit;

namespace OficinaDiag.Tests;

public class LabelLayoutTests
{
    // Áreas úteis em DIP (1 DIP = 1/96"), que é o que o PrintDialog devolve.
    // A4: 210×297 mm → 794×1123; Brother 62 mm: 234 de largura.

    [Fact]
    public void A4RetratoMantemATiquetaNoTamanhoFisico()
    {
        var l = LabelLayout.Fit(794, 1123);

        Assert.False(l.Rotate);
        Assert.Equal(1.0, l.Scale, 3);
    }

    [Fact]
    public void A4PaisagemTambemNaoRoda()
    {
        var l = LabelLayout.Fit(1123, 794);

        Assert.False(l.Rotate);
        Assert.Equal(1.0, l.Scale, 3);
    }

    [Fact]
    public void RoloVerticalEstreitoRodaEOcupaOTamanhoReal()
    {
        // Brother 62 mm: 234 DIP de largura e mais alto que largo.
        var l = LabelLayout.Fit(234, 378);

        Assert.True(l.Rotate);
        // Rodada, a pegada é 150×280 e cabe nos 234×378 sem encolher.
        Assert.Equal(1.0, l.Scale, 3);
    }

    [Fact]
    public void RoloVerticalAindaMaisEstreitoEncolheSobreAPegadaRodada()
    {
        // 120 DIP de largura: roda, mas 150 de largura rodada já não cabe.
        var l = LabelLayout.Fit(120, 400);

        Assert.True(l.Rotate);
        Assert.Equal(0.8, l.Scale, 3); // 120 / 150
    }

    [Fact]
    public void RoloHorizontalEncolheSemRodar()
    {
        // Dymo 57×32 mm: 215 DIP de largura, 121 de altura — mais largo que alto.
        var l = LabelLayout.Fit(215, 121);

        Assert.False(l.Rotate);
        Assert.Equal(215.0 / 280.0, l.Scale, 3);
    }

    [Fact]
    public void ZebraPequenaEncolheSemRodar()
    {
        // Zebra 50×30 mm: 189×113 DIP.
        var l = LabelLayout.Fit(189, 113);

        Assert.False(l.Rotate);
        Assert.Equal(189.0 / 280.0, l.Scale, 3);
    }

    [Fact]
    public void QuadradaNaoRodaEEscalaPelaLargura()
    {
        var l = LabelLayout.Fit(200, 200);

        Assert.False(l.Rotate);
        Assert.Equal(200.0 / 280.0, l.Scale, 3);
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(500, 0)]
    [InlineData(-1, 500)]
    [InlineData(double.NaN, 500)]
    [InlineData(500, double.PositiveInfinity)]
    public void AreaInvalidaNaoTransforma(double width, double height)
    {
        var l = LabelLayout.Fit(width, height);

        Assert.False(l.Rotate);
        Assert.Equal(1.0, l.Scale, 3);
    }
}
