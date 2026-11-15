using OficinaDiag.Grading;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Regressão do checklist morto em relatórios novos: `SetState` lia
/// `_state[key]` com o indexer e lançava `KeyNotFoundException` no primeiro
/// item marcado, engolida pelo handler global da UI — o técnico marcava e
/// nada acontecia, e o APPLY gravava zero linhas.
/// </summary>
public sealed class ChecklistMarksTests
{
    [Fact]
    public void MarcarItemSemMarcaNaoRebenta()
    {
        var marcas = new Dictionary<string, int>();

        var marca = ChecklistMarks.Toggle(marcas, "checklist.frame", 1);

        Assert.Equal(1, marca);
        Assert.Equal(1, marcas["checklist.frame"]);
    }

    [Fact]
    public void PremirOMesmoBotaoOutraVezDesmarca()
    {
        var marcas = new Dictionary<string, int>();
        ChecklistMarks.Toggle(marcas, "checklist.frame", 2);

        var marca = ChecklistMarks.Toggle(marcas, "checklist.frame", 2);

        Assert.Equal(0, marca);
        Assert.Equal(0, marcas["checklist.frame"]);
    }

    [Fact]
    public void PremirOutroBotaoTrocaAMarca()
    {
        var marcas = new Dictionary<string, int>();
        ChecklistMarks.Toggle(marcas, "checklist.frame", 1);

        var marca = ChecklistMarks.Toggle(marcas, "checklist.frame", 3);

        Assert.Equal(3, marca);
        Assert.Equal(3, marcas["checklist.frame"]);
    }

    [Fact]
    public void ItensDiferentesNaoSeAtrapalham()
    {
        var marcas = new Dictionary<string, int>();

        ChecklistMarks.Toggle(marcas, "checklist.frame", 1);
        ChecklistMarks.Toggle(marcas, "checklist.sim_tray", 3);

        Assert.Equal(1, marcas["checklist.frame"]);
        Assert.Equal(3, marcas["checklist.sim_tray"]);
    }

    [Theory]
    [InlineData(1, "pass")]
    [InlineData(2, "fail")]
    [InlineData(3, "skipped")]
    public void CadaMarcaTemOEstadoDoRelatorio(int marca, string esperado)
    {
        Assert.Equal(esperado, ChecklistMarks.Status(marca));
    }

    [Fact]
    public void AplicarEscreveAsLinhasMarcadasNoRelatorio()
    {
        var relatorio = new Devices.DeviceReport();
        var marcas = new Dictionary<string, int>
        {
            ["checklist.frame"] = 1,
            ["checklist.backglass"] = 3,
        };

        var aplicadas = ChecklistMarks.Apply(relatorio, marcas, item => item.LabelPt);

        Assert.Equal(2, aplicadas);
        Assert.Equal("pass", relatorio.Results["checklist.frame"].Status);
        Assert.Equal("skipped", relatorio.Results["checklist.backglass"].Status);
        // Só as marcadas entram: um item por olhar não vira «não testado».
        Assert.False(relatorio.Results.ContainsKey("checklist.buttons"));
    }

    [Fact]
    public void SemMarcasNaoEscreveNada()
    {
        var relatorio = new Devices.DeviceReport();

        var aplicadas = ChecklistMarks.Apply(relatorio, new Dictionary<string, int>(), item => item.LabelPt);

        Assert.Equal(0, aplicadas);
        Assert.Empty(relatorio.Results);
    }
}
