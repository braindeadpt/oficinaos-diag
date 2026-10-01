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
    private readonly Cloud.CloudClient _cloud = new();
    private DetectedDevice? _current;
    private DeviceReport? _report;

    public MainWindow()
    {
        InitializeComponent();
        Resources["StatusTag"] = new StatusTagConverter();
        Resources["StatusBrush"] = new StatusBrushConverter();
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
    }

    private void Log(string line)
    {
        Console.AppendText(line + Environment.NewLine);
        Console.ScrollToEnd();
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
        var code = Microsoft.VisualBasic.Interaction.InputBox(
            "Código da loja (ex.: LOJA-X7K2):", "Enviar à loja — PRO", "");
        if (string.IsNullOrWhiteSpace(code)) return;
        var (ok, msg) = await _cloud.SendToShopAsync(code, _report);
        Log(ok ? $"> {msg}" : $"! envio falhou: {msg}");
    }

    private async void Ai_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var token = Microsoft.VisualBasic.Interaction.InputBox(
            "Token da loja (oficinaos-cloud):", "Relatório IA — PRO", "");
        if (string.IsNullOrWhiteSpace(token)) return;
        Log("> a gerar relatório IA…");
        var (ok, text) = await _cloud.GenerateAiReportAsync(token, _report);
        if (!ok) { Log($"! IA falhou: {text}"); return; }
        var dlg = new SaveFileDialog { Filter = "Relatório IA|*.md", FileName = $"ai-report-{_report.CollectedAt:yyyyMMdd-HHmm}.md" };
        if (dlg.ShowDialog() == true) { File.WriteAllText(dlg.FileName, text); Log($"> relatório IA guardado: {dlg.FileName}"); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _detector?.Dispose();
        base.OnClosing(e);
    }
}
