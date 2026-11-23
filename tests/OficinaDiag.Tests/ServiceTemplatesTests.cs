using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Reports;
using OficinaDiag.Templates;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Templates de serviço (Fase E.6): o checklist tpl.&lt;serviço&gt;.* entra no
/// relatório com prefixo próprio, as medições viajam em Value, o propósito de
/// venda do "compra de usado" chega ao Purpose e o serviço sobrevive ao
/// re-scan como qualquer fonte manual.
/// </summary>
public sealed class ServiceTemplatesTests
{
    private static readonly ServiceTemplate Bateria =
        ServiceTemplates.All.Single(t => t.Id == "battery");
    private static readonly ServiceTemplate Usado =
        ServiceTemplates.All.Single(t => t.Id == "used");

    private static string Rotulo(ServiceTemplateItem i) => i.LabelPt;

    [Fact]
    public void AplicarEscreveChecksTplComPrefixoProprio()
    {
        var r = new DeviceReport();
        var marcas = new Dictionary<string, int>
        {
            ["function"] = 1,
            ["liquid"] = 2,
            ["seal"] = 3,
        };

        var aplicadas = ServiceTemplates.Apply(r, Bateria, marcas,
            new Dictionary<string, string>(), Rotulo);

        Assert.Equal(3, aplicadas);
        Assert.Equal("pass", r.Results["tpl.battery.function"].Status);
        Assert.Equal("fail", r.Results["tpl.battery.liquid"].Status);
        Assert.Equal("skipped", r.Results["tpl.battery.seal"].Status);
        // Item sem marca não entra — mesma regra do checklist físico.
        Assert.False(r.Results.ContainsKey("tpl.battery.capacity_before"));
        // O serviço fica marcado para a secção do relatório e para a IA.
        Assert.Equal("battery", r.Results[ServiceTemplates.AppliedKey].Value);
    }

    [Fact]
    public void MedicaoViajaEmValueEDetalheTemrotulo()
    {
        var r = new DeviceReport();
        var marcas = new Dictionary<string, int> { ["capacity_after"] = 1 };
        var medidas = new Dictionary<string, string> { ["capacity_after"] = " 91% " };

        ServiceTemplates.Apply(r, Bateria, marcas, medidas, Rotulo);

        var c = r.Results["tpl.battery.capacity_after"];
        Assert.Equal("pass", c.Status);
        Assert.Equal("91%", c.Value); // trim — o relatório não leva espaços
        Assert.Equal("Capacidade da bateria DEPOIS da troca", c.Detail);
    }

    [Fact]
    public void MedicaoSemTextoFicaSoComEstado()
    {
        var r = new DeviceReport();
        var marcas = new Dictionary<string, int> { ["capacity_after"] = 2 };

        ServiceTemplates.Apply(r, Bateria, marcas, new Dictionary<string, string>(), Rotulo);

        Assert.Equal("fail", r.Results["tpl.battery.capacity_after"].Status);
        Assert.Null(r.Results["tpl.battery.capacity_after"].Value);
    }

    [Fact]
    public void CompraDeUsadoMarcaPropositoDeVenda()
    {
        var r = new DeviceReport { Purpose = "repair" };
        var marcas = new Dictionary<string, int> { ["frame"] = 1, ["screen"] = 2 };

        ServiceTemplates.Apply(r, Usado, marcas, new Dictionary<string, string>(), Rotulo);

        Assert.Equal("sale", r.Purpose);
        Assert.Equal("sale", ServiceTemplates.AiPurposeFor(r));
    }

    [Fact]
    public void TemplateSemPropositoLimpaOSaleAnterior()
    {
        var r = new DeviceReport { Purpose = "sale" };

        ServiceTemplates.Apply(r, Bateria,
            new Dictionary<string, int> { ["function"] = 1 },
            new Dictionary<string, string>(), Rotulo);

        // O template é dono do propósito — um "sale" de outro serviço não
        // pode ficar preso a uma troca de bateria.
        Assert.Null(r.Purpose);
        Assert.Null(ServiceTemplates.AiPurposeFor(r));
    }

    [Fact]
    public void ReaplicarOutroTemplateLimpaOsChecksDoAnterior()
    {
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Bateria,
            new Dictionary<string, int> { ["function"] = 1 },
            new Dictionary<string, string>(), Rotulo);

        ServiceTemplates.Apply(r, Usado,
            new Dictionary<string, int> { ["frame"] = 1 },
            new Dictionary<string, string>(), Rotulo);

