using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Health;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Health score 0–100 — índice de marketing/UX complementar ao grau A–D.
/// As regras que estes testes fixam: dados em falta nunca penalizam (o peso
/// redistribui), cada ponto vem de um check real e o iPhone — que expõe
/// menos — não sai lesionado por comparável.
/// </summary>
public sealed class HealthScoreTests
{
    private static DeviceReport Report() => new() { Platform = "android" };

    private static void Set(DeviceReport r, string key, string status, string? value = null) =>
        r.Set(key, status, value);

    /// <summary>Retrato de um Android completo e saudável — o "100" do teste.</summary>
    private static DeviceReport Perfeito()
    {
        var r = Report();
        // bateria — capacidade real 100% do design, saúde boa, temp normal
        Set(r, "battery.capacity", "pass", "100%");
        Set(r, "battery.health", "pass", "good");
        Set(r, "battery.temperature", "info", "33.5°C");
        // armazenamento — 30% usado = 70% livre
        Set(r, "storage", "info", "30% usado");
        // segurança — tudo dentro do esperado
        Set(r, "security.mdm", "pass", "sem gestão remota");
        Set(r, "security.accounts", "pass", "sem contas");
        Set(r, "security.bootloader", "pass", "green");
        Set(r, "security.warranty", "pass", "intacto");
        Set(r, "security.oemunlock", "pass", "bootloader bloqueado");
        Set(r, "security.selinux", "pass", "Enforcing");
        Set(r, "security.build", "pass", "release-keys");
        Set(r, "security.crypto", "pass", "encrypted");
        // security.root ausente = sem root encontrado — nunca se escreve "pass".
        // estabilidade — sem panics; crashes de apps não existem no fixture
        Set(r, "logs.panics", "pass", "0");
        // teste no aparelho — tudo ok
        Set(r, "test.touch", "pass", "60/60 zonas");
        Set(r, "test.screen", "pass", "cores ok");
        Set(r, "test.edges", "pass", "28/28 células de borda");
        Set(r, "sensor.multitouch", "pass", "5 dedos simultâneos");
        Set(r, "sensor.speaker", "pass", "tom ouvido");
        Set(r, "sensor.earpiece", "pass", "tom grave ouvido");
        Set(r, "sensor.mic", "pass", "sinal detetado");
        Set(r, "sensor.vibrate", "pass", "vibrou");
        return r;
    }

    /// <summary>Retrato de um telemóvel mau — bateria morta, segurança aberta,
    /// panics, ecrã a falhar e líquido no checklist.</summary>
    private static DeviceReport Mau()
    {
        var r = Report();
        Set(r, "battery.capacity", "fail", "45%");
        Set(r, "battery.health", "warn", "dead");
        Set(r, "battery.cycles", "warn", "1200");
        Set(r, "battery.temperature", "warn", "41.2°C");
        Set(r, "storage", "info", "97% usado");
        Set(r, "storage.wear", "warn", "classe 10/11");
        Set(r, "security.mdm", "warn", "gestão remota ativa");
        Set(r, "security.root", "warn", "su encontrado");
        Set(r, "security.accounts", "warn", "3 conta(s)");
        Set(r, "security.bootloader", "warn", "orange");
        Set(r, "security.warranty", "fail", "Knox queimado (0x1)");
        Set(r, "logs.panics", "warn", "2");
        Set(r, "logs.crashes", "warn", "1 app crashes · 4 ANRs · 9 de sistema");
        Set(r, "logs.thermal", "warn", "thermalmonitord");
        Set(r, "test.touch", "fail", "12/60 zonas");
        Set(r, "sensor.speaker", "fail", "tom NÃO ouvido");
        Set(r, "checklist.liquid", "fail", "defeito");
        return r;
    }

    [Fact]
    public void RelatorioVazio_SemScore()
    {
        var res = HealthScore.Compute(new DeviceReport());
        Assert.Null(res.Score);
        Assert.Equal(HealthBand.None, res.Band);
        Assert.All(res.Categories, c => Assert.Null(c.Score));
    }

