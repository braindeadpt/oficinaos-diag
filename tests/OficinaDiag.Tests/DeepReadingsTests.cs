using System.IO.Compression;
using System.Text;
using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Pipeline fase 2: o parsing das leituras profundas com saídas reais de
/// três marcas (Samsung/Pixel/Xiaomi) — cada leitura declara valor,
/// «não disponível neste aparelho» ou «requer root», nunca silêncio.
/// </summary>
public sealed class DeepReadingsTests
{
    // ── fixtures sysfs de marcas reais ─────────────────────────────────
    // Samsung Galaxy S21: expõe charge_full + design no battery, sem cycles
    private const string SamsungS21Sysfs = """
        battery/cycle_count:missing
        battery/charge_full:value:4756000
        battery/charge_full_design:value:4800000
        bms/charge_full:missing
        bms/charge_full_design:missing
        max170xx_battery/charge_full:missing
        max170xx_battery/charge_full_design:missing
        """;

    // Pixel 7: cycle_count presente; alguns nós existem mas o shell não lê
    private const string Pixel7Sysfs = """
        battery/cycle_count:value:412
        battery/charge_full:denied
        battery/charge_full_design:denied
        bms/charge_full:missing
        bms/charge_full_design:missing
        max170xx_battery/charge_full:missing
        max170xx_battery/charge_full_design:missing
        """;

    // Xiaomi Redmi Note: expõe capacidade no bms, sem cycle_count
    private const string XiaomiRedmiSysfs = """
        battery/cycle_count:missing
        battery/charge_full:missing
        battery/charge_full_design:missing
        bms/charge_full:value:4900000
        bms/charge_full_design:value:5000000
        max170xx_battery/charge_full:missing
        max170xx_battery/charge_full_design:missing
        """;

    [Fact]
    public void ProbeSamsung_CargaRealComValorECiclosEmFalta()
    {
        var r = DeepReadingParsing.FromMarkedProbe("battery/charge_full", SamsungS21Sysfs, "battery/charge_full");
        Assert.Equal(ReadingState.Value, r.State);
        Assert.Equal("4756000", r.Value);

        var cycles = DeepReadingParsing.FromMarkedProbe("battery/cycle_count", SamsungS21Sysfs, "battery/cycle_count");
        Assert.Equal(ReadingState.Unavailable, cycles.State);
    }

    [Fact]
    public void ProbePixel_NegadoERaiz()
    {
        // O nó existe mas o shell não lê — é «requer root», nunca «em falta».
        var r = DeepReadingParsing.FromMarkedProbe("battery/charge_full", Pixel7Sysfs, "battery/charge_full");
        Assert.Equal(ReadingState.RequiresRoot, r.State);

        var cycles = DeepReadingParsing.FromMarkedProbe("battery/cycle_count", Pixel7Sysfs, "battery/cycle_count");
        Assert.Equal(ReadingState.Value, cycles.State);
        Assert.Equal("412", cycles.Value);
    }

    [Fact]
    public void ProbeXiaomi_CapacidadeNoBms()
    {
        var r = DeepReadingParsing.FromMarkedProbe("bms/charge_full", XiaomiRedmiSysfs, "bms/charge_full");
        Assert.Equal(ReadingState.Value, r.State);
        Assert.Equal("4900000", r.Value);
    }

    private static (Dictionary<string, string> valores, Dictionary<string, string> estados) ParseComoColetor(string dump)
    {
        // O caminho exato do coletor — os fixtures de marcas cobrem o que a
        // oficina recebe, não só o helper isolado.
        var v = new Dictionary<string, string>(StringComparer.Ordinal);
        var st = new Dictionary<string, string>(StringComparer.Ordinal);
        DeepReadingParsing.ParseMarkedSysfs(dump, v, st);
        return (v, st);
    }

    [Fact]
    public void CapacidadeReal_LidaEmBatteryComBmsNegado_NaoPuxaRoot()
    {
        // A combinação do review: valor lido no nó principal, nó alternativo
        // da MESMA família bloqueado. O «denied» efetivo é falso porque a
        // família produziu valor — simula o que o coletor passa à regra pura.
        var (valores, estados) = ParseComoColetor(SamsungS21Sysfs);
        bool AnyDenied(string[] nodes) =>
            nodes.Any(n => estados.TryGetValue(n, out var st) && st == "denied");
        string[] realNodes = ["battery/charge_full", "bms/charge_full", "max170xx_battery/charge_full"];
        string[] designNodes = ["battery/charge_full_design", "bms/charge_full_design", "max170xx_battery/charge_full_design"];
        var realOk = realNodes.Any(n => valores.ContainsKey(n));
        var designOk = designNodes.Any(n => valores.ContainsKey(n));
        var fb = DeepReadingParsing.CapacityFallbackState(realOk, designOk,
            AnyDenied(realNodes), AnyDenied(designNodes));
        // Samsung S21: real+design legíveis → sem fallback nenhum.
        Assert.Null(fb);
    }

