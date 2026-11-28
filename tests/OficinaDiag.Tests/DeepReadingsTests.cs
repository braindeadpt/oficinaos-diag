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
