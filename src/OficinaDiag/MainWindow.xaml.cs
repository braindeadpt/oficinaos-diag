using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
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
    private Tests.TestServer? _testServer;
    private readonly ScanHistory _history = new();
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
        if (!File.Exists(_adbPath))
            Log(L10n.T("log.adbmissing"));
        _detector = new DeviceDetector(_adbPath);
        _detector.Attached += d => Dispatcher.Invoke(() =>
        {
            Log(L10n.F("log.attached", d.Label, d.Id));
            _current = d;
            StatusText.Text = L10n.F("status.detected", d.Label);
            ScanButton.IsEnabled = true;
            BenchButton.IsEnabled = true;
            StartTelemetry(d);
        });
        _detector.Detached += d => Dispatcher.Invoke(() =>
        {
            Log(L10n.F("log.detached", d.Label));
            if (_current?.Id == d.Id) _current = null;
            StatusText.Text = L10n.T("status.watching");
            TelemetryText.Text = "";
            _telemetryCts?.Cancel();
            TestButton.IsEnabled = false;
            BenchButton.IsEnabled = false;
        });
        ScanButton.IsEnabled = true; // permite tentar mesmo sem evento
        _ = CheckForUpdateAsync();
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
        var (ok, msg) = await Cloud.UpdateChecker.DownloadAndStageAsync(
            _update, AppContext.BaseDirectory);
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
        StatusText.Text = _current is { } d
            ? L10n.F("status.detected", d.Label)
            : L10n.T("status.watching");
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
            FileName = $"cliente-{_report.Device.Serial ?? "device"}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
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
            _testServer?.Dispose(); // sessão anterior abandonada — liberta a porta
            var server = _testServer = new TestServer();
            if (!server.Start())
            {
                _testServer = null;
                server.Dispose();
                Log(L10n.F("log.test.fail", "device-test.html não encontrado"));
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
                server.Dispose();
                if (ReferenceEquals(_testServer, server)) _testServer = null;
            });

            var url = server.LanUrl;
            Log(L10n.F("log.test.url", url));
            if (_current?.Kind == DeviceKind.Android)
            {
                Process.Start(new ProcessStartInfo(_adbPath,
                    $"-s {_current.Id.TrimEnd('!')} shell am start -a android.intent.action.VIEW -d {url}")
                { CreateNoWindow = true });
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
            probe = () => adb.ProbeAsync(serial);
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
        bool saved;
        Devices.PowerSessionResult? res;
        try
        {
            var bench = new BenchWindow(d.Label, probe, loadOn, loadOff) { Owner = this };
            saved = bench.ShowDialog() == true;
            res = bench.Result;
        }
        finally { _busy = false; }

        if (!saved || res is null) return;
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = d.Kind == DeviceKind.Android ? "android" : "ios",
                Device = { Serial = d.Id },
            };
        }
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
            FileName = $"diag-{_report.Device.Serial ?? "device"}-{_report.CollectedAt:yyyyMMdd-HHmm}",
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
            FileName = $"seguradora-{_report.Device.Serial ?? "device"}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
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
        var dlg = new SendDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Log(L10n.F("log.send.start", dlg.ShopCode));
        SendButton.IsEnabled = false; // double-submit criava pedidos duplicados na loja
        if (dlg.Purpose == "sale") _report.Purpose = "sale";
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
        var dlg = new SettingsDialog(_config) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _config.Save();
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
        string lang;
        if (token is not null)
        {
            lang = "pt";
        }
        else
        {
            var tdlg = new TokenDialog { Owner = this };
            if (tdlg.ShowDialog() != true) return;
            token = tdlg.Token;
            lang = tdlg.Lang;
            if (tdlg.Remember)
            {
                _config.SetShopToken(token);
                _config.Save();
            }
        }
        AiButton.IsEnabled = false; // relatório IA é pago — sem double-submit
        try
        {
            Log(L10n.T("log.ai.gen"));
            var (ok, text) = await _cloud.GenerateAiReportAsync(token, _report, lang);
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
        _testServer?.Dispose();
        _detector?.Dispose();
        _telemetryCts?.Cancel();
        base.OnClosing(e);
    }
}
