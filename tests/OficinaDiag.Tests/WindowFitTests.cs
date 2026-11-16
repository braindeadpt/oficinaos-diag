using OficinaDiag.Ui;
using Xunit;

namespace OficinaDiag.Tests;

public class WindowFitTests
{
    /// <summary>1920×1080 físicos vistos em DIP a 100, 125 e 150%.</summary>
    private static AreaEcra FullHd(double escala) => new(0, 0, 1920 / escala, 1080 / escala);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void ACaixaCabeSempreNaAreaUtil(double escala)
    {
        // Pedido maior que a área nos três escalões (2000×1200 > 1920×1080,
        // 1536×864 e 1280×720): é o único formato em que a conta do `Math.Min`
        // é obrigatória em todas as linhas. Com 960×640 a teoria passava mesmo
        // sem teto a 150%, e o mutante do `mutation-check` sobrevivia a citá-la.
        var area = FullHd(escala);
        var c = WindowFit.Calcular(area, 2000, 1200);

        Assert.Equal(area.Width, c.Width, 6);
        Assert.Equal(area.Height, c.Height, 6);
        Assert.Equal(area.Width, c.MaxWidth);
        Assert.Equal(area.Height, c.MaxHeight);
        Assert.InRange(c.Left!.Value, area.Left, area.Left + area.Width - c.Width);
        Assert.InRange(c.Top!.Value, area.Top, area.Top + area.Height - c.Height);
    }

    [Fact]
    public void JanelaQueJaCabeFicaComOTamanhoPedido()
    {
        var area = FullHd(1.0);
        var c = WindowFit.Calcular(area, 960, 640);
        Assert.Equal(960, c.Width);
        Assert.Equal(640, c.Height);
        Assert.True(c.Width <= area.Width && c.Height <= area.Height);
    }

    [Fact]
    public void ACentoDaAreaEDiferenteEmCadaEscala()
    {
        // O centro é o mesmo em DIP por construção — o que muda é o espaço livre.
        var cem = WindowFit.Calcular(FullHd(1.0), 960, 640);
        var centoECinquenta = WindowFit.Calcular(FullHd(1.5), 960, 640);

        Assert.Equal((1920 - 960) / 2.0, cem.Left);
        Assert.Equal((1280 - 960) / 2.0, centoECinquenta.Left);
        Assert.NotEqual(cem.Left, centoECinquenta.Left);
    }

    [Fact]
    public void PortatilPequenoATrezeSeteECincoPorCentoEncolheAJanela()
    {
        // 1366×768 a 150% = 910,7×512 DIP: nem a largura nem a altura pedidas
        // cabem. É o caso real de um portátil de loja com a escala recomendada.
        var area = new AreaEcra(0, 0, 1366 / 1.5, 768 / 1.5);
        var c = WindowFit.Calcular(area, 960, 640);

        Assert.Equal(area.Width, c.Width, 3);
        Assert.Equal(area.Height, c.Height, 3);
        Assert.Equal(0, c.Left!.Value, 3);
        Assert.Equal(0, c.Top!.Value, 3);
    }

    [Fact]
    public void JanelaPequenaNaoECrescidaPelaArea()
    {
        // Ficar dentro da área não é «ocupar a área»: uma janela de 700×480
        // num monitor grande mantém o tamanho que pediu.
        var c = WindowFit.Calcular(new AreaEcra(0, 0, 1920, 1080), 700, 480);
        Assert.Equal(700, c.Width);
        Assert.Equal(480, c.Height);
        Assert.Equal((1920 - 700) / 2.0, c.Left);
    }

    [Fact]
    public void SemRecentrarMantemAJanelaOndeOUtilizadorADeixou()
    {
        // O gesto real: arrastar a janela para o segundo ecrã. Ela não pode
        // saltar para o centro do ecrã novo.
        var c = WindowFit.Calcular(new AreaEcra(1920, 0, 1280, 720), 960, 640,
            recentrar: false, esquerdaPedida: 2000, topoPedido: 50);

        Assert.Equal(2000, c.Left);
        Assert.Equal(50, c.Top);
        Assert.Equal(960, c.Width);
    }

    [Fact]
    public void SemRecentrarEncostaParaDentroQuandoSaiDoEcra()
    {
        // Maior do que a área e já fora dela: encolhe e encosta à borda, sem
        // recentrar.
        var c = WindowFit.Calcular(new AreaEcra(1920, 0, 1280, 720), 960, 640,
            recentrar: false, esquerdaPedida: 5000, topoPedido: -300);

        Assert.Equal(1920 + 1280 - 960, c.Left);
        Assert.Equal(0, c.Top);
    }

    [Fact]
    public void SemRecentrarNaoMexeQuandoJaCabe()
    {
        var c = WindowFit.Calcular(new AreaEcra(0, 0, 1920, 1080), 960, 640,
            recentrar: false, esquerdaPedida: 10, topoPedido: 10);
        Assert.Equal(10, c.Left);
        Assert.Equal(10, c.Top);
        Assert.Equal(1920, c.MaxWidth);
    }

    [Fact]
    public void MonitorASDireitaCentraNaAreaDele()
    {
        var c = WindowFit.Calcular(new AreaEcra(1920, 0, 1280, 720), 960, 640);
        Assert.Equal(1920 + (1280 - 960) / 2.0, c.Left);
        Assert.Equal((720 - 640) / 2.0, c.Top);
    }

    [Fact]
    public void SemAreaNaoSeCortaNada()
    {
        foreach (var area in new AreaEcra?[]
                 {
                     null,
                     new AreaEcra(0, 0, 0, 0),
                     new AreaEcra(double.NaN, 0, 1920, 1080),
                     new AreaEcra(0, 0, -5, 1080),
                 })
        {
            var c = WindowFit.Calcular(area, 960, 640);
            Assert.Equal(960, c.Width);
            Assert.Equal(640, c.Height);
            Assert.True(double.IsPositiveInfinity(c.MaxWidth));
            Assert.Null(c.Left);
            Assert.Null(c.Top);
        }
    }

    [Theory]
    [InlineData(1920, 96, 1920)]
    [InlineData(1920, 120, 1536)]
    [InlineData(1920, 144, 1280)]
    [InlineData(1366, 144, 910.6666666)]
    public void PixeisParaDipRespeitaAEscala(double pixeis, double dpi, double esperado)
    {
        Assert.Equal(esperado, WindowFit.ParaDip(pixeis, dpi), 4);
    }

    [Fact]
    public void DpiInvalidoNaoDividePorZero()
    {
        Assert.Equal(1920, WindowFit.ParaDip(1920, 0));
        Assert.Equal(1920, WindowFit.ParaDip(1920, -96));
    }
}
