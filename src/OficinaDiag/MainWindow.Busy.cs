using System.Windows;
using System.Windows.Controls;

namespace OficinaDiag;

public partial class MainWindow
{
    private CancellationTokenSource? _opCts;

    private void BeginBusy(string text, bool cancellable)
    {
        _busy = true;
        _opCts = new CancellationTokenSource();
        BusyText.Text = text;
        BusyCancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.Visibility = Visibility.Visible;

        foreach (var b in BusyBlockedButtons()) b.IsEnabled = false;
    }

    private void EndBusy()
    {
        if (BusyBar.Visibility != Visibility.Visible) return;
        BusyBar.Visibility = Visibility.Collapsed;
        BusyText.Text = "";
        BusyCancelButton.Visibility = Visibility.Collapsed;
        _opCts?.Dispose();
        _opCts = null;
        _busy = false;

        SelectDevice(_current, announce: false);
        ChecklistButton.IsEnabled = ServiceButton.IsEnabled = true;
        SyncReportButtons();
    }

    private void BusyCancel_Click(object sender, RoutedEventArgs e) => _opCts?.Cancel();

    private IEnumerable<Button> BusyBlockedButtons()
    {
        yield return ScanButton;
        yield return TestButton;
        yield return BenchButton;
        yield return PerfButton;
        yield return MirrorButton;
        yield return AppsButton;
        yield return MaintenanceButton;
        yield return ChecklistButton;
        yield return ServiceButton;
        yield return CompareButton;
        yield return LabelButton;
        yield return ClientButton;
        yield return ExportButton;
        yield return SendButton;
        yield return AiButton;
        yield return MaintenanceAiButton;
        yield return InsuranceButton;
    }
}
