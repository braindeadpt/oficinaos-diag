using System.Diagnostics;
using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    // Um espelho de cada vez: dois scrcpy a competir pelo mesmo telemóvel
    // disputam o servidor adb e confundem o técnico com duas janelas.
    private bool _mirrorRunning;

    /// <summary>
    /// O botão só acorda com Android <c>ready</c> — o mesmo estado em que o
    /// adb o vê, ou seja depuração USB ativa. Sem isso o scrcpy arranca e
    /// morre com "device unauthorized", que é ruído para o técnico.
    /// </summary>
    private void SyncMirrorButton() =>
        MirrorButton.IsEnabled = !_mirrorRunning
            && _current is { Kind: DeviceKind.Android, State: "ready" };

    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        var d = _current;
        if (d?.Kind != DeviceKind.Android || d.State != "ready") return;
        var serial = d.Id.TrimEnd('!');
        var baseDir = AppContext.BaseDirectory;

        // Instalação sem tools/scrcpy (build de dev sem fetch-tools) diz-se
        // em vez de deixar o botão morto — mesmo padrão do log.adbmissing.
        if (ScrcpyMirror.FindExe(baseDir) is null)
        {
            Log(L10n.T("log.mirror.missing"));
            return;
        }

        var title = $"OFICINA-OS // DIAG — {serial}";
        var psi = ScrcpyMirror.CreateProcessInfo(baseDir, _adbPath, serial, title);
        if (psi is null) return; // serial rejeitado — o detetor só dá serials adb válidos

        Process? scrcpy;
        try { scrcpy = Process.Start(psi); }
        catch (Exception ex) { Log(L10n.F("log.mirror.fail", ex.Message)); return; }
        if (scrcpy is null)
        {
            Log(L10n.F("log.mirror.fail", "Process.Start devolveu null"));
            return;
        }

        _mirrorRunning = true;
        SyncMirrorButton();
        Log(L10n.F("log.mirror.open", serial));

        // O scrcpy vive até a janela dele fechar; a vigia em fundo repõe o
        // botão e regista uma saída antecipada (aparelho offline, depuração
        // revogada a meio) com a última linha que ele escreveu.
        var outTask = scrcpy.StandardOutput.ReadToEndAsync();
        var errTask = scrcpy.StandardError.ReadToEndAsync();
        _ = VigiarEspelhoAsync(scrcpy, outTask, errTask);
    }

    private async Task VigiarEspelhoAsync(Process scrcpy, Task<string> outTask, Task<string> errTask)
    {
        try
        {
            await scrcpy.WaitForExitAsync().ConfigureAwait(false);
            var codigo = scrcpy.ExitCode;
            var texto = await errTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(texto))
                texto = await outTask.ConfigureAwait(false);
            var linhas = texto.Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ultima = linhas.Length > 0 ? linhas[^1] : "";

            await Dispatcher.InvokeAsync(() =>
            {
                _mirrorRunning = false;
                SyncMirrorButton();
                Log(codigo == 0
                    ? L10n.T("log.mirror.closed")
                    : L10n.F("log.mirror.exiterr", codigo,
                        ultima.Length > 0 ? $" — {ultima}" : ""));
            }).Task.ConfigureAwait(false);
        }
        catch
        {
            // Janela fechada a meio ou pipe morto — o scrcpy já não diz nada
            // de útil e o botão fica como estava.
        }
    }
}