    [Fact]
    public void AparelhoPerfeito_CemPontos()
    {
        var res = HealthScore.Compute(Perfeito());
        Assert.Equal(100, res.Score);
        Assert.Equal(HealthBand.Healthy, res.Band);
        Assert.All(res.Categories, c => Assert.Equal(100, c.Score));
    }

    [Fact]
    public void AparelhoMau_ScoreBaixo()
    {
        var res = HealthScore.Compute(Mau());
        Assert.NotNull(res.Score);
        Assert.True(res.Score < 45, $"esperava score baixo, veio {res.Score}");
        Assert.Equal(HealthBand.Concerning, res.Band);
    }

    [Fact]
    public void DadosParciais_NaoPenalizamCategoriasEmFalta()
    {
        // Só a bateria foi medida: a categoria pontua no breakdown, mas o
        // score global exige cobertura mínima — um check solto não é
        // «saúde do aparelho» e não pode sair com nota.
        var r = Report();
        Set(r, "battery.capacity", "pass", "80%");
        var res = HealthScore.Compute(r);

        Assert.Null(res.Score);
        Assert.Contains("insuficientes", res.Verdict);
        var battery = Assert.Single(res.Categories, c => c.Score is not null);
        Assert.Equal("battery", battery.Key);
        Assert.Equal(80, battery.Score);
        Assert.Equal(5, res.Categories.Count(c => c.Score is null));

        // Com uma segunda categoria o peso redistribui-se pelas medidas
        // que existem — bateria 80 (peso 25) + segurança 100 (peso 20)
        // dá 89, não uma média diluída pelas cinco em falta.
        Set(r, "security.mdm", "pass", "sem gestão remota");
        var res2 = HealthScore.Compute(r);
        Assert.Equal(89, res2.Score);
    }

    [Fact]
    public void CoberturaInsuficiente_ApplyNaoGravaBreakdown()
    {
        // Bateria sozinha é cobertura insuficiente: o relatório não pode
        // mostrar "bateria 80/100" sem um total que a sustente — as
        // entradas health.<cat> também ficam de fora.
        var r = Report();
        Set(r, "battery.capacity", "pass", "80%");
        HealthScore.Apply(r);
        Assert.True(r.Results.TryGetValue("health.score", out var hs));
        Assert.Equal("skipped", hs!.Status);
        Assert.DoesNotContain(r.Results.Keys,
            k => k.StartsWith("health.", StringComparison.Ordinal) && k != "health.score");
    }

    [Fact]
    public void IosComMenosDados_NaoPerdePontos()
    {
        // Retrato realista de iPhone: sem root/MDM consultável (skipped), sem
        // auditoria Pro, sem teste on-device, crashes de apps = info.
        var r = new DeviceReport { Platform = "ios" };
        Set(r, "battery.capacity", "pass", "92%");
        Set(r, "battery.cycles", "info", "310");
        Set(r, "battery.temp", "info", "31.2 °C");
        Set(r, "storage", "info", "60 GB livres de 128 GB");
        Set(r, "security.mdm", "skipped", "indeterminado");
        Set(r, "security.findmy", "skipped", "indeterminado");
        Set(r, "device.activation", "pass", "Activated");
        Set(r, "device.sim", "info", "kCTSIMSupportSIMStatusReady");
        Set(r, "logs.panics", "pass", "0");
        Set(r, "logs.crashes", "info", "2 apps · 1 jetsam");

        var res = HealthScore.Compute(r);
        Assert.NotNull(res.Score);
        Assert.True(res.Score >= 90, $"iOS saudável devia ficar ≥90, veio {res.Score}");
        Assert.Equal(HealthBand.Healthy, res.Band);
        // O que não existe no iOS não pode aparecer como penalização.
        Assert.Null(res.Categories.Single(c => c.Key == "test").Score);
        Assert.Contains("bateria", res.Verdict);
    }

