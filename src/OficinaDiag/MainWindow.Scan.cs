using System.Windows;
using OficinaDiag.Devices;
using OficinaDiag.Grading;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        _busy = true;
        Log(L10n.T("log.scan.run"));
        try
        {
            if (_current is null)
            {
                Log(L10n.T("log.nodev"));
                return;
            }
            _lastAiReport = null;
            // Os WaitForExit dentro dos coletores são síncronos — sem o
            // Task.Run cada chamada adb bloqueava a UI thread e a janela
            // congelava durante o scan inteiro.
            var scannedId = _current.Id;
            var fresh = _current.Kind == DeviceKind.Android
                ? await Task.Run(() => new AndroidCollector(_adbPath).CollectAsync(scannedId.TrimEnd('!')))
                : await Task.Run(() => new IosCollector().CollectAsync(scannedId));
            // Teste no ecrã/checklist/bancada feitos ANTES do scan já têm
            // resultados neste aparelho — substituir o relatório deitava-os
            // fora. Fundir o que o scan não produz (a base é o relatório
            // novo: tem os campos de hardware reais).
            if (_report is not null && _reportDeviceId == scannedId)
                foreach (var kv in _report.Results)
                    if (!fresh.Results.ContainsKey(kv.Key))
                        fresh.Results[kv.Key] = kv.Value;
            _report = fresh;
            _reportDeviceId = scannedId;

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
            ExportButton.IsEnabled = TestButton.IsEnabled = SendButton.IsEnabled =
                AiButton.IsEnabled = InsuranceButton.IsEnabled =
                CompareButton.IsEnabled = LabelButton.IsEnabled =
                ClientButton.IsEnabled = true;
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
        finally { ScanButton.IsEnabled = true; _busy = false; }
    }

    /// <summary>
    /// Recomputa o grau A–D sempre que o relatório ganha dados (scan, SCAN+,
    /// teste no ecrã, checklist) — o veredicto vai para o log e para o HTML.
    /// </summary>
    private void ApplyGrade()
    {
        if (_report is null) return;
        var g = Grader.Apply(_report);
        RefreshResults();
        Log(L10n.F("log.grade", g.Grade, g.Verdict));
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
        var dlg = new ChecklistDialog(_report) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ApplyGrade();
        ExportButton.IsEnabled = SendButton.IsEnabled = AiButton.IsEnabled =
            InsuranceButton.IsEnabled = CompareButton.IsEnabled =
            LabelButton.IsEnabled = ClientButton.IsEnabled = true;
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
        if (_report is null) return;
        ResultsList.ItemsSource = _report.Results
            .Select(kv => new ResultRow
            {
                Key = kv.Key,
                Status = kv.Value.Status,
                Display = $"{kv.Value.Value ?? ""} {kv.Value.Detail ?? ""}".Trim(),
            })
            .OrderBy(r => r.Key).ToList();
    }
}
