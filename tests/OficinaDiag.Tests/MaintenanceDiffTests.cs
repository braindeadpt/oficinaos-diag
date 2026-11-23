using OficinaDiag.Maintenance;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// O antes/depois do relatório — função pura. Cada teste aqui é uma promessa
/// ao cliente: o delta que aparece no papel é o que o aparelho mediu.
/// </summary>
public class MaintenanceDiffTests
{
    private static MaintenanceSnapshot Snap(
        long? free = null, long? mem = null, int? procs = null,
        double? anim = null) => new()
    {
        StorageFreeBytes = free,
        MemAvailableBytes = mem,
        RunningProcesses = procs,
        Animations = anim is null ? null : new AnimationScales(anim, anim, anim),
    };

    [Fact]
    public void Compute_SemAntesOuDepois_NaoHaLinhas()
    {
        Assert.Empty(MaintenanceDiff.Compute(null, Snap(free: 1)));
        Assert.Empty(MaintenanceDiff.Compute(Snap(free: 1), null));
        Assert.Empty(MaintenanceDiff.Compute(null, null));
    }

    [Fact]
    public void Compute_ArmazenamentoMostraDeltaComSinal()
    {
        var mib = 1024L * 1024;
        var before = Snap(free: 100 * mib);
        var after = Snap(free: 612 * mib);
        var rows = MaintenanceDiff.Compute(before, after);
        var storage = rows.Single(r => r.Key == "storage.free");
        Assert.Equal("+512 MB", storage.Delta);
        Assert.Equal("up-good", storage.Direction);
        Assert.Equal("100 MB", storage.Before);
        Assert.Equal("612 MB", storage.After);
    }

    [Fact]
    public void Compute_ProcessosADiminuirEDirecaoBoa()
    {
        var rows = MaintenanceDiff.Compute(Snap(procs: 120), Snap(procs: 97));
        var procs = rows.Single(r => r.Key == "processes.running");
        Assert.Equal("-23", procs.Delta);
        Assert.Equal("down-good", procs.Direction);
    }

    [Fact]
    public void Compute_MetricaSemLeituraNaoAparece()
    {
        // Antes não mediu RAM, depois mediu: a linha existe mas sem delta —
        // nunca um número inventado para tapar a falta de medição.
        var rows = MaintenanceDiff.Compute(Snap(), Snap(mem: 1024L * 1024 * 1024));
        var ram = rows.Single(r => r.Key == "memory.available");
        Assert.Null(ram.Before);
        Assert.Null(ram.Delta);
        Assert.Equal("1 GB", ram.After);
    }

    [Fact]
    public void Compute_MetricaIgualNaoTemDelta()
    {
        // Sem variação não há variação: o relatório não enche o papel com "+0 KB".
        var mib = 1024L * 1024;
        var rows = MaintenanceDiff.Compute(Snap(free: 100 * mib), Snap(free: 100 * mib));
        var storage = rows.Single(r => r.Key == "storage.free");
        Assert.Null(storage.Delta);
    }

    [Fact]
    public void Compute_BateriaEDeltaNuloInformativo()
    {
        var before = Snap(); before.BatteryLevelPct = 80;
        var after = Snap(); after.BatteryLevelPct = 78;
        var rows = MaintenanceDiff.Compute(before, after);
        var batt = rows.Single(r => r.Key == "battery.level");
        Assert.Equal("neutral", batt.Direction);
        Assert.Null(batt.Delta); // a limpeza não move a bateria — mostra-se, não julga-se
    }

    [Fact]
    public void FormatBytes_UsaUnidadesLegiveis()
    {
        Assert.Equal("512 MB", MaintenanceDiff.FormatBytes(512L * 1024 * 1024));
        Assert.Equal("2 GB", MaintenanceDiff.FormatBytes(2L * 1024 * 1024 * 1024));
        Assert.Equal("+512 MB", MaintenanceDiff.FormatSignedBytes(512L * 1024 * 1024));
        Assert.Equal("-256 MB", MaintenanceDiff.FormatSignedBytes(-256L * 1024 * 1024));
    }

    [Fact]
    public void Build_MontaRelatorioComDeltasEAcoes()
    {
        var before = Snap(free: 100L * 1024 * 1024);
        var after = Snap(free: 200L * 1024 * 1024);
        var actions = new[]
        {
            new MaintenanceActionResult { Key = "trim-caches", LabelPt = "Caches", Status = "ok" },
        };
        var report = MaintenanceReport.Build(before, after, actions);
        Assert.Same(before, report.Before);
        Assert.Same(after, report.After);
        Assert.Single(report.Actions);
        Assert.Contains(report.Deltas, d => d.Key == "storage.free");
    }
}
