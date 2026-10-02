using System.ComponentModel;
using System.Globalization;
using System.IO;
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

    public MainWindow()
    {
        _cloud = new Cloud.CloudClient(_config);
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
        });
        _detector.Detached += d => Dispatcher.Invoke(() =>
        {
            Log($"> dispositivo removido: {d.Label}");
            if (_current?.Id == d.Id) _current = null;
            StatusText.Text = "a vigiar USB…";
            TestButton.IsEnabled = false;
        });
        ScanButton.IsEnabled = true; // permite tentar mesmo sem evento
        _ = CheckForUpdateAsync();
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
            ExportButton.IsEnabled = TestButton.IsEnabled = SendButton.IsEnabled = AiButton.IsEnabled = true;
        }
        catch (Exception ex) { Log($"! erro: {ex.Message}"); }
        finally { ScanButton.IsEnabled = true; }
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
                _report?.Set("test.screen", "pass", "cores ok",
                    "utilizador confirmou as cores — dead pixels seriam visíveis");
                Log("> resultados do teste de ecrã recebidos do telefone");
                Scan_Click(sender, e: null!); // refresh visual
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
        File.WriteAllText(dlg.FileName,
            dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ReportBuilder.ToJson(_report)
                : ReportBuilder.ToHtml(_report));
        Log($"> relatório guardado: {dlg.FileName}");
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
        var dlg = new SaveFileDialog { Filter = "Relatório IA|*.md", FileName = $"ai-report-{_report.CollectedAt:yyyyMMdd-HHmm}.md" };
        if (dlg.ShowDialog() == true) { File.WriteAllText(dlg.FileName, text); Log($"> relatório IA guardado: {dlg.FileName}"); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _detector?.Dispose();
        base.OnClosing(e);
    }
}