    [Fact]
    public void CaminhoColetor_RealLegivelBmsBloqueadoDesignAusente_NaoERoot()
    {
        // A combinação exata do review: valor lido no nó principal, nó
        // alternativo da MESMA família bloqueado, design em falta. Sem o
        // filtro «denied só conta se a família não produziu valor», isto
        // sairia «requer root» — errado, porque nenhuma leitura necessária
        // está bloqueada: a percentagem falta por falta do design.
        const string probe = """
            battery/charge_full:value:4385000
            bms/charge_full:denied
            max170xx_battery/charge_full:missing
            battery/charge_full_design:missing
            bms/charge_full_design:missing
            max170xx_battery/charge_full_design:missing
            battery/cycle_count:missing
            battery/charge_counter:missing
            """;
        var (valores, estados) = ParseComoColetor(probe);
        bool AnyDenied(string[] nodes) =>
            nodes.Any(n => estados.TryGetValue(n, out var st) && st == "denied");
        string[] realNodes = ["battery/charge_full", "bms/charge_full", "max170xx_battery/charge_full"];
        string[] designNodes = ["battery/charge_full_design", "bms/charge_full_design", "max170xx_battery/charge_full_design"];
        var realOk = realNodes.Any(n => valores.ContainsKey(n));
        var designOk = designNodes.Any(n => valores.ContainsKey(n));
        var fb = DeepReadingParsing.CapacityFallbackState(realOk, designOk,
            AnyDenied(realNodes), AnyDenied(designNodes));
        Assert.NotNull(fb);
        Assert.Equal("não disponível neste aparelho", fb!.Value.state);
        // E a nota de produção para este cenário não pode mencionar root:
        // o que falta é o valor de fábrica, não uma permissão.
        Assert.True(realOk && !designOk);
    }

    [Fact]
    public void CaminhoColetor_PixelDesignNegado()
    {
        var (valores, estados) = ParseComoColetor(Pixel7Sysfs);
        Assert.Equal("412", valores["battery/cycle_count"]);
        Assert.False(valores.ContainsKey("battery/charge_full"));
        Assert.Equal("denied", estados["battery/charge_full"]);
    }

    [Fact]
    public void CaminhoColetor_SamsungEmFalta()
    {
        var (valores, estados) = ParseComoColetor(SamsungS21Sysfs);
        Assert.Equal("4756000", valores["battery/charge_full"]);
        Assert.Equal("missing", estados["battery/cycle_count"]);
    }

    [Fact]
    public void CaminhoColetor_XiaomiBms()
    {
        var (valores, _) = ParseComoColetor(XiaomiRedmiSysfs);
        Assert.Equal("4900000", valores["bms/charge_full"]);
        Assert.False(valores.ContainsKey("battery/cycle_count"));
    }

    [Fact]
    public void ProbeNulo_EmFalta()
    {
        Assert.Equal(ReadingState.Unavailable,
            DeepReadingParsing.FromMarkedProbe("k", null, "battery/cycle_count").State);
    }

    [Theory]
    [InlineData("412\n", 412)]
    [InlineData("0\n", 0)]
    [InlineData("lixo\n99999999\n", null)]   // fora da gama plausível
    [InlineData(null, null)]
    public void CycleCount_Parse(string? output, int? esperado)
    {
        Assert.Equal(esperado, DeepReadingParsing.ParseCycleCount(output));
    }

    [Theory]
    [InlineData("0x01 0x01\n", 1)]
    [InlineData("0x0a 0x0b\n", 11)]
    [InlineData("sem nada\n", null)]
    [InlineData(null, null)]
    public void EmmcLife_PiorClasse(string? output, int? esperado)
    {
        Assert.Equal(esperado, DeepReadingParsing.ParseEmmcLifeClass(output));
    }

    // ── regra da capacidade: root só quando chega ─────────────────────

