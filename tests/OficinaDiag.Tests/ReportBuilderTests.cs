using System.Text.Json;
using OficinaDiag.Devices;
using OficinaDiag.Reports;
using Xunit;

namespace OficinaDiag.Tests;

public class ReportBuilderTests
{
    private static DeviceReport Sample() => new()
    {
        Platform = "android",
        Device = new DeviceIdentity { Brand = "Samsung", Model = "SM-G991B", Os = "Android", OsVersion = "14", Serial = "R5X" },
    };

    [Fact]
    public void AiReportHtmlEscapesRawHtml()
    {
        var md = "# Resumo\n\n<script>alert(1)</script>\n\nTexto <img src=x onerror=alert(2)> fim\n\n**ok**";
        var html = ReportBuilder.AiReportToHtml(Sample(), md);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<strong>ok</strong>", html); // markdown normal continua a funcionar
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData(" JaVaScRiPt:void(0)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("vbscript:msgbox(1)")]
    public void SafeUrl_SoDeixaPassarHttpMailto(string url)
    {
        Assert.Equal("#", ReportBuilder.SafeUrl(url));
    }

    [Theory]
    [InlineData("https://oficinaos.pt")]
    [InlineData("http://192.168.1.10:8080/x")]
    [InlineData("mailto:loja@oficinaos.pt")]
    public void SafeUrl_MantemEsquemasLegitimos(string url)
    {
        Assert.Equal(url, ReportBuilder.SafeUrl(url));
    }

    [Fact]
    public void AiReportHtmlReescreveLinksPerigosos()
    {
        var md = "[clica](javascript:alert(1)) e [ok](https://oficinaos.pt)";
        var html = ReportBuilder.AiReportToHtml(Sample(), md);
        Assert.DoesNotContain("javascript:", html.ToLowerInvariant().Replace("&quot;", ""));
        Assert.Contains("href=\"#\"", html);
        Assert.Contains("href=\"https://oficinaos.pt\"", html);
    }

    [Fact]
    public void SerialMaliciosoEEscapadoEmTodosOsFormatos()
    {
        var r = Sample();
        r.Device.Serial = "<img src=x onerror=alert(1)>";
        var htmls = new[]
        {
            ReportBuilder.ToHtml(r),
            ReportBuilder.ToClientHtml(r),
            ReportBuilder.ToInsuranceHtml(r, new InsuranceForm
            {
                InsuredName = "Cliente",
                DamageDescription = "Queda",
                ShopName = "Loja",
            }),
        };
        foreach (var html in htmls)
        {
            Assert.DoesNotContain("<img src=x", html);
            Assert.Contains("&lt;img src=x", html);
        }
    }

    [Fact]
    public void SafeFileToken_SanitizaSerialComPorta()
    {
        Assert.Equal("192.168.1.50_5555", ReportBuilder.SafeFileToken("192.168.1.50:5555"));
        Assert.Equal("device", ReportBuilder.SafeFileToken(null));
        Assert.Equal("AB_C", ReportBuilder.SafeFileToken("AB?C"));
    }

    [Fact]
    public void SafeCssToken_NaoDeixaEntrarMarcacao()
    {
        Assert.Equal("pass", ReportBuilder.SafeCssToken("pass"));
        Assert.Equal("grade-b", ReportBuilder.SafeCssToken("grade-b"));
        Assert.Equal("info", ReportBuilder.SafeCssToken("\"><script>alert(1)</script>"));
        Assert.Equal("d", ReportBuilder.SafeCssToken(null, "d"));
    }

    [Theory]
    [InlineData("sale", "sale")]
    [InlineData("repair", "repair")]
    public void CloudJsonIncludesPurpose(string purpose, string expected)
    {
        var r = Sample();
        r.Purpose = purpose;
        using var doc = JsonDocument.Parse(ReportBuilder.ToCloudJson(r, "pt"));
        Assert.Equal(expected, doc.RootElement.GetProperty("purpose").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("lixo")]
    public void CloudJsonOmitsUnknownPurpose(string? purpose)
    {
        var r = Sample();
        r.Purpose = purpose;
        using var doc = JsonDocument.Parse(ReportBuilder.ToCloudJson(r, "pt"));
        Assert.False(doc.RootElement.TryGetProperty("purpose", out _));
    }
}
