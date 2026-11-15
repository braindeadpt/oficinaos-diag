using OficinaDiag.Log;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// O handler global do `App` engolia a exceção e seguia: o botão ficava a não
/// fazer nada e ninguém sabia. A política destes testes é a que põe o aviso e
/// desliga o botão à terceira falha, com o log a parar de crescer.
/// </summary>
public sealed class FailureGuardTests
{
    [Fact]
    public void AsDuasPrimeirasFalhasNaoAvisamNemDesligam()
    {
        var guarda = new FailureGuard();

        var primeira = guarda.Registar("ScanButton");
        var segunda = guarda.Registar("ScanButton");

        Assert.Equal(1, primeira.Total);
        Assert.Equal(2, segunda.Total);
        Assert.False(primeira.Avisar || segunda.Avisar);
        Assert.False(primeira.Desactivado || segunda.Desactivado);
    }

    [Fact]
    public void ATerceiraFalhaAvisaEDesliga()
    {
        var guarda = new FailureGuard();
        guarda.Registar("ScanButton");
        guarda.Registar("ScanButton");

        var terceira = guarda.Registar("ScanButton");

        Assert.Equal(3, terceira.Total);
        Assert.True(terceira.Avisar);
        Assert.True(terceira.Desactivado);
        Assert.True(terceira.EscreverLog);
    }

    [Fact]
    public void ApartirDaTerceiraOLogFicaEmDedupe()
    {
        var guarda = new FailureGuard();
        for (var i = 0; i < FailureGuard.Limite; i++) guarda.Registar("ScanButton");

        var quarta = guarda.Registar("ScanButton");
        var quinta = guarda.Registar("ScanButton");

        Assert.False(quarta.EscreverLog);
        Assert.False(quinta.EscreverLog);
        Assert.False(quarta.Avisar); // o aviso sai uma vez, não a cada clique
        Assert.True(quarta.Desactivado);
        Assert.Equal(5, quinta.Total);
    }

    [Fact]
    public void UmSucessoRecomecaAContagem()
    {
        var guarda = new FailureGuard();
        guarda.Registar("ScanButton");
        guarda.Registar("ScanButton");

        guarda.Sucesso("ScanButton");

        Assert.Equal(0, guarda.FalhasDe("ScanButton"));
        var depois = guarda.Registar("ScanButton");
        Assert.Equal(1, depois.Total);
        Assert.False(depois.Avisar); // duas falhas antigas não contam para o aviso
    }

    [Theory]
    [InlineData("ScanButton", 0, "ScanButton")]
    [InlineData("ScanButton", 4999, "ScanButton")]
    [InlineData("ScanButton", 5001, null)] // chegou tarde: é de outra coisa
    [InlineData("ScanButton", -1, null)]
    [InlineData(null, 10, null)]
    [InlineData("", 10, null)]
    public void SoSeCulpaOBotaoSeAFalhaChegouPertoDoGesto(string? acao, long ms, string? esperado)
    {
        Assert.Equal(esperado, FailureGuard.AcaoReconhecida(acao, ms));
    }

    [Fact]
    public void AcoesDiferentesNaoSeContaminam()
    {
        var guarda = new FailureGuard();
        guarda.Registar("ScanButton");
        guarda.Registar("ScanButton");
        guarda.Registar("ScanButton");

        var outra = guarda.Registar("ExportButton");

        Assert.Equal(1, outra.Total);
        Assert.False(outra.Avisar);
        Assert.False(outra.Desactivado);
    }
}