    [Fact]
    public void BateriaCapacidadeBaixa_DerrubaCategoria()
    {
        var r = Report();
        Set(r, "battery.capacity", "fail", "55%");
        var res = HealthScore.Compute(r);
        var battery = res.Categories.Single(c => c.Key == "battery");
        Assert.Equal(55, battery.Score);
        // Um check solto não chega para score global — cobertura mínima.
        Assert.Null(res.Score);

        // Com uma segunda categoria o total existe e a bateria má
        // continua a puxar a média: (25×55 + 20×100) / 45 = 75.
        Set(r, "security.mdm", "pass", "sem gestão remota");
        var res2 = HealthScore.Compute(r);
        Assert.Equal(75, res2.Score);
    }

    [Fact]
    public void MdmWarn_PesaMaisQueUmWarnNormal()
    {
        // Mesma estrutura, duas variantes: mdm warn (peso 2, score 35) vs
        // root warn (peso 1, score 50) — o bloqueio de gestão tem de doer mais.
        DeviceReport Com(params (string Key, string Status, string? Val)[] checks)
        {
            var r = Report();
            foreach (var (k, s, v) in checks) Set(r, k, s, v);
            return r;
        }
        var baseChecks = new (string, string, string?)[]
        {
            ("security.bootloader", "pass", "green"),
            ("security.accounts", "pass", "sem contas"),
        };
        var comMdm = Com([.. baseChecks, ("security.mdm", "warn", "gestão remota ativa")]);
        var comRoot = Com([.. baseChecks, ("security.root", "warn", "su encontrado")]);

        var scoreMdm = HealthScore.Compute(comMdm).Categories.Single(c => c.Key == "security").Score;
        var scoreRoot = HealthScore.Compute(comRoot).Categories.Single(c => c.Key == "security").Score;
        Assert.True(scoreMdm < scoreRoot,
            $"MDM ({scoreMdm}) devia doer mais que root ({scoreRoot})");
    }

    [Fact]
    public void SkippedNuncaPontua()
    {
        var r = Report();
        Set(r, "battery.capacity", "pass", "90%");
        Set(r, "security.mdm", "skipped", "indeterminado");
        Set(r, "test.touch", "skipped", null);
        var res = HealthScore.Compute(r);
        // mdm/test.touch skipped ficam fora — só battery + restantes security
        // com dados pontuam.
        Assert.DoesNotContain(res.Categories, c => c.Key == "test" && c.Score is not null);
    }

    [Fact]
    public void CrashesDeAppsInfo_NaoDescontam()
    {
        // logs.crashes info = "crashes de apps = software, informativo" —
        // penalizar aqui era descontar o software do cliente à saúde do
        // aparelho. Só warn (falhas de sistema repetidas) entra.
        var r = Report();
        Set(r, "logs.crashes", "info", "3 app crashes · 0 ANRs · 0 de sistema");
        var res = HealthScore.Compute(r);
        Assert.Null(res.Score); // nada mais medido → sem score

        Set(r, "logs.crashes", "warn", "0 app crashes · 0 ANRs · 9 de sistema");
        var res2 = HealthScore.Compute(r);
        Assert.Equal(50, res2.Categories.Single(c => c.Key == "stability").Score);
    }

    [Fact]
    public void Apply_GravaScoreEBreakdownNoRelatorio()
    {
        var r = Perfeito();
        var res = HealthScore.Apply(r);
        Assert.Equal(100, res.Score);

        Assert.True(r.Results.TryGetValue("health.score", out var hs));
        Assert.Equal("pass", hs!.Status);
        Assert.Equal("100/100", hs.Value);
        // health.score + 6 categorias medidas no retrato perfeito.
        Assert.Equal(7, r.Results.Count(kv => kv.Key.StartsWith("health.", StringComparison.Ordinal)));
        Assert.True(r.Results.ContainsKey("health.battery"));
        Assert.True(r.Results.ContainsKey("health.test"));
    }

