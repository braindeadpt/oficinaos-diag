using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OficinaDiag.Reports;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    /// <summary>Etiqueta de balcão — modelo, serial, grau e QR; cola no saco do aparelho.</summary>
    private void Label_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        try
        {
            Log(LabelPrinter.Print(_report)
                ? L10n.T("log.label.printed")
                : L10n.T("log.label.cancel"));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
    }

    /// <summary>Vista cliente — linguagem simples, grau em destaque, sem chaves técnicas.</summary>
    private void Client_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = L10n.T("dlg.client.filter"),
            FileName = $"cliente-{Reports.ReportBuilder.SafeFileToken(_report.Device.Serial)}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ReportBuilder.ToClientHtml(_report));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.client.saved", dlg.FileName));
        OpenHtmlFile(dlg.FileName);
    }

    /// <summary>
    /// Abre um ficheiro que acabámos de gravar — só .html: o nome vem do
    /// SaveFileDialog e o utilizador podia trocar a extensão à mão; com
    /// UseShellExecute isso corria a associação arbitrária da shell.
    /// </summary>
    private static void OpenHtmlFile(string path)
    {
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = L10n.T("dlg.export.filter"),
            FileName = $"diag-{Reports.ReportBuilder.SafeFileToken(_report.Device.Serial)}-{_report.CollectedAt:yyyyMMdd-HHmm}",
        };
        if (dlg.ShowDialog() != true) return;
        var isHtml = !dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        try
        {
            File.WriteAllText(dlg.FileName,
                isHtml ? ReportBuilder.ToHtml(_report, ThemeManager.Current)
                       : ReportBuilder.ToJson(_report));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.export.saved", dlg.FileName));
        // HTML é para ver/partilhar — abre já no browser predefinido.
        if (isHtml)
            OpenHtmlFile(dlg.FileName);
    }

    /// <summary>Relatório formal A4 para seguradora — dano + estado + carimbo da loja.</summary>
    private void Insurance_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new InsuranceDialog(_config) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Form is not { } form) return;
        var save = new SaveFileDialog
        {
            Filter = L10n.T("dlg.ins.filter"),
            FileName = $"seguradora-{Reports.ReportBuilder.SafeFileToken(_report.Device.Serial)}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
        };
        if (save.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(save.FileName, ReportBuilder.ToInsuranceHtml(_report, form));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.ins.saved", save.FileName));
        // Abre já no browser — daí é imprimir ou gravar em PDF.
        OpenHtmlFile(save.FileName);
    }
}
