using OficinaDiag.Devices;
using OficinaDiag.Reports;
using Xunit;

namespace OficinaDiag.Tests;

public class ReportIntegrityTests
{
    private static DeviceReport Sample() => new()
    {
        Platform = "android",
        Device = new DeviceIdentity { Brand = "Samsung", Model = "SM-G991B", Os = "Android", OsVersion = "14", Serial = "R5X" },
    };

    private static InsuranceForm Form() => new()
    {
        InsuredName = "Cliente",
        DamageDescription = "Queda",
        ShopName = "Loja",
    };

    [Fact]
    public void Marcar_EIdempotente()
    {
        var uma = ReportIntegrity.Marcar(ReportBuilder.ToHtml(Sample()));
        var duas = ReportIntegrity.Marcar(uma);
        Assert.Equal(uma, duas);
        Assert.Equal(ReportIntegrity.Calcular(uma), ReportIntegrity.Calcular(duas));
    }

    [Fact]
    public void AsTresVistasSaemComSelo()
    {
        var r = Sample();
        foreach (var html in new[]
        {
            ReportBuilder.ToHtml(r),
            ReportBuilder.ToClientHtml(r),
            ReportBuilder.ToInsuranceHtml(r, Form()),
        })
        {
            Assert.Contains(ReportIntegrity.Prefixo, html);
            Assert.True(ReportIntegrity.Verificar(html).Ok);
        }
    }

    [Fact]
    public void DocumentoIntactoConfere()
    {
        var html = ReportIntegrity.Marcar("<!DOCTYPE html><html><body><p>ok</p></body></html>");
        var (ok, declarado, calculado) = ReportIntegrity.Verificar(html);
        Assert.True(ok);
        Assert.Equal(calculado, declarado);
        Assert.Equal(64, calculado.Length);
    }

    [Fact]
    public void UmaAlteracaoInvalidaOSelo()
    {
        var html = ReportBuilder.ToInsuranceHtml(Sample(), Form());
        var (okAntes, _, _) = ReportIntegrity.Verificar(html);
        Assert.True(okAntes);

        // O cenário real: alguém edita o relatório depois de emitido.
        var adulterado = html.Replace("Queda", "Queda ligeira");
        var (ok, declarado, calculado) = ReportIntegrity.Verificar(adulterado);
        Assert.False(ok);
        Assert.NotEqual(declarado, calculado);
    }

    [Fact]
    public void SemSeloNaoEIntegro()
    {
        var (ok, declarado, calculado) = ReportIntegrity.Verificar("<html><body><p>sem selo</p></body></html>");
        Assert.False(ok);
        Assert.Null(declarado);
        Assert.Equal(64, calculado.Length);
    }

    [Fact]
    public void SeloNaoDependeDoFimDeLinha()
    {
        var unix = "<html><body><p>a</p>\n<p>b</p></body></html>";
        var win = unix.Replace("\n", "\r\n");
        Assert.Equal(ReportIntegrity.Calcular(unix), ReportIntegrity.Calcular(win));
    }

    [Fact]
    public void EditarOSeloTambemInvalida()
    {
        var html = ReportIntegrity.Marcar("<html><body><p>ok</p></body></html>");
        var hash = html[html.IndexOf("<code>", StringComparison.Ordinal)..];
        hash = hash[6..70];
        var trocado = html.Replace(hash, new string('0', 64));
        Assert.False(ReportIntegrity.Verificar(trocado).Ok);
    }
}
