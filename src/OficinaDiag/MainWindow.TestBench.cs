using System.Windows;
using OficinaDiag.Devices;
using OficinaDiag.Tests;
using QRCoder;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // O resultado só pode aterrar num relatório DESTE aparelho.
            // Se o aberto pertence a outro cliente, bloquear antes de ligar
            // a sessão — o telemóvel não pode completar o teste, entregar ao
            // vazio e sair a achar que foi aceite.
            if (_report is not null && _reportDeviceId != _current?.Id)
            {
                Log(L10n.T("log.test.mismatch"));
                return;
            }
            if (_report is null)
            {
                if (_current is null)
                {
                    Log(L10n.T("log.test.mismatch"));
                    return;
                }
                // Sem relatório aberto, criar um para o aparelho selecionado —
                // como na bancada: o resultado tem sempre onde aterrar.
                _report = new DeviceReport
                {
                    Platform = _current.Kind == DeviceKind.Android ? "android" : "ios",
                    Device = { Serial = _current.Id },
                };
                _reportDeviceId = _current.Id;
            }
            await _tunnel.ClearAsync(); // túnel da sessão anterior não fica pendurado
            _testServer?.Dispose(); // sessão anterior abandonada — liberta a porta
            var server = _testServer = new TestServer();
            if (!server.Start())
            {
                _testServer = null;
                server.Dispose();
                Log(L10n.F("log.test.fail", L10n.T("err.test.htmlmissing")));
                return;
            }
            // InvokeAsync + await: a UI atualiza já e a limpeza do túnel espera
            // fora do dispatcher, sem prender a janela 3 s.
            var testedId = _current?.Id;
            // O ACK ao telemóvel só sai se houver um relatório DESTE aparelho
            // a receber o resultado — sem ele a página mostrava «entregue» e
            // nada era gravado. Por identidade (não instância): um rescan do
            // mesmo aparelho durante a sessão não condena o retry a 409.
            server.AcceptResult = _ =>
                _report is not null && _reportDeviceId == testedId;
            server.ResultReceived += data => Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    // O resultado pertence ao aparelho em teste — se o relatório
                    // aberto descreve outro dispositivo, recusar em vez de
                    // contaminar o diagnóstico do cliente seguinte.
                    if (_report is null || _reportDeviceId != testedId)
                    {
                        Log(L10n.T("log.test.wrongdev"));
                        return;
                    }
                    TestResultMapper.Apply(_report, data);
                    Log(L10n.T("log.test.received"));
                    ApplyGrade();
                    // O teste pode correr antes do primeiro scan — os botões
                    // do relatório têm de acordar como no scan/bancada, senão
                    // as medições ficavam invisíveis para exportar/enviar.
                    ExportButton.IsEnabled = SendButton.IsEnabled =
                        AiButton.IsEnabled = InsuranceButton.IsEnabled =
                        CompareButton.IsEnabled = LabelButton.IsEnabled =
                        ClientButton.IsEnabled = true;
                }
                finally
                {
                    // O túnel e a porta fecham mesmo quando o resultado é
                    // recusado — antes o return saltava a limpeza.
                    await _tunnel.ClearAsync(); // o telefone não precisa mais do túnel
                    server.Dispose();
                    if (ReferenceEquals(_testServer, server)) _testServer = null;
                }
            });

            var url = server.LanUrl;
            // Sem IP de LAN o servidor ficou em loopback — o QR/URL não abre
            // no telemóvel. Avisa em vez de deixar o utilizador a ler um
            // código condenado a falhar.
            if (server.IsLoopbackOnly)
                Log(L10n.T("log.test.nolan"));
            Log(L10n.F("log.test.url", url));
            if (_current?.Kind == DeviceKind.Android)
            {
                // O cabo USB já está ligado: `adb reverse` faz o telefone chegar
                // a 127.0.0.1 sem Wi-Fi nenhum e em contexto seguro — o que
                // também destranca o teste de microfone no browser.
                var serial = _current.Id.TrimEnd('!');
                var loop = server.LoopbackUrl;
                if (await RunAdbAsync(serial, $"reverse tcp:{server.Port} tcp:{server.Port}"))
                {
                    _tunnel.Mount(serial, server.Port);
                    await RunAdbAsync(serial, $"shell am start -a android.intent.action.VIEW -d {loop}");
                    Log(L10n.T("log.test.reverse"));
                }
                else
                {
                    // adb sem reverse: fica o caminho LAN (Wi-Fi) como antes.
                    await RunAdbAsync(serial, $"shell am start -a android.intent.action.VIEW -d {url}");
                }
                Log(L10n.T("log.test.android"));
            }
            else
            {
                // iPhone: QR para o cliente ler com a câmara
                var gen = new QRCodeGenerator();
                var qr = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                new QrWindow(qr).Show();
                Log(L10n.T("log.test.qr"));
            }
        }
        catch (Exception ex) { Log(L10n.F("log.test.fail", ex.Message)); }
    }

    /// <summary>SCAN+ — sessão guiada de potência na janela bancada.</summary>
    private void Bench_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            Log(L10n.T("log.nodev.usb"));
            return;
        }
        var d = _current;
        // A sessão recolhe medições para o relatório aberto. Se ele descreve
        // outro aparelho, parar ANTES da bancada — não deixar o técnico
        // percorrer as três fases para o resultado ser descartado ao gravar.
        if (_report is not null && _reportDeviceId != d.Id)
        {
            Log(L10n.T("log.test.mismatch"));
            return;
        }
        Func<Task<LiveTelemetry?>> probe;
        Func<Task>? loadOn = null, loadOff = null;
        if (d.Kind == DeviceKind.Android)
        {
            var adb = new AndroidCollector(_adbPath);
            var serial = d.Id.TrimEnd('!');
            // probe + zonas térmicas — célula vs SoC distingue defeito de peça
            // de throttling; segundo adb por tick é barato a 2s de cadência
            probe = async () =>
            {
                var t = await adb.ProbeAsync(serial);
                if (t is null) return null;
                var soc = await adb.ProbeSocTempAsync(serial);
                return t with { SocTempC = soc };
            };
            // carga de trabalho automática: ecrã acordado + brilho máximo
            loadOn = async () => await adb.ShellAsync(serial,
                "input keyevent KEYCODE_WAKEUP; svc power stayon true; " +
                "settings put system screen_brightness_mode 0; " +
                "settings put system screen_brightness 255");
            loadOff = async () => await adb.ShellAsync(serial,
                "settings put system screen_brightness_mode 1; " +
                "svc power stayon false; input keyevent KEYCODE_SLEEP");
        }
        else
        {
            probe = () => new IosCollector().ProbeAsync(d.Id);
        }

        _busy = true; // ticker pausa durante a sessão
        // O host conta as quedas USB durante a sessão — cruz com o fuel gauge
        // distingue porta/cabo instável de bateria fraca.
        var drops = 0;
        var benchId = d.Id;
        void OnDrop(DetectedDevice dd) { if (dd.Id == benchId) drops++; }
        _detector!.Detached += OnDrop;
        bool saved;
        Devices.PowerSessionResult? res;
        try
        {
            var bench = new BenchWindow(d.Label, probe, loadOn, loadOff) { Owner = this };
            saved = bench.ShowDialog() == true;
            res = bench.Result;
        }
        finally { _busy = false; _detector!.Detached -= OnDrop; }

        if (!saved || res is null) return;
        if (_report is not null && _reportDeviceId != d.Id)
        {
            // O bench correu no aparelho d mas o relatório aberto descreve
            // outro — gravar aqui contaminava o diagnóstico desse cliente.
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
        res.UsbDrops = drops;
        res.ApplyTo(_report);
        ApplyGrade();
        ExportButton.IsEnabled = SendButton.IsEnabled = AiButton.IsEnabled =
            InsuranceButton.IsEnabled = CompareButton.IsEnabled =
            LabelButton.IsEnabled = ClientButton.IsEnabled = true;
        Log(L10n.F("log.bench.saved", res.ChargeValue, res.SampleCount));
    }
}