        Assert.DoesNotContain(r.Results.Keys, k => k.StartsWith("tpl.battery."));
        Assert.True(r.Results.ContainsKey("tpl.used.frame"));
        Assert.Equal("used", r.Results[ServiceTemplates.AppliedKey].Value);
    }

    [Fact]
    public void PreserveManualMantemOTemplateNoRescan()
    {
        var antigo = new DeviceReport();
        ServiceTemplates.Apply(antigo, Bateria,
            new Dictionary<string, int> { ["function"] = 1, ["capacity_after"] = 1 },
            new Dictionary<string, string> { ["capacity_after"] = "91%" }, Rotulo);

        var novo = new DeviceReport();
        novo.Set("battery.capacity", "pass", "95%"); // scan novo ganha sempre

        novo.PreserveManualFrom(antigo);

        Assert.True(novo.Results.ContainsKey("tpl.battery.function"));
        Assert.True(novo.Results.ContainsKey("tpl.battery.capacity_after"));
        Assert.True(novo.Results.ContainsKey(ServiceTemplates.AppliedKey));
    }

    [Fact]
    public void ChecksTplNaoContamParaOCoberturaDoGrau()
    {
        // Um relatório só com o checklist de serviço não tem base para grau —
        // tpl.* regista o trabalho, não a condição do aparelho.
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Bateria,
            new Dictionary<string, int> { ["function"] = 1, ["liquid"] = 1 },
            new Dictionary<string, string>(), Rotulo);

        var g = Grader.Compute(r);

        Assert.Equal("—", g.Grade);
    }

    [Fact]
    public void HtmlTemSecaoDoServicoComItensVerificados()
    {
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Bateria,
            new Dictionary<string, int> { ["function"] = 1, ["seal"] = 1, ["liquid"] = 2 },
            new Dictionary<string, string>(), Rotulo);
        // capacity_after sem marca não entra — a secção não a inventa.
        r.Set("tpl.battery.capacity_after", "pass", "91%", "Capacidade da bateria DEPOIS da troca");

        var html = ReportBuilder.ToHtml(r);

        Assert.Contains("SERVIÇO — Troca de bateria", html);
        // HtmlEncode escapa os acentos — os asserts usam fragmentos sem eles.
        Assert.Contains("Carga + arranque + Bluetooth OK", html);
        Assert.Contains("Selos/adesivos repostos", html);
        Assert.Contains("91%", html);
    }

    [Fact]
    public void VistaClienteMostraRotulosDoServicoSemChavesTecnicas()
    {
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Usado,
            new Dictionary<string, int> { ["frame"] = 1, ["screen"] = 2 },
            new Dictionary<string, string>(), Rotulo);

        var html = ReportBuilder.ToClientHtml(r);

        Assert.Contains("Moldura: mossas, trincas, parafusos", html);
        Assert.DoesNotContain("tpl.used.frame", html);
        // service.template é meta-dado — não entra na tabela do cliente.
        Assert.DoesNotContain("service.template", html);
    }

    [Fact]
    public void CloudJsonLevaOTemplateERespeitaOPromocoDeVenda()
    {
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Usado,
            new Dictionary<string, int> { ["frame"] = 1 },
            new Dictionary<string, string>(), Rotulo);

        var json = ReportBuilder.ToCloudJson(r);

        Assert.Contains("tpl.used.frame", json);
        Assert.Contains("service.template", json);
        // Purpose "sale" do template viaja como purpose do relatório IA.
        Assert.Contains("\"purpose\": \"sale\"", json);
    }

    [Fact]
    public void RotulosEDeteccaoDoCatalogo()
    {
        Assert.Equal("Troca de bateria", ServiceTemplates.Find("battery")?.LabelPt);
        Assert.Null(ServiceTemplates.Find("inexistente"));
        Assert.Equal("tpl.screen.function", ServiceTemplates.CheckKey("screen", "function"));
        Assert.Equal("Carga + arranque + Bluetooth OK após a troca",
            ServiceTemplates.LabelPtFor("tpl.battery.function"));
        Assert.Null(ServiceTemplates.LabelPtFor("checklist.frame"));
        Assert.Null(ServiceTemplates.LabelPtFor("tpl.battery.item_fantasma"));
    }

    [Fact]
    public void ManutencaoAbreOFfluxoExistenteSemChecklistProprio()
    {
        var m = ServiceTemplates.All.Single(t => t.Id == "maintenance");
        Assert.True(m.OpensMaintenanceDialog);
        Assert.Empty(m.Items); // não duplica o maint.* — a limpeza medida é o serviço
        Assert.Equal("maintenance", m.AiPurpose);
    }
}
