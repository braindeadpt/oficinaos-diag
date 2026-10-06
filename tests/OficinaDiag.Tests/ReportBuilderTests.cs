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