    [Fact]
    public void ApplySemDados_GravaSkippedNaoZero()
    {
        // Relatório vazio: "0/100" sugeria telemóvel morto; "—" skipped é honesto.
        var r = new DeviceReport();
        HealthScore.Apply(r);
        Assert.True(r.Results.TryGetValue("health.score", out var hs));
        Assert.Equal("skipped", hs!.Status);
        Assert.Equal("—", hs.Value);
        Assert.False(r.Results.ContainsKey("health.battery"));
    }

    [Fact]
    public void Grader_IgnoraChecksDeSaudeNaCoverage()
    {
        // Regressão real: health.score é escrito em TODO o relatório (mesmo
        // vazio, como "skipped"). Sem a exclusão no coverage do Grader, um
        // relatório sem dados nenhuns passava de "—" para grau A à segunda
        // passada do ApplyGrade.
        var r = new DeviceReport { Platform = "android" };
        HealthScore.Apply(r);
        Assert.True(r.Results.ContainsKey("health.score"));

        var g = Grader.Compute(r);
        Assert.Equal("—", g.Grade);
    }

    [Fact]
    public void Apply_CategoriaSemDados_RemoveEntradaAntiga()
    {
        // Regressão Devin Review: marcar o único teste como «não testado»
        // depois de já ter pontuado deixava health.test=100/100 no JSON/HTML
        // exportado, embora a categoria já não entrasse no total. A bateria
        // entra para a cobertura mínima (25+15=40) — sem ela o score global
        // nem existiria e nenhuma entrada health.<cat> seria gravada.
        var r = Report();
        Set(r, "battery.capacity", "pass", "90%");
        Set(r, "test.touch", "pass", "60/60 zonas");
        HealthScore.Apply(r);
        Assert.True(r.Results.ContainsKey("health.test"));

        Set(r, "test.touch", "skipped", null);
        HealthScore.Apply(r);

        Assert.False(r.Results.ContainsKey("health.test"));
        // O total fica — sem dados nenhuns é "skipped", nunca um número velho.
        Assert.True(r.Results.TryGetValue("health.score", out var hs));
        Assert.Equal("skipped", hs!.Status);
    }

    [Fact]
    public void SkippedComValor_NuncaPontua()
    {
        // Regressão Devin Review: os scorers que leem Value (Percent,
        // BatteryHealth, StorageFree) interpretavam-no mesmo com status
        // skipped — a exclusão tem de ser uniforme. É exatamente o caso do
        // iPhone sem espaço livre exposto (storage skipped).
        var r = Report();
        Set(r, "battery.capacity", "skipped", "95%");
        Set(r, "battery.health", "skipped", "good");
        Set(r, "storage", "skipped", "indisponível");
        var res = HealthScore.Compute(r);
        Assert.Null(res.Score);
        Assert.All(res.Categories, c => Assert.Null(c.Score));
    }

    [Fact]
    public void StorageWearInfo_ForaDoCalculo()
    {
        // storage.wear info = classe abaixo do limiar — contexto como os
        // outros info: não desconta (antes dava 85 e todo o Android
        // saudável ficava com armazenamento a 95).
        var r = Report();
        Set(r, "storage", "info", "30% usado"); // 70% livre → 100
        Set(r, "storage.wear", "info", "classe 3/11");
        var res = HealthScore.Compute(r);
        Assert.Equal(100, res.Categories.Single(c => c.Key == "storage").Score);

        // warn = perto do fim da vida NAND — esse desconta mesmo.
        Set(r, "storage.wear", "warn", "classe 10/11");
        var res2 = HealthScore.Compute(r);
        Assert.Equal(80, res2.Categories.Single(c => c.Key == "storage").Score);
    }

