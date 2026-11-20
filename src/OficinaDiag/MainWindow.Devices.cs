using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OficinaDiag.Devices;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    /// <summary>
    /// adb assíncrono com timeout — o telefone pode estar a dormir e três
    /// chamadas encadeadas bloqueavam a UI até ~10 s.
    /// </summary>
    private async Task<bool> RunAdbAsync(string serial, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(_adbPath, $"-s {serial} {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // ConfigureAwait(false): o cleanup no fecho espera de forma síncrona
            // por este task — retomar no dispatcher bloqueado daria deadlock.
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Aviso do AdbTunnel quando o remove falha: o túnel pode continuar
    /// montado e o telefone fica a chegar a 127.0.0.1 sem ninguém à escuta.
    /// No OnClosing a UI está bloqueada à espera deste task — despachar
    /// para o dispatcher daria deadlock, por isso fora da UI o aviso vai
    /// só para o diag.log.
    /// </summary>
    private void WarnTunnelLeft(string msg)
    {
        if (Dispatcher.CheckAccess()) Log(msg);
        else OficinaDiag.Log.AppLog.Write(msg);
    }

    /// <summary>
    /// Limpeza síncrona e limitada para os sítios onde não se pode esperar
    /// (handler do resultado, fecho da app). Fire-and-forget deixava o
    /// `adb reverse` montado no telefone quando o processo saía primeiro.
    /// </summary>
    private void ClearReverseBlocking()
    {
        try { _tunnel.ClearAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* adb morto/timeout — nada a fazer */ }
    }

    /// <summary>Texto do header por estado — recovery/fastboot não são "detetado".</summary>
    private static string StatusFor(Devices.DetectedDevice d) => d.State switch
    {
        "unauthorized" => L10n.F("status.unauthorized", d.Label),
        "recovery" => L10n.F("status.recovery", d.Label),
        "sideload" => L10n.F("status.sideload", d.Label),
        "fastboot" => L10n.F("status.fastboot", d.Label),
        _ => L10n.F("status.detected", d.Label),
    };

    private void AttachDevice(Devices.DetectedDevice d)
    {
        var existing = _devices.FirstOrDefault(x => x.Id == d.Id);
        if (existing is not null) _devices[_devices.IndexOf(existing)] = d; // label/estado atualiza
        else _devices.Add(d);
        SyncSelector();
        // Um segundo telemóvel ligado não rouba a bancada de quem lá está.
        if (_current is null) SelectDevice(d, announce: false);
    }

    private void DetachDevice(Devices.DetectedDevice d)
    {
        var existing = _devices.FirstOrDefault(x => x.Id == d.Id);
        if (existing is not null) _devices.Remove(existing);
        SyncSelector();
        // Só o aparelho ativo manda no estado.
        if (_current?.Id != d.Id) return;
        // Fica outro ligado? Passa-se para ele em vez de deixar a bancada morta.
        var next = _devices.FirstOrDefault(x => x.State == "ready") ?? _devices.FirstOrDefault();
        if (next is not null) SelectDevice(next, announce: true);
        else SelectDevice(null, announce: false);
    }

    private void DeviceSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceSelector.SelectedItem is not Devices.DetectedDevice d) return;
        if (_current?.Id == d.Id) return;
        SelectDevice(d, announce: true);
    }

    /// <summary>O seletor só aparece quando há mais do que um aparelho.</summary>
    private void SyncSelector()
    {
        DeviceSelector.Visibility = _devices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_current is not null)
            DeviceSelector.SelectedItem = _devices.FirstOrDefault(x => x.Id == _current.Id);
    }

    /// <summary>Aplica o dispositivo ativo e o que ele permite fazer.</summary>
    private void SelectDevice(Devices.DetectedDevice? d, bool announce)
    {
        _current = d;
        if (announce && d is not null) Log(L10n.F("log.devswitch", d.Label));
        if (d is null)
        {
            StatusText.Text = L10n.T("status.watching");
            TelemetryText.Text = "";
            _telemetryCts?.Cancel();
            TestButton.IsEnabled = false;
            BenchButton.IsEnabled = false;
            ScanButton.IsEnabled = true; // permite tentar mesmo sem evento
            return;
        }

        StatusText.Text = StatusFor(d);
        // Sem OS completo (recovery/sideload/bootloader/sem autorização) não há
        // scan, teste no ecrã nem bancada de potência — o resto continua vivo.
        var ready = d.State == "ready";
        ScanButton.IsEnabled = ready;
        TestButton.IsEnabled = ready;
        BenchButton.IsEnabled = ready;
        if (ready) StartTelemetry(d);
        else { _telemetryCts?.Cancel(); TelemetryText.Text = ""; }
    }

    /// <summary>
    /// Poll de telemetria a cada 4s enquanto o dispositivo está ligado —
    /// pausa durante scan/testes para não colidir com as leituras do coletor.
    /// </summary>
    private void StartTelemetry(DetectedDevice d)
    {
        _telemetryCts?.Cancel();
        var cts = _telemetryCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (_current?.Id == d.Id && !_busy)
                    {
                        var t = d.Kind == DeviceKind.Android
                            ? await new AndroidCollector(_adbPath).ProbeAsync(d.Id.TrimEnd('!'))
                            : await new IosCollector().ProbeAsync(d.Id);
                        var text = FormatTelemetry(t);
                        Dispatcher.Invoke(() => TelemetryText.Text = text);
                    }
                }
                catch { }
                try { await Task.Delay(4000, cts.Token); } catch { return; }
            }
        });
    }

    private static string FormatTelemetry(LiveTelemetry? t)
    {
        if (t is null) return "";
        var parts = new List<string>();
        if (t.Milliamps is { } mA)
            parts.Add($"{mA}mA{(t.Charging == true ? "↑" : t.Charging == false ? "↓" : "")}");
        if (t.Volts is { } v) parts.Add($"{v:0.00}V");
        if (t.TempC is { } c) parts.Add($"{c:0.#}°C");
        if (t.Percent is { } p) parts.Add($"batt {p}%");
        return string.Join(" · ", parts);
    }
}
