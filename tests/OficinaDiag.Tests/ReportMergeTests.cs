using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Re-scan do mesmo aparelho: o relatório novo é a base (campos de hardware
/// reais) mas herda só as fontes manuais — checklist, teste no ecrã e
/// bancada. Resultados USB/derivados são produzidos de novo; copiá-los
/// falseava o re-scan (aviso de root obsoleto mantinha o grau limitado).
/// </summary>
public class ReportMergeTests
{
    private static DeviceReport RelatorioAntigo()
    {
        var r = new DeviceReport();
        // Fontes manuais — têm de sobreviver ao re-scan
        r.Set("checklist.body", "fail", "ecrã rachado");
        r.Set("test.touch", "pass", "9/9");
        r.Set("sensor.multitouch", "pass", "5 pontos");
        r.Set("power.charge", "warn", "450 mA");
        r.Set("usb.stability", "warn", "2 desconexão(ões)");
        // Perfil de desempenho — medição manual como a bancada, tem de
        // sobreviver ao re-scan para o antes/depois continuar comparável.
        r.Set("perf.session", "info", "168 amostras · 336s");
        r.Set("perf.thermal", "info", "SoC pico 52.1°C");
        r.Set("perf.usb", "pass", "ligação estável");
        // Produzidos pelo scan/derivados — NÃO podem ser arrastados
        r.Set("security.root", "warn", "su encontrado");
        r.Set("battery.swap", "info", "célula diferente");
        r.Set("battery.health", "warn", "74%");
        r.Notes = "cliente quer orçamento";
        r.Purpose = "sale";
        r.PhotoPaths.Add(@"C:\fotos\ecra.jpg");
        return r;
    }

    [Fact]
    public void PreserveManual_MantemFontesManuaisEAnexos()
    {
        var antigo = RelatorioAntigo();
        var novo = new DeviceReport();
        novo.Set("battery.health", "pass", "91%"); // recolha nova ganha sempre

        novo.PreserveManualFrom(antigo);

        Assert.True(novo.Results.ContainsKey("checklist.body"));
        Assert.True(novo.Results.ContainsKey("test.touch"));
        Assert.True(novo.Results.ContainsKey("sensor.multitouch"));
        Assert.True(novo.Results.ContainsKey("power.charge"));
        Assert.True(novo.Results.ContainsKey("usb.stability"));
        Assert.True(novo.Results.ContainsKey("perf.session"));
        Assert.True(novo.Results.ContainsKey("perf.thermal"));
        Assert.True(novo.Results.ContainsKey("perf.usb"));
        Assert.Equal("cliente quer orçamento", novo.Notes);
        Assert.Equal("sale", novo.Purpose);
        Assert.Contains(@"C:\fotos\ecra.jpg", novo.PhotoPaths);
    }

    [Fact]
    public void PreserveManual_NaoArrastaResultadosDoScan()
    {
        var antigo = RelatorioAntigo();
        var novo = new DeviceReport();
        novo.Set("battery.health", "pass", "91%");

        novo.PreserveManualFrom(antigo);

        // O aparelho restaurado não volta a mostrar root nem a troca de
        // célula derivada — e a leitura nova de battery.health prevalece.
        Assert.False(novo.Results.ContainsKey("security.root"));
        Assert.False(novo.Results.ContainsKey("battery.swap"));
        Assert.Equal("91%", novo.Results["battery.health"].Value);
    }

    [Fact]
    public void PreserveManual_NaoSobrepoeChaveNovaNemAnexosProprios()
    {
        var antigo = RelatorioAntigo();
        var novo = new DeviceReport();
        novo.Set("test.touch", "fail", "3/9"); // teste refeito nesta sessão
        novo.Notes = "nota nova";
        novo.PhotoPaths.Add(@"C:\fotos\ecra.jpg");

        novo.PreserveManualFrom(antigo);

        Assert.Equal("3/9", novo.Results["test.touch"].Value);
        Assert.Equal("nota nova", novo.Notes);
        Assert.Single(novo.PhotoPaths); // sem duplicados
    }
}