    [Fact]
    public void CompareRow_ScorePerdidoOuNovo_NuncaEMelhoria()
    {
        // Regressão Devin Review: score anterior «Razoável» (warn) que
        // virava skipped por cobertura insuficiente caía na lógica de
        // severidade do CompareWindow e pintava VERDE — perda de dados
        // não é melhoria. E o contrário também não é queda do aparelho.
        var old = new CheckResult { Status = "warn", Value = "62/100" };
        var gone = new CheckResult { Status = "skipped", Value = "—" };
        Assert.Equal(HealthRowTrend.Neutral, HealthScore.CompareRow(old, gone));
        Assert.Equal(HealthRowTrend.Neutral, HealthScore.CompareRow(gone, old));
        Assert.Equal(HealthRowTrend.Neutral, HealthScore.CompareRow(null, old));
        Assert.Equal(HealthRowTrend.Neutral, HealthScore.CompareRow(old, null));
    }

    [Fact]
    public void CompareRow_DescidaEMelhoriaSegueOCobertura()
    {
        static CheckResult S(string v) => new() { Status = "pass", Value = v };
        Assert.Equal(HealthRowTrend.Declined,
            HealthScore.CompareRow(S("96/100"), S("78/100")));
        Assert.Equal(HealthRowTrend.Improved,
            HealthScore.CompareRow(S("78/100"), S("96/100"), 5, 5));
        Assert.Equal(HealthRowTrend.Neutral,
            HealthScore.CompareRow(S("87/100"), S("87/100")));
        // Subida com menos categorias medidas = não confirmada.
        Assert.Equal(HealthRowTrend.Unconfirmed,
            HealthScore.CompareRow(S("62/100"), S("95/100"), 5, 3));
        Assert.Equal(HealthRowTrend.Improved,
            HealthScore.CompareRow(S("62/100"), S("95/100"), 3, 5));
        // Linhas de categoria (sem cobertura): a subida é real.
        Assert.Equal(HealthRowTrend.Improved,
            HealthScore.CompareRow(S("40/100"), S("90/100")));
    }

    [Fact]
    public void StorageIOS_LeGBLivres()
    {
        var r = Report();
        Set(r, "storage", "info", "100 GB livres de 128 GB"); // ~78% livre
        var res = HealthScore.Compute(r);
        Assert.Equal(100, res.Categories.Single(c => c.Key == "storage").Score);

        Set(r, "storage", "info", "4 GB livres de 128 GB"); // ~3% livre → crítico
        var res2 = HealthScore.Compute(r);
        Assert.Equal(20, res2.Categories.Single(c => c.Key == "storage").Score);
    }

    [Fact]
    public void PinPukDoCartao_NaoEDefeito()
    {
        // device.sim warn = o cartão pede PIN — problema do cliente, não do
        // aparelho. warn=85, nunca o 55 genérico de um defeito real.
        var r = Report();
        Set(r, "device.sim", "warn", "READY");
        var res = HealthScore.Compute(r);
        var security = res.Categories.Single(c => c.Key == "security");
        Assert.Equal(85, security.Score);
    }
}

/// <summary>
/// As etiquetas da faixa (health.band.*) vivem no Core via BandL10nKey —
/// fora do radar do L10nTests, que só lê a app WPF. Serial porque toca no
/// L10n.Current global.
/// </summary>
[Collection(SerialCollection.Name)]
public sealed class HealthScoreL10nTests
{
    [Fact]
    public void EtiquetasDeBanda_ExistemNosDoisIdiomas()
    {
        var chaves = new[]
        {
            "health.band.healthy", "health.band.fair",
            "health.band.concerning", "health.band.none",
            "health.summary", "log.health",
        };
        var anterior = L10n.Current;
        try
        {
            foreach (var idioma in new[] { "pt", "en" })
            {
                L10n.Set(idioma);
                foreach (var chave in chaves)
                    Assert.True(L10n.T(chave) != chave, $"{idioma}:{chave} sem tradução");
            }
        }
        finally { L10n.Set(anterior); }
    }
}
