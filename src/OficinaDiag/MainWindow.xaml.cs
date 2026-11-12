using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.History;
using OficinaDiag.Reports;
using OficinaDiag.Tests;
using QRCoder;
using System.Windows.Media.Imaging;
using System.Diagnostics;

namespace OficinaDiag;

public sealed class ResultRow
{
    public required string Key { get; init; }
    public required string Status { get; init; }
    public required string Display { get; init; }
}

public sealed class StatusTagConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        ThemeManager.IsTerminal ? $"[{v}]" : $"{v}";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        Application.Current.TryFindResource((v as string) switch
        {
            "pass" => "PassBrush",
            "warn" => "WarnBrush",
            "fail" => "FailBrush",
            _ => "DimBrush",
        }) ?? Brushes.Gray;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public partial class MainWindow : Window
{
    private readonly string _adbPath =
        Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools", "adb.exe");

    private DeviceDetector? _detector;
    // Túnel `adb reverse` ativo (serial, porta) — removido sempre no fim.
    private (string Serial, int Port)? _reverse;
    private Tests.TestServer? _testServer;
    private readonly ScanHistory _history = new();
    /// <summary>Dispositivos ligados agora — alimenta o seletor do header.</summary>
    private readonly ObservableCollection<Devices.DetectedDevice> _devices = new();
    private readonly Cloud.DiagConfig _config = Cloud.DiagConfig.Load();
    private readonly Cloud.CloudClient _cloud;
    private DetectedDevice? _current;
    private DeviceReport? _report;
    // Último relatório IA gerado para _report — viaja no envio à loja para a
    // loja o ver no pedido sem gastar outra geração.
    private string? _lastAiReport;
    // Ticker do header — leitura ambiente da bateria enquanto há dispositivo.
    private CancellationTokenSource? _telemetryCts;
    private bool _busy;

    public MainWindow()
    {
        _cloud = new Cloud.CloudClient(_config);
        InitializeComponent();
        DeviceSelector.ItemsSource = _devices;
        // RGPD: a retenção vive no DiagConfig e aplica-se logo no arranque.
        _history.RetentionDays = _config.HistoryRetentionDays;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // nunca nascer maior que a área útil do ecrã (scaling/ecrãs pequenos)
        var wa = SystemParameters.WorkArea;
        MaxWidth = wa.Width;
        MaxHeight = wa.Height;
        if (Width > wa.Width) Width = wa.Width;
        if (Height > wa.Height) Height = wa.Height;
        // CenterScreen posiciona antes do clamp — re-centra dentro da work area
        Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
        ApplyStrings();
        L10n.Changed += ApplyStrings;
        ThemeManager.Changed += ApplyStrings;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        Title = $"OFICINA-OS // DIAG v{ver}";
        Log(L10n.F("log.screen", wa.Width, wa.Height, Width, Height, Left, Top));
        Log(L10n.T("log.boot"));
        Log(L10n.T("log.tagline"));
        // Token guardado antes dos scopes foi apagado no Load() — pede ao
        // utilizador para emparelhar outra vez com o código da loja.
        if (_config.LegacyTokenWiped)
            Log(L10n.T("log.pairing.wiped"));
        if (!File.Exists(_adbPath))
            Log(L10n.T("log.adbmissing"));
        _detector = new DeviceDetector(_adbPath);
        _detector.Attached += d => Dispatcher.Invoke(() =>
        {
            Log(L10n.F("log.attached", d.Label, d.Id));
            AttachDevice(d);
        });
        _detector.Detached += d => Dispatcher.Invoke(() =>
        {
            Log(L10n.F("log.detached", d.Label));
            DetachDevice(d);
        });
        ScanButton.IsEnabled = true; // permite tentar mesmo sem evento
        // MSIX/Store não tem auto-update: nem vale a pena bater no GitHub.
        if (!Cloud.UpdateChecker.IsPackagedApp()) _ = CheckForUpdateAsync();
    }

    /// <summary>adb com timeout curto — o telefone pode estar a dormir.</summary>
    private bool RunAdb(string serial, string args)
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
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            _ = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Remove o túnel USB — tem de correr mesmo que o teste seja cancelado.</summary>
    private void ClearReverse()
    {
        if (_reverse is not { } r) return;
        _reverse = null;
        RunAdb(r.Serial, $"reverse --remove tcp:{r.Port}");
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

    private Cloud.UpdateChecker.UpdateInfo? _update;

    private async Task CheckForUpdateAsync()
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0);
        var info = await Cloud.UpdateChecker.CheckAsync(current);
        if (info is null) return;
        _update = info;
        var label = L10n.F("btn.update", info.Tag);
        UpdateButton.Content = ThemeManager.IsTerminal ? $"[ {label} ]" : label;
        UpdateButton.Visibility = Visibility.Visible;
        Log(L10n.F("log.update.avail", info.Tag));
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        UpdateButton.IsEnabled = false;
        Log(L10n.F("log.update.dl", _update.Tag));
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
            ?? new Version(0, 0, 0);
        var (ok, msg) = await Cloud.UpdateChecker.DownloadAndStageAsync(
            _update, AppContext.BaseDirectory, current);
        Log(ok ? $"> {msg}" : L10n.F("log.update.fail", msg));
        if (ok)
            Application.Current.Shutdown();
        else
            UpdateButton.IsEnabled = true;
    }

    /// <summary>Reaplica todos os textos da janela — corre no arranque e ao trocar de idioma.</summary>
    private void ApplyStrings()
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        HeaderTitle.Text = $"{(ThemeManager.IsTerminal ? "█▓▒░ " : "")}OFICINA-OS // DIAG v{ver}";
        StatusText.Text = _current is { } d ? StatusFor(d) : L10n.T("status.watching");
        ResultsHeader.Text = L10n.Section("results.header");
        ScanButton.Content = L10n.Btn("btn.scan");
        BenchButton.Content = L10n.Btn("btn.bench");
        TestButton.Content = L10n.Btn("btn.test");
        ChecklistButton.Content = L10n.Btn("btn.checklist");
        CompareButton.Content = L10n.Btn("btn.compare");
        LabelButton.Content = L10n.Btn("btn.label");
        ClientButton.Content = L10n.Btn("btn.client");
        ExportButton.Content = L10n.Btn("btn.export");
        SendButton.Content = L10n.Btn("btn.send");
        AiButton.Content = L10n.Btn("btn.ai");
        InsuranceButton.Content = L10n.Btn("btn.insurance");
        LogButton.Content = L10n.Btn("btn.log");
        ConfigButton.Content = L10n.Btn("btn.settings");
        // Tema novo = brushes novas: as linhas da lista foram pintadas com as
        // antigas (verde néon sobre fundo branco no tema Fluent).
        RefreshResults();
        if (_update is not null)
            UpdateButton.Content = ThemeManager.IsTerminal
                ? $"[ {L10n.F("btn.update", _update.Tag)} ]"
                : L10n.F("btn.update", _update.Tag);
    }

    private void Log(string line)
    {
        Console.AppendText(line + Environment.NewLine);
        Console.ScrollToEnd();
        OficinaDiag.Log.AppLog.Write(line);
    }

    /// <summary>Erros vindos do handler global (App) — visíveis na consola da app.</summary>
    internal void LogError(string where, Exception ex) =>
        Log($"! {where}: {ex.GetType().Name}: {ex.Message}");

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
            _report = _current.Kind == DeviceKind.Android
                ? await new AndroidCollector(_adbPath).CollectAsync(_current.Id.TrimEnd('!'))
                : await new IosCollector().CollectAsync(_current.Id);

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
        _report ??= new DeviceReport
        {
            Platform = _current?.Kind == DeviceKind.Ios ? "ios" : "android",
            Device = { Serial = _current?.Id?.TrimEnd('!') },
        };
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

    /// <summary>Etiqueta de balcão — modelo, serial, grau e QR; cola no saco do aparelho.</summary>
    private void Label_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        try
        {
            Log(LabelPrinter.Print(_report)
                ? L10n.T("log.label.printed")
                : L10n.T("log.label.cancel"));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
    }

    /// <summary>Vista cliente — linguagem simples, grau em destaque, sem chaves técnicas.</summary>
    private void Client_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = L10n.T("dlg.client.filter"),
            FileName = $"cliente-{Reports.ReportBuilder.SafeFileToken(_report.Device.Serial)}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ReportBuilder.ToClientHtml(_report));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.client.saved", dlg.FileName));
        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ClearReverse(); // túnel da sessão anterior não fica pendurado no telefone
            _testServer?.Dispose(); // sessão anterior abandonada — liberta a porta
            var server = _testServer = new TestServer();
            if (!server.Start())
            {
                _testServer = null;
                server.Dispose();
                Log(L10n.F("log.test.fail", L10n.T("err.test.htmlmissing")));
                return;
            }
            server.ResultReceived += data => Dispatcher.Invoke(() =>
            {
                if (_report is null) return;
                if (data.TryGetValue("touchHit", out var hit) && data.TryGetValue("touchTotal", out var tot))
                {
                    var h = hit.GetInt32(); var t = tot.GetInt32();
                    _report.Set("test.touch", h >= t - 5 ? "pass" : "fail",
                        L10n.F("test.touch.zones", h, t), L10n.T("test.touch.detail"));
                }
                _report.Set("test.screen", "pass", L10n.T("test.screen.val"),
                    L10n.T("test.screen.detail"));

                // Bordas: falha de toque nas margens é o sintoma clássico de
                // ecrã aftermarket / mal ligado — contado à parte do interior.
                if (data.TryGetValue("edgeHit", out var eh) && data.TryGetValue("edgeTotal", out var et))
                {
                    var eH = eh.GetInt32(); var eT = et.GetInt32();
                    if (eT > 0)
                        _report.Set("test.edges", eH >= eT ? "pass" : "warn",
                            L10n.F("test.edges.val", eH, eT),
                            eH >= eT ? L10n.T("test.edges.ok")
                                     : L10n.T("test.edges.fail"));
                }

                // Multi-toque real (dedos simultâneos vistos na fase 3) —
                // mais informativo que o maxTouchPoints anunciado.
                if (data.TryGetValue("multiTouch", out var mt) && mt.GetInt32() > 0)
                    _report.Set("sensor.multitouch",
                        mt.GetInt32() >= 5 ? "pass" : "warn",
                        L10n.F("test.multitouch.val", mt.GetInt32()),
                        L10n.T("test.multitouch.detail"));

                // Altifalante — confirmação humana do tom WebAudio.
                if (data.TryGetValue("speaker", out var spk) && spk.ValueKind == JsonValueKind.String)
                {
                    var s = spk.GetString();
                    _report.Set("sensor.speaker",
                        s == "ok" ? "pass" : s == "fail" ? "fail" : "skipped",
                        s == "ok" ? L10n.T("test.speaker.ok")
                            : s == "fail" ? L10n.T("test.speaker.fail")
                            : L10n.T("test.speaker.skip"),
                        L10n.T("test.speaker.detail"));
                }

                // Sensores — a página mede por browser APIs; "no-data" significa
                // que o browser não respondeu, não que o hardware falhou.
                if (data.TryGetValue("sensors", out var sensors)
                    && sensors.ValueKind == JsonValueKind.Object)
                {
                    var names = new (string Key, string Label)[]
                    {
                        ("accel", L10n.T("sensor.accel")), ("gyro", L10n.T("sensor.gyro")),
                        ("orient", L10n.T("sensor.orient")), ("light", L10n.T("sensor.light")),
                    };
                    foreach (var (key, label) in names)
                    {
                        if (!sensors.TryGetProperty(key, out var s)) continue;
                        var state = s.GetString();
                        if (state is null) continue;
                        _report.Set($"sensor.{key}",
                            state == "ok" ? "pass" : state == "no-data" ? "warn" : "skipped",
                            state == "ok" ? L10n.T("sensor.responding")
                                : state == "no-data" ? L10n.T("sensor.nodata")
                                : L10n.T("sensor.noapi"),
                            state == "no-data"
                                ? L10n.F("sensor.nodata.detail", label)
                                : L10n.F("sensor.reading.detail", label));
                    }

                    // Auricular e microfone: "fail" é defeito, "skip" não
                    // (microfone bloqueado em HTTP LAN não é hardware).
                    void MapAudio(string key, string okKey, string failKey, string detailKey)
                    {
                        if (!sensors.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
                            return;
                        var st = v.GetString();
                        _report.Set($"sensor.{key}",
                            st == "ok" ? "pass" : st == "fail" ? "fail" : "skipped",
                            st == "ok" ? L10n.T(okKey)
                                : st == "fail" ? L10n.T(failKey) : L10n.T("test.mic.skip"),
                            L10n.T(detailKey));
                    }
                    MapAudio("earpiece", "test.earpiece.ok", "test.earpiece.fail", "test.earpiece.detail");
                    MapAudio("mic", "test.mic.ok", "test.mic.fail", "test.mic.detail");
                }
                if (data.TryGetValue("vibrate", out var vib) && vib.ValueKind == JsonValueKind.String)
                {
                    var v = vib.GetString();
                    if (v == "ok")
                        _report.Set("sensor.vibrate", "pass", L10n.T("sensor.vibrate.ok"),
                            L10n.T("sensor.vibrate.ok.detail"));
                    else if (v == "no-data")
                        _report.Set("sensor.vibrate", "fail", L10n.T("sensor.vibrate.fail"),
                            L10n.T("sensor.vibrate.fail.detail"));
                }
                Log(L10n.T("log.test.received"));
                ApplyGrade();
                ClearReverse(); // o telefone não precisa mais do túnel
                server.Dispose();
                if (ReferenceEquals(_testServer, server)) _testServer = null;
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
                if (RunAdb(serial, $"reverse tcp:{server.Port} tcp:{server.Port}"))
                {
                    _reverse = (serial, server.Port);
                    RunAdb(serial, $"shell am start -a android.intent.action.VIEW -d {loop}");
                    Log(L10n.T("log.test.reverse"));
                }
                else
                {
                    // adb sem reverse: fica o caminho LAN (Wi-Fi) como antes.
                    RunAdb(serial, $"shell am start -a android.intent.action.VIEW -d {url}");
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
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = d.Kind == DeviceKind.Android ? "android" : "ios",
                Device = { Serial = d.Id },
            };
        }
        res.UsbDrops = drops;
        res.ApplyTo(_report);
        ApplyGrade();
        ExportButton.IsEnabled = SendButton.IsEnabled = AiButton.IsEnabled =
            InsuranceButton.IsEnabled = CompareButton.IsEnabled =
            LabelButton.IsEnabled = ClientButton.IsEnabled = true;
        Log(L10n.F("log.bench.saved", res.ChargeValue, res.SampleCount));
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

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = L10n.T("dlg.export.filter"),
            FileName = $"diag-{Reports.ReportBuilder.SafeFileToken(_report.Device.Serial)}-{_report.CollectedAt:yyyyMMdd-HHmm}",
        };
        if (dlg.ShowDialog() != true) return;
        var isHtml = !dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        try
        {
            File.WriteAllText(dlg.FileName,
                isHtml ? ReportBuilder.ToHtml(_report) : ReportBuilder.ToJson(_report));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.export.saved", dlg.FileName));
        // HTML é para ver/partilhar — abre já no browser predefinido.
        if (isHtml)
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    /// <summary>Relatório formal A4 para seguradora — dano + estado + carimbo da loja.</summary>
    private void Insurance_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new InsuranceDialog(_config) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Form is not { } form) return;
        var save = new SaveFileDialog
        {
            Filter = L10n.T("dlg.ins.filter"),
            FileName = $"seguradora-{Reports.ReportBuilder.SafeFileToken(_report.Device.Serial)}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
        };
        if (save.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(save.FileName, ReportBuilder.ToInsuranceHtml(_report, form));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.ins.saved", save.FileName));
        // Abre já no browser — daí é imprimir ou gravar em PDF.
        Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true });
    }

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
                _lastAiReport, dlg.Purpose);
            Log(ok ? $"> {msg}" : L10n.F("log.send.fail", msg));
        }
        finally { SendButton.IsEnabled = true; }
    }

    private async void LogSend_Click(object sender, RoutedEventArgs e)
    {
        const int maxChars = 150_000; // servidor aceita até 200k — enviamos só a cauda
        try
        {
            var path = OficinaDiag.Log.AppLog.FilePath;
            if (!File.Exists(path)) { Log(L10n.T("log.log.none")); return; }
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var skip = Math.Max(0, fs.Length - maxChars);
                fs.Seek(skip, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                tail = await sr.ReadToEndAsync();
                if (skip > 0) tail = L10n.T("log.log.truncated") + "\n" + tail;
            }
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

    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        // Aviso de privacidade: crash logs (~40KB com paths e usernames) vão
        // para o LLM via cloud — o técnico confirma antes de enviar.
        if (MessageBox.Show(this, L10n.T("dlg.ai.privacy"),
                L10n.T("dlg.ai.token.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;
        var token = _config.GetShopToken();
        AiButton.IsEnabled = false; // relatório IA é pago — sem double-submit
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
                }
            }
            Log(L10n.T("log.ai.gen"));
            var (ok, text) = await _cloud.GenerateAiReportAsync(token!, _report, lang);
            if (!ok) { Log(L10n.F("log.ai.fail", text)); return; }
            _lastAiReport = text;
            // Guarda em HTML renderizado (entregável ao cliente) e abre no browser.
            var dlg = new SaveFileDialog { Filter = L10n.T("dlg.ai.filter"), FileName = $"ai-report-{_report.CollectedAt:yyyyMMdd-HHmm}.html" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    File.WriteAllText(dlg.FileName, ReportBuilder.AiReportToHtml(_report, text));
                }
                catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
                Log(L10n.F("log.ai.saved", dlg.FileName));
                Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
            }
        }
        finally { AiButton.IsEnabled = true; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Sem estas saídas o MainWindow ficava refletido em L10n/ThemeManager
        // e continuava a ser notificado depois de fechado.
        ClearReverse(); // fechar a app não deixa o túnel adb montado
        L10n.Changed -= ApplyStrings;
        ThemeManager.Changed -= ApplyStrings;
        _testServer?.Dispose();
        _detector?.Dispose();
        _telemetryCts?.Cancel();
        base.OnClosing(e);
    }
}