    [Theory]
    // par completo → sem estado alternativo
    [InlineData(true, true, false, false, null)]
    // real bloqueada, design legível → root resolve
    [InlineData(false, true, true, false, "requer root")]
    // design bloqueado, real legível → root resolve
    [InlineData(true, false, false, true, "requer root")]
    // real bloqueada MAS design em falta → root não chega
    [InlineData(false, false, true, false, "não disponível neste aparelho")]
    // design bloqueado MAS real em falta → idem
    [InlineData(false, false, false, true, "não disponível neste aparelho")]
    // tudo em falta → indisponível
    [InlineData(false, false, false, false, "não disponível neste aparelho")]
    // flags BRUTOS: nó alternativo negado com a família já lida não conta —
    // real legível + bms negado + design em falta → indisponível, nunca root
    [InlineData(true, false, true, false, "não disponível neste aparelho")]
    // simétrico: design legível + alternativo negado + real em falta → idem
    [InlineData(false, true, false, true, "não disponível neste aparelho")]
    // e sem o desconto continua a haver root quando a família certa está negada
    [InlineData(true, false, false, true, "requer root")]
    public void CapacityFallback_RootSoQuandoChega(
        bool realOk, bool designOk, bool realDenied, bool designDenied, string? esperado)
    {
        var r = DeepReadingParsing.CapacityFallbackState(realOk, designOk, realDenied, designDenied);
        Assert.Equal(esperado, r?.state);
    }

    // ── bugreport: contagens e tipos, nunca conteúdo ────────────────────

    [Theory]
    [InlineData("FS/data/tombstones/tombstone_00", "tombstone")]
    [InlineData("FS/data/system/dropbox/system_app_crash@1.txt", "appcrash")]
    [InlineData("FS/data/system/dropbox/data_app_anr@2.txt", "anr")]
    [InlineData("bugreport-ramdump_kernel_panic.txt", "kernelpanic")]
    [InlineData("FS/data/system/dropbox/system_server_crash@3.txt", "systemcrash")]
    [InlineData("bugreport-SQQ3A.txt", null)]
    [InlineData("dumpstate_board.txt", null)]
    public void BugreportClassify_Tipos(string entrada, string? tipo)
    {
        Assert.Equal(tipo, BugreportCrashes.Classify(entrada));
    }

    [Fact]
    public void BugreportCounts_DeUmZipReal()
    {
        // Zip sintético com nomes de entradas de um bugreport real — só os
        // nomes contam, o conteúdo nunca é lido.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var n in new[]
            {
                "bugreport-SQ3A.txt",
                "FS/data/tombstones/tombstone_00",
                "FS/data/tombstones/tombstone_01",
                "FS/data/tombstones/tombstone_02",
                "FS/data/system/dropbox/system_app_crash@1.txt",
                "FS/data/system/dropbox/data_app_anr@2.txt",
                "FS/data/system/dropbox/data_app_anr@3.txt",
                "dumpstate_board.txt",
            })
            {
                var e = zip.CreateEntry(n);
                using var w = new StreamWriter(e.Open());
                w.Write("conteúdo pessoal que nunca deve sair daqui");
            }
        }
        ms.Position = 0;
        using var read = new ZipArchive(ms);
        var counts = BugreportCrashes.CountEntries(read);

        Assert.Equal(3, counts.Tombstones);
        Assert.Equal(1, counts.AppCrashes);
        Assert.Equal(2, counts.Anrs);
        Assert.Equal(0, counts.KernelPanics);
        Assert.Equal(6, counts.Total);
    }

    [Fact]
    public void BugreportSummarize_SoContagensETipos()
    {
        var s = BugreportCrashes.Summarize(new BugreportCrashes.Counts(2, 1, 0, 1, 0));
        Assert.Contains("2 tombstones", s);
        Assert.Contains("1 panics de kernel", s);
        Assert.DoesNotContain("conteúdo", s);

        Assert.Equal("sem crashes registados no bugreport",
            BugreportCrashes.Summarize(new BugreportCrashes.Counts(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void BugreportStatus_PanicsRepetidosAvisam()
    {
        Assert.Equal("warn", BugreportCrashes.Status(new BugreportCrashes.Counts(0, 0, 0, 1, 0)));
        Assert.Equal("warn", BugreportCrashes.Status(new BugreportCrashes.Counts(6, 0, 0, 0, 0)));
        Assert.Equal("info", BugreportCrashes.Status(new BugreportCrashes.Counts(0, 5, 3, 0, 0)));
    }
}
