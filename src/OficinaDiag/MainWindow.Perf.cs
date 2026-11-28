using System.Windows;
using OficinaDiag.Devices;
using OficinaDiag.Tests;
using QRCoder;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    /// <summary>
    /// PERFIL — sessão de desempenho/bateria antes/depois (Fase C). O
    /// TestServer serve a página de stress (WebGL/Canvas) e recebe o
    /// resultado; o PerfWindow amostra bateria/temperatura/corrente/frequência
    /// e conta as quedas USB na janela. Os checks perf.* ficam no relatório
    /// e viajam no antes/depois como qualquer outra medição manual.
    /// </summary>
    private async void Perf_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            Log(L10n.T("log.nodev.usb"));
            return;
        }
        var d = _current;
        // O perfil escreve no relatório aberto — se ele descreve outro
        // aparelho, parar ANTES da sessão (mesma regra da bancada).
        if (_report is not null && !DeviceIds.Same(_reportDeviceId, d.Id))
        {
            Log(L10n.T("log.test.mismatch"));
            return;
        }

        await _tunnel.ClearAsync(); // túnel da sessão anterior não fica pendurado
        _testServer?.Dispose(); // sessão de teste/perfil anterior abandonada
        var server = _testServer = new TestServer(TestSession.PerfProfile);
        if (!server.Start())
        {
            _testServer = null;
            server.Dispose();
            Log(L10n.F("log.test.fail", L10n.T("err.perf.htmlmissing")));
            return;
        }

        var testedId = d.Id;
        // Stats da página — escritos no handler do resultado (dispatcher),
        // lidos pela janela na análise. null até ao POST perf-result.
        PerfPageStats? pageStats = null;
        // Sinal de alinhamento: a página fez GET perf-started — o stress
        // on-device começou AGORA e o host pode começar a amostrar.
        var pageStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        server.PerfStarted += () => pageStarted.TrySetResult();
        // O ACK ao telemól só sai se o aparelho em teste continuar a ser o
        // ativo — trocar de telefone a meio não pode entregar stats de um
        // perfil que já não descreve o relatório.
        server.AcceptResult = _ => _current?.Id == testedId;
        server.ResultReceived += data => Dispatcher.InvokeAsync(() =>
        {
            try
            {
                pageStats = PerfPageStats.FromJson(data);
            }
            finally
            {
                // O stress acabou — o túnel e a porta fecham como no teste
                // de ecrã, mesmo que o payload seja inesperado.
                _ = Task.Run(async () =>
                {
                    await _tunnel.ClearAsync();
                    server.Dispose();
                    if (ReferenceEquals(_testServer, server)) _testServer = null;
                });
            }
        });

        if (server.IsLoopbackOnly)
            Log(L10n.T("log.test.nolan"));
        // SafeUrl: o log pode ir para a cloud — o token da sessão é a
        // autorização ativa da página e não pode ficar gravado em claro.
        Log(L10n.F("log.perf.url", server.SafeUrl));

        // probe + zonas térmicas + frequência de CPU — três adb por tick a
        // 2 s de cadência é barato; iOS fica só com a bateria (o resto skipped).
        Func<Task<LiveTelemetry?>> probe;
        Func<Task>? loadOn = null, loadOff = null;
        if (d.Kind == DeviceKind.Android)
        {
            var adb = new AndroidCollector(_adbPath);
            var serial = d.Id.TrimEnd('!');
            // Estado de ecrã lido ANTES de o perfil mexer — no fim repõe-se
            // o que era (brilho manual/auto, nível, stay-awake), nunca se
            // escrevem valores fixos que o aparelho não tinha.
            AndroidCollector.ScreenState? screen = null;
            probe = async () =>
            {
                var t = await adb.ProbeAsync(serial);
                if (t is null) return null;
                var soc = await adb.ProbeSocTempAsync(serial);
                var freq = await adb.ProbeCpuFreqAsync(serial);
                return t with { SocTempC = soc, CpuFreqMhz = freq };
            };
            loadOn = async () =>
            {
                screen = await adb.ProbeScreenStateAsync(serial);
                await adb.ShellAsync(serial,
                    "input keyevent KEYCODE_WAKEUP; svc power stayon true; " +
                    "settings put system screen_brightness_mode 0; " +
                    "settings put system screen_brightness 255");
            };
            loadOff = async () =>
            {
                // Se a leitura inicial falhou não se inventam valores: fica o
                // comportamento anterior (stayon off + ecrã a dormir).
                if (screen is { } st)
                    await adb.RestoreScreenStateAsync(serial, st);
                else
                    await adb.ShellAsync(serial,
                        "svc power stayon false; input keyevent KEYCODE_SLEEP");
            };
        }
        else
        {
            probe = () => new IosCollector().ProbeAsync(d.Id);
        }

        // Abre a página de stress no telemóvel — Android por adb reverse
        // (contexto seguro, sem Wi-Fi) com fallback LAN; iOS por QR.
        async Task OpenPerfPage(int secs)
        {
            var suffix = $"?secs={secs}";
            if (d.Kind == DeviceKind.Android)
            {
                var serial = d.Id.TrimEnd('!');
                var loop = server.LoopbackUrl + suffix;
                if (await RunAdbAsync(serial, $"reverse tcp:{server.Port} tcp:{server.Port}"))
                {
                    _tunnel.Mount(serial, server.Port);
                    await RunAdbAsync(serial, $"shell am start -a android.intent.action.VIEW -d {loop}");
                    Log(L10n.T("log.test.reverse"));
                }
                else
                {
                    await RunAdbAsync(serial,
                        $"shell am start -a android.intent.action.VIEW -d {server.LanUrl + suffix}");
                }
                Log(L10n.T("log.perf.page"));
            }
            else
            {
                var gen = new QRCodeGenerator();
                var qr = gen.CreateQrCode(server.LanUrl + suffix, QRCodeGenerator.ECCLevel.M);
                new QrWindow(qr).Show();
                Log(L10n.T("log.test.qr"));
            }
        }

        _busy = true; // ticker pausa durante a sessão
        // Quedas USB na janela do perfil — mesmo contador do SCAN+.
        var drops = new UsbDropCounter(d.Id);
        void OnDrop(DetectedDevice dd) => drops.OnDeviceDetached(dd.Id);
        _detector!.Detached += OnDrop;
        var win = new PerfPanel(d.Label, probe, OpenPerfPage,
            () => pageStats, pageStarted.Task, loadOn, loadOff,
            drops.Begin, drops.End, () => drops.Count);
        win.CloseRequested += (_, _) =>
        {
            CloseInteractive();
            _busy = false;
            _detector!.Detached -= OnDrop;
            var res = win.Result;
            if (!win.SavedAndDone || res is null) return;
            if (_report is not null && !DeviceIds.Same(_reportDeviceId, d.Id))
            {
                Log(L10n.T("log.test.wrongdev"));
                return;
            }
            if (_report is null)
            {
                _report = new DeviceReport
                {
                    Platform = d.Kind == DeviceKind.Android ? "android" : "ios",
                    Device = { Serial = d.Id },
                };
                _reportDeviceId = d.Id;
            }
            res.ApplyTo(_report);
            ApplyGrade();
            SyncReportButtons();
            Log(L10n.F("log.perf.saved", res.SampleCount, res.DurationSec));
        };
        OpenInteractive(win);
    }
}
