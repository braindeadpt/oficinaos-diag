using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

public partial class MainWindow
{
    private void Apps_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { Kind: DeviceKind.Android, State: "ready" })
        {
            Log(L10n.T("log.nodev.usb"));
            return;
        }
        var d = _current;
        var serial = d.Id.TrimEnd('!');
        AppsButton.IsEnabled = false;
        try
        {
            var manager = new AndroidAppManager(_adbPath);
            var dlg = new AppManagerDialog(manager, serial, d.Label) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            Log(L10n.F("log.err", ex.Message));
        }
        finally
        {
            AppsButton.IsEnabled = _current is { Kind: DeviceKind.Android, State: "ready" };
        }
    }
}
