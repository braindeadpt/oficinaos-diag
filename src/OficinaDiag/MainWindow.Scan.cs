using System.Windows;
using System.Windows.Media;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Health;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (BusyBar.Visibility == Visibility.Visible) return;
        if (_current is null)
        {
            Log(L10n.T("log.nodev"));
            return;
        }
        BeginBusy(L10n.T("busy.scan"), cancellable: true);
        Log(L10n.T("log.scan.run"));
        try
        {
            // Os WaitForExit dentro dos coletores são síncronos — sem o
            // Task.Run cada chamada adb bloqueava a UI thread e a janela
            // congelava durante o scan inteiro.
            var scannedId = _current.Id;
            // Auditoria de segurança das apps é Pro (decisão do dono): sem
            // token Pro na máquina o scan gratuito não a corre.
            var securityAudit = _config.GetShopToken() is not null;
            var ct = _opCts?.Token ?? CancellationToken.None;
            var fresh = _current.Kind == DeviceKind.Android
                ? await Task.Run(() => new AndroidCollector(_adbPath, securityAudit).CollectAsync(scannedId.TrimEnd('!'), ct))
                : await Task.Run(() => new IosCollector().CollectAsync(scannedId, ct));
            // O scan substitui o relatório mas há resultados que ele não
            // volta a produzir — checklist manual, teste no ecrã e bancada.
            // PreserveManualFrom copia só esses: arrastar chaves USB/
            // derivadas (root, battery.swap) de um scan anterior falseava
            // a nova recolha (aviso obsoleto mantinha o grau limitado).
            if (_report is not null && DeviceIds.Same(_reportDeviceId, scannedId))
            {
                fresh.PreserveManualFrom(_report);
            }
            else if (_report is not null && _reportDeviceId is null && _report.HasManualData())
            {
                // Checklist/serviço/notas feitos sem USB não têm
                // identificador — não se assume que são do aparelho que
                // acabou de ligar: o técnico confirma antes de copiar os
                // dados manuais para o scan novo.
                var nome = fresh.Device.MarketingName ?? fresh.Device.Model ?? scannedId;
                var resp = MessageBox.Show(this, L10n.F("scan.manual.prompt", nome),
                    L10n.T("scan.manual.title"),
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (resp == MessageBoxResult.Cancel)
                {
                    Log(L10n.T("scan.manual.abort"));
                    return; // o finally repõe os botões — relatório e dados ficam
                }
                if (resp == MessageBoxResult.Yes)
                    fresh.PreserveManualFrom(_report);
                else
                    Log(L10n.T("scan.manual.discard"));
            }
            _report = fresh;
            _reportDeviceId = scannedId;
            // O parecer de IA era do relatório anterior — só se invalida
            // quando a substituição acontece mesmo: um Cancel no aviso de
            // dados manuais (ou um erro na recolha) não o pode apagar, senão
            // o envio à loja ia sem ele.
            _lastAiReport = null;

            // iOS: o serial da célula não se repete — se mudou desde a última
            // visita, a bateria foi trocada e isso tem de ficar no relatório.
            // TEM de correr ANTES do _history.Add abaixo: depois de gravado, o
            // scan compararia consigo próprio e a nota nunca aparecia.
            if (_report.Results.TryGetValue("battery.serial", out var batSerial)
                && _history.BatterySwappedSince(_report.Device.Serial ?? "", batSerial.Value) is { } swapped)
                _report.Set("battery.swap", "info", swapped,
                    "série da célula diferente do scan anterior deste aparelho");

            ResultsList.ItemsSource = _report.Results
                .Select(kv => new ResultRow
                {
                    Key = kv.Key,
                    Status = kv.Value.Status,
                    Display = $"{kv.Value.Value ?? ""} {kv.Value.Detail ?? ""}".Trim(),
                })
                .OrderBy(r => r.Key)
                .ToList();

            ApplyGrade();
            // histórico guarda o scan já com grau — o antes/depois mostra-o na lista
            _history.Add(_report);
            Log(L10n.F("log.scan.done", _report.Results.Count));
        }
        catch (OperationCanceledException) { Log(L10n.T("busy.cancelled")); }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
        finally { EndBusy(); }
    }

    /// <summary>
    /// Recomputa o grau A–D sempre que o relatório ganha dados (scan, SCAN+,
    /// teste no ecrã, checklist) — o veredicto vai para o log e para o HTML.
    /// </summary>
    private void ApplyGrade()
    {
        if (_report is null) return;
        var g = Grader.Apply(_report);
        // Health score 0–100 — derivado como o grau: recalcula-se sempre que
        // o relatório ganha dados (scan, teste no ecrã, bancada, checklist).
        var h = HealthScore.Apply(_report);
        // Dados manuais gravam-se depois do scan: sincroniza a entrada de
        // histórico (o antes/depois não pode perder o perfil do "antes") e
        // invalida o parecer de IA — foi escrito para os checks de então e
        // não pode viajar no envio à loja como se fosse actual.
        _history.Update(_report);
        _lastAiReport = null;
        RefreshResults();
        Log(L10n.F("log.grade", g.Grade, g.Verdict));
        if (h.Score is { } hs)
            Log(L10n.F("log.health", hs, L10n.T(h.BandL10nKey)));
    }

    /// <summary>Checklist físico — o que o USB não vê; entra no relatório e no grau.</summary>
    private void Checklist_Click(object sender, RoutedEventArgs e)
    {
        // Inspeção física sem scan é válida — telemóvel que não fala por USB.
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = _current?.Kind == DeviceKind.Ios ? "ios" : "android",
                Device = { Serial = _current?.Id?.TrimEnd('!') },
            };
            _reportDeviceId = _current?.Id;
        }
        else if (_current is not null && _reportDeviceId is not null
                 && !DeviceIds.Same(_reportDeviceId, _current.Id))
        {
            // Mesmo vetor do SERVIÇO: o checklist físico ia para o relatório
            // de outro aparelho e ficava gravado no histórico dele.
            Log(L10n.T("log.device.mismatch"));
            return;
        }
        var dlg = new ChecklistDialog(_report) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ApplyGrade();
        SyncReportButtons();
        Log(L10n.T("log.checklist.saved"));
    }

    /// <summary>Antes/depois — compara o relatório atual com um scan anterior do mesmo serial.</summary>
    private void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var serial = _report.Device.Serial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            Log(L10n.T("log.cmp.noserial"));
            return;
        }
        // O scan atual também está no histórico — exclui-o por timestamp.
        var previous = _history.ForSerial(serial)
            .Where(p => p.CollectedAt != _report.CollectedAt)
            .ToList();
        if (previous.Count == 0)
        {
            Log(L10n.T("log.cmp.none"));
            return;
        }
        new CompareWindow(_report, previous) { Owner = this }.ShowDialog();
    }

    private void RefreshResults()
    {
        EmptyState.Visibility = _report is null ? Visibility.Visible : Visibility.Collapsed;
        if (_report is null) return;
        ResultsList.ItemsSource = _report.Results
            .Select(kv => new ResultRow
            {
                Key = kv.Key,
                Status = kv.Value.Status,
                Display = $"{kv.Value.Value ?? ""} {kv.Value.Detail ?? ""}".Trim(),
            })
            .OrderBy(r => r.Key).ToList();
        ShowHealth();
        SyncReportButtons();
    }

    /// <summary>
    /// Linha de saúde junto ao cabeçalho dos resultados — percentagem +
    /// etiqueta L10n ("SAÚDE 87/100 — Saudável"), cor pela faixa. Corre em
    /// RefreshResults: troca de idioma/tema reaplica o texto sem recalcular.
    /// </summary>
    private void ShowHealth()
    {
        var h = HealthScore.Compute(_report!);
        if (h.Score is not { } s)
        {
            HealthText.Visibility = Visibility.Collapsed;
            return;
        }
        HealthText.Text = L10n.F("health.summary", s, L10n.T(h.BandL10nKey));
        HealthText.Foreground = (Brush)Application.Current.FindResource(
            h.Band switch
            {
                HealthBand.Healthy => "PassBrush",
                HealthBand.Fair => "WarnBrush",
                _ => "FailBrush",
            });
        HealthText.Visibility = Visibility.Visible;
    }
}
