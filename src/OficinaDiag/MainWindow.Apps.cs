using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

public partial class MainWindow
{
    private void Apps_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { State: "ready" }
            || _current.Kind is not (DeviceKind.Android or DeviceKind.Ios))
        {
            Log(L10n.T("log.nodev.usb"));
            return;
        }
        var d = _current;
        AppsButton.IsEnabled = false;
        try
        {
            Window dlg = d.Kind == DeviceKind.Android
                ? new AppManagerDialog(new AndroidAppManager(_adbPath),
                    d.Id.TrimEnd('!'), d.Label) { Owner = this }
                : new IosAppDialog(new IosAppManager(), d.Id, d.Label) { Owner = this };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            Log(L10n.F("log.err", ex.Message));
        }
        finally
        {
            AppsButton.IsEnabled = _current is { State: "ready",
                Kind: DeviceKind.Android or DeviceKind.Ios };
        }
    }
}
