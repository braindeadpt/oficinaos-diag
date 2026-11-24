using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Maintenance;
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

    [Fact]
    public void HasManualDataDetetaOTabalhoQueOScanNaoReproduz()
    {
        // Base do aviso "dados sem aparelho" no scan: um relatório vazio ou
        // só com dados de scan substitui-se sem perguntar; checklist,
        // serviço ou notas pedem confirmação antes de copiar.
        Assert.False(new DeviceReport().HasManualData());

        var soScan = new DeviceReport();
        soScan.Set("battery.capacity", "pass", "95%");
        Assert.False(soScan.HasManualData());

        var comServico = new DeviceReport();
        ServiceTemplates.Apply(comServico, Bateria,
            new Dictionary<string, int> { ["function"] = 1 },
            new Dictionary<string, string>(), Rotulo);
        Assert.True(comServico.HasManualData());

        var soNotas = new DeviceReport { Notes = "cliente espera resposta" };
        Assert.True(soNotas.HasManualData());
    }

    [Fact]
    public void LimpezaGravadaSubstituiOServicoDeTemplateAnterior()
    {
        // Otimização escolhida no SERVIÇO depois de "compra de usado": a
        // limpeza passa a ser o serviço — tpl.used.* e o purpose=sale não
        // podem ficar presos ao relatório da otimização.
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Usado,
            new Dictionary<string, int> { ["frame"] = 1 },
            new Dictionary<string, string>(), Rotulo);
        r.Maintenance = new MaintenanceReport();

        var substituiu = ServiceTemplates.ApplyMaintenance(r);

        Assert.True(substituiu);
        Assert.DoesNotContain(r.Results.Keys, k => k.StartsWith("tpl."));
        Assert.Null(r.Purpose);
        // A limpeza medida em si não é tocada — é ela que vira o serviço.
        Assert.NotNull(r.Maintenance);
    }

    [Fact]
    public void LimpezaGravadaMarcaOServicoComoOtimizacao()
    {
        // O marcador service.template=maintenance faz o seletor SERVIÇO
        // reabrir na otimização — sem ele a limpeza gravada era
        // indistinguível de uma limpeza complementar (botão LIMPEZA).
        var r = new DeviceReport();
        r.Maintenance = new MaintenanceReport();

        Assert.False(ServiceTemplates.ApplyMaintenance(r)); // nada a substituir

        Assert.Equal(ServiceTemplates.MaintenanceId,
            r.Results[ServiceTemplates.AppliedKey].Value);
        Assert.Equal("maintenance", ServiceTemplates.AiPurposeFor(r));
        // Reaplicar a mesma otimização não conta como substituição.
        Assert.False(ServiceTemplates.ApplyMaintenance(r));
    }

    [Fact]
    public void LimpezaSemTemplatePreservaPropositoManual()
    {
        // O SaleBox do checklist físico não é template — uma limpeza que só
        // complementa o atendimento não pode apagar uma escolha manual.
        var r = new DeviceReport { Purpose = "sale" };

        Assert.False(ServiceTemplates.ApplyMaintenance(r));
        Assert.Equal("sale", r.Purpose);
        // O serviço do atendimento fica marcado na mesma — o Purpose manual
        // é enquadramento de venda, não o serviço executado.
        Assert.Equal(ServiceTemplates.MaintenanceId,
            r.Results[ServiceTemplates.AppliedKey].Value);
    }

    [Fact]
    public void HtmlDaOtimizacaoNaoEmiteTabelaDeChecklistVazia()
    {
        // service.template=maintenance não tem itens tpl.* — a secção
        // SERVIÇO aponta para a secção LIMPEZA em vez de uma tabela vazia.
        var r = new DeviceReport();
        r.Maintenance = new MaintenanceReport();
        ServiceTemplates.ApplyMaintenance(r);

        var html = ReportBuilder.ToHtml(r);

        // O título leva acentos — o HtmlEncode escapa-os; os fragmentos
        // asserts são ASCII como nos outros testes de HTML.
        Assert.Contains("SERVIÇO — ", html);
        Assert.Contains("limpeza e otimização medidas", html);
        Assert.Contains("LIMPEZA E OTIMIZAÇÃO", html);

        // A nota sozinha não chega — uma tabela vazia podia aparecer na
        // mesma secção. Entre os dois títulos não pode haver <table>.
        var ini = html.IndexOf("SERVIÇO — ", StringComparison.Ordinal);
        var fim = html.IndexOf("LIMPEZA E OTIMIZAÇÃO", StringComparison.Ordinal);
        Assert.True(ini >= 0 && fim > ini);
        Assert.DoesNotContain("<table", html[ini..fim], StringComparison.Ordinal);
    }

    [Fact]
    public void MedicoesExigidasDistingemOrigemEPlataforma()
    {
        // O aviso "em falta" aponta a ação que produz a medição: test.* vem
        // do ECRÃ+TOQUE, não do scan; grade.* deriva-se do scan+checklist.
        Assert.Equal("test", ServiceTemplates.RequiredSource("test.touch"));
        Assert.Equal("test", ServiceTemplates.RequiredSource("test.screen"));
        Assert.Equal("auto", ServiceTemplates.RequiredSource("grade.overall"));
        Assert.Equal("scan", ServiceTemplates.RequiredSource("battery.capacity"));
        Assert.Equal("scan", ServiceTemplates.RequiredSource("battery.health"));

        // battery.health só existe no coletor Android — no iOS seria um
        // "em falta" impossível de resolver.
        Assert.False(ServiceTemplates.RequiredApplies("battery.health", "ios"));
        Assert.True(ServiceTemplates.RequiredApplies("battery.health", "android"));
        Assert.True(ServiceTemplates.RequiredApplies("battery.capacity", "ios"));
        Assert.True(ServiceTemplates.RequiredApplies("battery.capacity", null));
    }

    [Fact]
    public void HtmlTecnicoNaoRepeteChecklistNaGrelhaGeral()
    {
        // A secção SERVIÇO já lista os tpl.* por ordem do template — a
        // tabela geral não os repete com as chaves técnicas.
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Bateria,
            new Dictionary<string, int> { ["function"] = 1 },
            new Dictionary<string, string>(), Rotulo);
        r.Set("battery.capacity", "pass", "95%");

        var html = ReportBuilder.ToHtml(r);

        Assert.Contains("SERVIÇO — Troca de bateria", html);
        Assert.DoesNotContain("tpl.battery.function", html);
        Assert.DoesNotContain("service.template", html);
        // Os checks de scan continuam na grelha geral.
        Assert.Contains("battery.capacity", html);
    }

    [Fact]
    public void HtmlTecnicoMostraTplOrfaosSemSecaoDeServico()
    {
        // tpl.* sem service.template não podem desaparecer do HTML — o
        // Apply nunca produz este estado, mas um arrasto manual pode.
        var r = new DeviceReport();
        ServiceTemplates.Apply(r, Bateria,
            new Dictionary<string, int> { ["function"] = 1 },
            new Dictionary<string, string>(), Rotulo);
        r.Remove(ServiceTemplates.AppliedKey);

        var html = ReportBuilder.ToHtml(r);

        Assert.Contains("tpl.battery.function", html);
        Assert.DoesNotContain("SERVIÇO —", html);
    }
}
