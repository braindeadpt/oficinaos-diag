using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using OficinaDiag.Devices;
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
    public object Convert(object v, Type t, object p, CultureInfo c) => $"[{v}]";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => (v as string) switch
    {
        "pass" => new SolidColorBrush(Color.FromRgb(0x33, 0xff, 0x33)),
        "warn" => new SolidColorBrush(Color.FromRgb(0xff, 0xd2, 0x3f)),
        "fail" => new SolidColorBrush(Color.FromRgb(0xff, 0x55, 0x55)),
        _ => new SolidColorBrush(Color.FromRgb(0x7f, 0xd7, 0x7f)),
    };
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public partial class MainWindow : Window
{
    private readonly string _adbPath =
        Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools", "adb.exe");

    private DeviceDetector? _detector;
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
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        Title = $"OFICINA-OS // DIAG v{ver}";
        HeaderTitle.Text = $"█▓▒░ OFICINA-OS // DIAG v{ver}";
        Log($"> ecrã útil {wa.Width:0}x{wa.Height:0} · janela {Width:0}x{Height:0} @{Left:0},{Top:0}");
        Log("> boot sequence…");
        Log("> oficinaos-diag — scan USB grátis · relatórios Pro via cloud");
        if (!File.Exists(_adbPath))
            Log("! adb não encontrado em tools\\platform-tools — corre tools\\fetch-tools.ps1");
        _detector = new DeviceDetector(_adbPath);
        _detector.Attached += d => Dispatcher.Invoke(() =>
        {
            Log($"> dispositivo ligado: {d.Label} [{d.Id}]");
            _current = d;
            StatusText.Text = $"{d.Label} detetado";
            ScanButton.IsEnabled = true;
            BenchButton.IsEnabled = true;
            StartTelemetry(d);
        });
        _detector.Detached += d => Dispatcher.Invoke(() =>
        {
            Log($"> dispositivo removido: {d.Label}");
            if (_current?.Id == d.Id) _current = null;
            StatusText.Text = "a vigiar USB…";
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
        UpdateButton.Content = $"[ ↓ ATUALIZAR {info.Tag} ]";
        UpdateButton.Visibility = Visibility.Visible;
        Log($"> nova versão {info.Tag} disponível — carrega ATUALIZAR");
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        UpdateButton.IsEnabled = false;
        Log($"> a descarregar {_update.Tag}…");
        var (ok, msg) = await Cloud.UpdateChecker.DownloadAndStageAsync(
            _update, AppContext.BaseDirectory);
        Log(ok ? $"> {msg}" : $"! atualização falhou: {msg}");
        if (ok)
            Application.Current.Shutdown();
        else
            UpdateButton.IsEnabled = true;
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
        Log("> scan a correr…");
        try
        {
            if (_current is null)
            {
                Log("! nenhum dispositivo — liga um telefone por USB (ADB debug / Confiar)");
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

            _history.Add(_report);
            Log($"> scan completo: {_report.Results.Count} checks");
            ExportButton.IsEnabled = TestButton.IsEnabled = SendButton.IsEnabled =
                AiButton.IsEnabled = InsuranceButton.IsEnabled = true;
        }
        catch (Exception ex) { Log($"! erro: {ex.Message}"); }
        finally { ScanButton.IsEnabled = true; _busy = false; }
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var server = new TestServer();
            server.Start();
            server.ResultReceived += data => Dispatcher.Invoke(() =>
            {
                if (_report is null) return;
                if (data.TryGetValue("touchHit", out var hit) && data.TryGetValue("touchTotal", out var tot))
                {
                    var h = hit.GetInt32(); var t = tot.GetInt32();
                    _report.Set("test.touch", h >= t - 5 ? "pass" : "fail",
                        $"{h}/{t} zonas", "grelha de toque preenchida no telefone");
                }
                _report.Set("test.screen", "pass", "cores ok",
                    "utilizador confirmou as cores — dead pixels seriam visíveis");

                // Sensores — a página mede por browser APIs; "no-data" significa
                // que o browser não respondeu, não que o hardware falhou.
                if (data.TryGetValue("sensors", out var sensors)
                    && sensors.ValueKind == JsonValueKind.Object)
                {
                    var names = new (string Key, string Label)[]
                    {
                        ("accel", "acelerómetro"), ("gyro", "giroscópio"),
                        ("orient", "orientação"), ("light", "luz ambiente"),
                        ("multitouch", "multi-toque"),
                    };
                    foreach (var (key, label) in names)
                    {
                        if (!sensors.TryGetProperty(key, out var s)) continue;
                        var state = s.GetString();
                        if (state is null) continue;
                        _report.Set($"sensor.{key}",
                            state == "ok" ? "pass" : state == "no-data" ? "warn" : "skipped",
                            state == "ok" ? "a responder" : state == "no-data" ? "sem leitura" : "browser não expõe",
                            state == "no-data"
                                ? $"o browser não devolveu dados do {label} — re-testar ou verificar hardware"
                                : $"leitura via browser ({label})");
                    }
                }
                if (data.TryGetValue("vibrate", out var vib) && vib.ValueKind == JsonValueKind.String)
                {
                    var v = vib.GetString();
                    if (v == "ok")
                        _report.Set("sensor.vibrate", "pass", "vibrou", "utilizador confirmou vibração no telefone");
                    else if (v == "no-data")
                        _report.Set("sensor.vibrate", "fail", "não vibrou", "utilizador não sentiu vibração — verificar motor");
                }
                Log("> resultados do teste no telefone recebidos (ecrã, toque, sensores)");
                RefreshResults();
                server.Dispose();
            });

            var url = server.LanUrl;
            Log($"> teste de ecrã em {url}");
            if (_current?.Kind == DeviceKind.Android)
            {
                Process.Start(new ProcessStartInfo(_adbPath,
                    $"-s {_current.Id.TrimEnd('!')} shell am start -a android.intent.action.VIEW -d {url}")
                { CreateNoWindow = true });
                Log("> página de teste aberta no Android — segue as instruções no telefone");
            }
            else
            {
                // iPhone: QR para o cliente ler com a câmara
                var gen = new QRCodeGenerator();
                var qr = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
                new QrWindow(qr).Show();
                Log("> QR mostrado — lê com a câmara do iPhone na mesma rede Wi-Fi");
            }
        }
        catch (Exception ex) { Log($"! teste falhou: {ex.Message}"); }
    }

    /// <summary>SCAN+ — sessão guiada de potência na janela bancada.</summary>
    private void Bench_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            Log("! nenhum dispositivo — liga um telefone por USB");
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
        var bench = new BenchWindow(d.Label, probe, loadOn, loadOff) { Owner = this };
        var saved = bench.ShowDialog() == true;
        _busy = false;

        if (!saved || bench.Result is not { } res) return;
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = d.Kind == DeviceKind.Android ? "android" : "ios",
                Device = { Serial = d.Id },
            };
        }
        res.ApplyTo(_report);
        RefreshResults();
        ExportButton.IsEnabled = SendButton.IsEnabled = AiButton.IsEnabled =
            InsuranceButton.IsEnabled = true;
        Log($"> SCAN+ gravado: {res.ChargeValue} · {res.SampleCount} amostras");
    }

    private void RefreshResults()
    {
        if (_report is null) return;
        ResultsList.ItemsSource = _report.Results
            .Select(kv => new ResultRow { Key = kv.Key, Status = kv.Value.Status, Display = kv.Value.Value ?? "" })
            .OrderBy(r => r.Key).ToList();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Relatório HTML|*.html|JSON|*.json",
            FileName = $"diag-{_report.Device.Serial ?? "device"}-{_report.CollectedAt:yyyyMMdd-HHmm}",
        };
        if (dlg.ShowDialog() != true) return;
        var isHtml = !dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(dlg.FileName,
            isHtml ? ReportBuilder.ToHtml(_report) : ReportBuilder.ToJson(_report));
        Log($"> relatório guardado: {dlg.FileName}");
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
            Filter = "Relatório seguradora|*.html",
            FileName = $"seguradora-{_report.Device.Serial ?? "device"}-{_report.CollectedAt:yyyyMMdd-HHmm}.html",
        };
        if (save.ShowDialog() != true) return;
        File.WriteAllText(save.FileName, ReportBuilder.ToInsuranceHtml(_report, form));
        Log($"> relatório de seguradora guardado: {save.FileName}");
        // Abre já no browser — daí é imprimir ou gravar em PDF.
        Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true });
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SendDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Log($"> a enviar à loja {dlg.ShopCode}…");
        var (ok, msg) = await _cloud.SendToShopAsync(
            dlg.ShopCode, _report, dlg.CustomerName, dlg.CustomerPhone, dlg.CustomerEmail,
            _lastAiReport, dlg.Purpose);
        Log(ok ? $"> {msg}" : $"! envio falhou: {msg}");
    }

    private async void LogSend_Click(object sender, RoutedEventArgs e)
    {
        const int maxChars = 150_000; // servidor aceita até 200k — enviamos só a cauda
        try
        {
            var path = OficinaDiag.Log.AppLog.FilePath;
            if (!File.Exists(path)) { Log("! ainda não há log para enviar"); return; }
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var skip = Math.Max(0, fs.Length - maxChars);
                fs.Seek(skip, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                tail = await sr.ReadToEndAsync();
                if (skip > 0) tail = "[… início cortado …]\n" + tail;
            }
            var hint = _report is not null
                ? $"{_report.Platform} {_report.Device.Brand} {_report.Device.Model} {_report.Device.Os} {_report.Device.OsVersion}".Trim()
                : _current?.Label;
            LogButton.IsEnabled = false;
            Log("> a enviar log de diagnóstico à cloud…");
            var (ok, msg) = await _cloud.SendLogAsync(tail, _report?.ToolVersion ?? "0.1.0", hint);
            Log(ok ? $"> {msg}" : $"! envio do log falhou: {msg}");
        }
        catch (Exception ex) { Log($"! envio do log falhou: {ex.Message}"); }
        finally { LogButton.IsEnabled = true; }
    }

    private void Config_Click(object sender, RoutedEventArgs e)
    {
        var url = Microsoft.VisualBasic.Interaction.InputBox(
            "URL do servidor OficinaOS Cloud:", "Configuração", _config.CloudUrl);
        if (string.IsNullOrWhiteSpace(url)) return;
        _config.CloudUrl = url.Trim();
        _config.Save();
        _cloud.BaseUri = new Uri(_config.CloudUrl.TrimEnd('/') + "/");
        Log($"> cloud: {_config.CloudUrl}");
    }

    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var token = Microsoft.VisualBasic.Interaction.InputBox(
            "Token da loja (oficinaos-cloud):", "Relatório IA — PRO", "");
        if (string.IsNullOrWhiteSpace(token)) return;
        var lang = (Microsoft.VisualBasic.Interaction.InputBox(
            "Idioma do relatório (pt/en/fr/es):", "Relatório IA — PRO", "pt") ?? "pt")
            .Trim().ToLowerInvariant();
        if (lang is not ("pt" or "en" or "fr" or "es")) lang = "pt";
        Log("> a gerar relatório IA…");
        var (ok, text) = await _cloud.GenerateAiReportAsync(token, _report, lang);
        if (!ok) { Log($"! IA falhou: {text}"); return; }
        _lastAiReport = text;
        // Guarda em HTML renderizado (entregável ao cliente) e abre no browser.
        var dlg = new SaveFileDialog { Filter = "Relatório IA|*.html", FileName = $"ai-report-{_report.CollectedAt:yyyyMMdd-HHmm}.html" };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, ReportBuilder.AiReportToHtml(_report, text));
            Log($"> relatório IA guardado: {dlg.FileName}");
            Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _detector?.Dispose();
        base.OnClosing(e);
    }
}
