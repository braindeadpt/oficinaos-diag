using OficinaDiag.Devices;
using OficinaDiag.Grading;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>Grading determinístico A–D — os tetos que o checklist e os checks impõem.</summary>
public sealed class GraderTests
{
    private static DeviceReport Report()
    {
        var r = new DeviceReport { Platform = "android" };
        r.Set("scan", "pass", "completo");
        r.Set("battery.level", "info", "85%"); // check real — "scan" não conta para coverage
        return r;
    }

    [Fact]
    public void SemDados_GrauIndeterminado()
    {
        var g = Grader.Compute(new DeviceReport());
        Assert.Equal("—", g.Grade);
        Assert.Contains(g.Reasons, x => x.Contains("sem dados"));
    }

    [Fact]
    public void ScanLimpo_GrauA()
    {
        var g = Grader.Compute(Report());
        Assert.Equal("A", g.Grade);
        Assert.Empty(g.Parts);
    }

    [Fact]
    public void MdmWarn_EHardLock_D()
    {
        var r = Report();
        r.Set("security.mdm", "warn", "supervisionado");
        Assert.Equal("D", Grader.Compute(r).Grade);
    }

    [Fact]
    public void ActivationWarn_EHardLock_D()
    {
        var r = Report();
        r.Set("device.activation", "warn", "Unactivated");
        Assert.Equal("D", Grader.Compute(r).Grade);
    }

    [Fact]
    public void SkippedNaoDisparaHardLock()
    {
        var r = Report();
        // iOS sem MDM consultável fica "skipped" — nunca deve chumbar a retoma.
        r.Set("security.mdm", "skipped", "indeterminado");
        r.Set("device.activation", "skipped", null);
        Assert.Equal("A", Grader.Compute(r).Grade);
    }

    [Fact]
    public void BateriaAbaixoDe60_EFail_D()
    {
        var r = Report();
        r.Set("battery.capacity", "fail", "55%");
        var g = Grader.Compute(r);
        Assert.Equal("D", g.Grade);
        Assert.Contains("bateria", g.Parts);
    }

    [Fact]
    public void BateriaEntre60e80_EC()
    {
        var r = Report();
        r.Set("battery.capacity", "warn", "72%");
        Assert.Equal("C", Grader.Compute(r).Grade);
    }

    [Fact]
    public void BatteryHealthWarnReal_EC()
    {
        // Com valor real lido do device — documenta o comportamento pretendido
        // (o bug do parser fingia health="" → warn → C em aparelhos saudáveis).
        var r = Report();
        r.Set("battery.health", "warn", "overheat");
        Assert.Equal("C", Grader.Compute(r).Grade);
    }

    [Fact]
    public void ToqueFalhou_EDeSugereEcra()
    {
        var r = Report();
        r.Set("test.touch", "fail", "34/60 zonas");
        var g = Grader.Compute(r);
        Assert.Equal("D", g.Grade);
        Assert.Contains("ecrã", g.Parts);
    }

    [Fact]
    public void ChecklistPeso2_ED()
    {
        var r = Report();
        r.Set("checklist.liquid", "fail", null);
        Assert.Equal("D", Grader.Compute(r).Grade);
    }

    [Fact]
    public void ChecklistPeso1_EC()
    {
        var r = Report();
        r.Set("checklist.buttons", "warn", null);
        Assert.Equal("C", Grader.Compute(r).Grade);
    }

    [Fact]
    public void ChecklistPeso0_EB()
    {
        var r = Report();
        r.Set("checklist.sim_tray", "fail", null);
        var g = Grader.Compute(r);
        Assert.Equal("B", g.Grade);
        Assert.NotEmpty(g.Reasons);
    }

    [Fact]
    public void ChecklistSkipped_NaoContaComoFeito()
    {
        var r = Report();
        r.Set("checklist.mic", "skipped", null);
        var g = Grader.Compute(r);
        Assert.Equal("A", g.Grade);
        Assert.Contains(g.Reasons, x => x.Contains("checklist físico por preencher"));
    }

    [Fact]
    public void Apply_GravaGradeESugestaoNoReport()
    {
        var r = Report();
        r.Set("test.touch", "fail", "10/60");
        var g = Grader.Apply(r);
        Assert.Equal("D", g.Grade);
        Assert.True(r.Results.TryGetValue("grade.overall", out var gr));
        Assert.Equal("fail", gr!.Status);
        Assert.True(r.Results.ContainsKey("suggest.parts"));
    }

    [Fact]
    public void BootloaderAusente_SkippedNaoCap()
    {
        // Regressão do fix 2026-10: prop ro.boot.verifiedbootstate ausente era
        // emitida como "warn" → cap C "software modificado" sem prova.
        var r = Report();
        r.Set("security.bootloader", "skipped", "prop ausente");
        Assert.Equal("A", Grader.Compute(r).Grade);
    }

    [Fact]
    public void ScanFalhado_NaoEGrauA()
    {
        // Um device que não respondeu ao adb sai só com scan:fail — é meta,
        // não dado do aparelho. Sem dados reais o grau é indeterminado.
        var r = new DeviceReport { Platform = "android" };
        r.Set("scan", "fail", "sem resposta");
        var g = Grader.Compute(r);
        Assert.Equal("—", g.Grade);
    }

    [Fact]
    public void ScanParcialFalhado_NotificaIncompleto()
    {
        var r = Report();
        r.Set("scan", "fail", "timeout");
        r.Set("battery.capacity", "pass", "95%");
        var g = Grader.Compute(r);
        Assert.Contains(g.Reasons, x => x.Contains("incompleta"));
    }
}
