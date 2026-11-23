using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using OficinaDiag.Reports;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SendDialog(_report.Purpose) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Log(L10n.F("log.send.start", dlg.ShopCode));
        SendButton.IsEnabled = false; // double-submit criava pedidos duplicados na loja
        // Sincroniza nos dois sentidos: um envio explícito como "reparação"
        // limpa o "sale" que o checklist possa ter deixado.
        _report.Purpose = dlg.Purpose == "sale" ? "sale" : null;
        try
        {
            var (ok, msg) = await _cloud.SendToShopAsync(
                dlg.ShopCode, _report, dlg.CustomerName, dlg.CustomerPhone, dlg.CustomerEmail,
                _lastAiReport, dlg.Purpose, Fila);
            // "Ficou em fila" não é um falhanço para o técnico: o relatório sai
            // quando a rede voltar, e não deve aparecer com um "!" à frente.
            Log(ok || msg == L10n.T("cloud.send.queued")
                ? $"> {msg}"
                : L10n.F("log.send.fail", msg));
            if (!ok) _ = DrenarFilaAsync();
        }
        finally { SendButton.IsEnabled = true; }
    }

    private async void LogSend_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = OficinaDiag.Log.AppLog.FilePath;
            if (!File.Exists(path)) { Log(L10n.T("log.log.none")); return; }
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
            {
                tail = await sr.ReadToEndAsync();
            }
            // Após uma rotação o histórico recente está em diag.log.1 —
            // juntar antes do corte para o suporte não perder os erros.
            if (File.Exists(path + ".1"))
            {
                using var fsPrev = new FileStream(path + ".1", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var srPrev = new StreamReader(fsPrev);
                tail = await srPrev.ReadToEndAsync() + tail;
            }
            // A Cloud recusa logs vazios ou acima de 200k — corta para a
            // cauda (com marcador) ou devolve null quando não há nada.
            var preparado = Cloud.IntakeRules.PrepareLogForUpload(tail);
            if (preparado is null) { Log(L10n.T("log.log.none")); return; }
            tail = preparado;
            var hint = _report is not null
                ? $"{_report.Platform} {_report.Device.Brand} {_report.Device.Model} {_report.Device.Os} {_report.Device.OsVersion}".Trim()
                : _current?.Label;
            LogButton.IsEnabled = false;
            Log(L10n.T("log.log.sending"));
            var (ok, msg) = await _cloud.SendLogAsync(tail, _report?.ToolVersion ?? "0.1.0", hint);
            Log(ok ? $"> {msg}" : L10n.F("log.log.fail", msg));
        }
        catch (Exception ex) { Log(L10n.F("log.log.fail", ex.Message)); }
        finally { LogButton.IsEnabled = true; }
    }

    private void Config_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_config, _history) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _config.Save();
        // Retenção nova aplica-se já: o histórico relido esconde o que passou
        // do limite, sem esperar pelo próximo scan.
        _history.RetentionDays = _config.HistoryRetentionDays;
        // o diálogo valida antes de guardar — aqui é sempre um URI parseable
        if (Cloud.DiagConfig.TryValidateCloudUrl(_config.CloudUrl, out var uri))
            _cloud.BaseUri = new Uri(uri.ToString().TrimEnd('/') + "/");
        Log($"> cloud: {_config.CloudUrl}");
    }

    private async void Ai_Click(object sender, RoutedEventArgs e) =>
        await GenerateAiReportForPurposeAsync(null);

    private async void MaintenanceAi_Click(object sender, RoutedEventArgs e) =>
        await GenerateAiReportForPurposeAsync("maintenance");

    private bool _aiBusy;

    private void UpdateAiButtons()
    {
        var can = _report is not null && !_aiBusy;
        AiButton.IsEnabled = can;
        MaintenanceAiButton.IsEnabled = can;
    }

    private async Task GenerateAiReportForPurposeAsync(string? purpose)
    {
        if (_report is null) return;
        var report = _report;
        // Aviso de privacidade: crash logs (~40KB com paths e usernames) vão
        // para o LLM via cloud — o técnico confirma antes de enviar.
        if (MessageBox.Show(this, L10n.T("dlg.ai.privacy"),
                L10n.T("dlg.ai.token.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        var token = _config.GetShopToken();
        _aiBusy = true;
        AiButton.IsEnabled = false; // relatório IA é pago — sem double-submit
        MaintenanceAiButton.IsEnabled = false;
        try
        {
            string lang;
            if (token is not null)
            {
                // O relatório sai no idioma da UI — forçar "pt" ignorava as
                // Opções e devolvia texto PT a quem trabalha em EN.
                lang = L10n.Current;
            }
            else
            {
                var tdlg = new TokenDialog { Owner = this };
                if (tdlg.ShowDialog() != true) return;
                token = tdlg.Token;
                lang = tdlg.Lang;
                // Só o código de emparelhamento (XXXX-YYYY da app da loja) — a
                // cloud troca-o por um token scoped "diag". Um token completo
                // colado é recusado: este PC nunca deve guardar poder total.
                if (!Cloud.IntakeRules.IsPairingCode(token))
                {
                    Log(L10n.T("log.pairing.codeonly"));
                    return;
                }
                Log(L10n.T("log.pairing"));
                var (pOk, pToken, pShop, pErr) = await _cloud.RedeemPairingAsync(token);
                if (!pOk) { Log(L10n.F("log.pairing.fail", pErr)); return; }
                Log(L10n.F("log.pairing.ok", pShop));
                token = pToken;
                if (tdlg.Remember)
                {
                    _config.SetShopToken(token, "diag");
                    _config.Save();
                    // O emparelhamento acontece a meio do relatório de manutenção,
                    // depois do scan: a auditoria Pro só entra no próximo scan.
                    // iOS não tem auditoria de apps — o aviso seria enganador.
                    if (_current?.Kind == Devices.DeviceKind.Android)
                        Log(L10n.T("log.pairing.rescan"));
                }
            }
            Log(L10n.T("log.ai.gen"));
            var (ok, text) = await _cloud.GenerateAiReportAsync(token!, report, lang, purpose);
            if (!ok) { Log(L10n.F("log.ai.fail", text)); return; }
            // O relatório de manutenção é para o técnico/sala; não sobrescreve o
            // diagnóstico que viaja no envio ao cliente.
            if (purpose is null && ReferenceEquals(_report, report)) _lastAiReport = text;
            // Guarda em HTML renderizado (entregável ao cliente) e abre no browser.
            var prefix = purpose == "maintenance" ? "ai-maint-report-" : "ai-report-";
            var dlg = new SaveFileDialog { Filter = L10n.T("dlg.ai.filter"), FileName = $"{prefix}{report.CollectedAt:yyyyMMdd-HHmm}.html" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, ReportBuilder.AiReportToHtml(report, text, ThemeManager.Current));
                }
                catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
                Log(L10n.F("log.ai.saved", dlg.FileName));
                OpenHtmlFile(dlg.FileName);
            }
        }
        finally
        {
            _aiBusy = false;
            UpdateAiButtons();
        }
    }

    private Cloud.CloudOutbox? _outbox;

    /// <summary>
    /// Fila durável dos envios que falharam por rede. Criada à primeira
    /// utilização para não abrir pasta nenhuma em quem nunca envia à loja.
    /// </summary>
    private Cloud.CloudOutbox Fila => _outbox ??= new Cloud.CloudOutbox(
        Cloud.CloudOutbox.PastaPorDefeito,
        (code, corpo, chave, ct) => _cloud.SendIntakePayloadAsync(code, corpo, chave, ct));

    private async Task DrenarFilaAsync()
    {
        try
        {
            var enviados = await Fila.DrenarAsync();
            if (enviados > 0) Log(L10n.F("log.outbox.enviados", enviados));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
    }
}
