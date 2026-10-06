using System.Diagnostics;
using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

/// <summary>
/// SCAN+ — sessão guiada de potência (~60s), cabo de dados sempre ligado:
/// 1) base 15s → 2) carga de trabalho 20s (ecrã+brilho máximo — ΔV/ΔI →
/// resistência interna) → 3) recuperação 25s.
/// Em Android a carga é aplicada por ADB; em iOS é instrução manual.
/// </summary>
public sealed partial class BenchWindow : Window
{
    private const int BaseSec = 15;
    private const int LoadSec = 20;
    private const int RecoverSec = 25;

    private readonly Func<Task<LiveTelemetry?>> _probe;
    private readonly Func<Task>? _loadOn;
    private readonly Func<Task>? _loadOff;
    private CancellationTokenSource? _cts;

    /// <summary>Preenchido quando a sessão termina com dados suficientes.</summary>
    public PowerSessionResult? Result { get; private set; }

    public BenchWindow(string deviceLabel, Func<Task<LiveTelemetry?>> probe,
        Func<Task>? loadOn = null, Func<Task>? loadOff = null)
    {
        InitializeComponent();
        Title = L10n.T("bench.title");
        HeaderText.Text = $"{(ThemeManager.IsTerminal ? "▓ " : "")}{L10n.T("bench.header")}";
        Instr.Text = L10n.T("bench.instr.idle");
        TempLegend.Text = L10n.T("bench.legend.temp");
        StartBtn.Content = L10n.Btn("btn.start");
        DoneBtn.Content = L10n.Btn("btn.saveclose");
        CloseBtn.Content = L10n.Btn("btn.close");
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            MaxWidth = wa.Width;
            MaxHeight = wa.Height;
            if (Width > wa.Width) Width = wa.Width;
            if (Height > wa.Height) Height = wa.Height;
            Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
        };
        _probe = probe;
        _loadOn = loadOn;
        _loadOff = loadOff;
        DeviceLabel.Text = deviceLabel;
        Scope.Reset(BaseSec + LoadSec + RecoverSec);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var samples = new List<PowerSample>();
        var sw = Stopwatch.StartNew();
        try
        {
            Instr.Text = L10n.T("bench.phase1");
            await RunPhase(samples, 1, sw, sw.Elapsed + TimeSpan.FromSeconds(BaseSec));

            Instr.Text = L10n.T(_loadOn is not null ? "bench.phase2.auto" : "bench.phase2.ios");
            if (_loadOn is not null) await _loadOn();
            await RunPhase(samples, 2, sw, sw.Elapsed + TimeSpan.FromSeconds(LoadSec));

            Instr.Text = L10n.T(_loadOff is not null ? "bench.phase3.auto" : "bench.phase3.ios");
            if (_loadOff is not null) await _loadOff();
            await RunPhase(samples, 3, sw, sw.Elapsed + TimeSpan.FromSeconds(RecoverSec));

            Instr.Text = L10n.T("bench.done");
            Result = PowerAnalyzer.Analyze(samples);
            Verdict.Text = "> " + Result.ChargeDetail +
                           (Result.InternalResistanceMohm is { } ir
                               ? L10n.F("bench.verdict.ir", ir)
                               : Result.VoltageSagMv is { } sag && sag > 0
                                   ? L10n.F("bench.verdict.sag", sag) : "");
            DoneBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Verdict.Text = L10n.F("bench.aborted", ex.Message);
            if (samples.Count > 10)
            {
                Result = PowerAnalyzer.Analyze(samples);
                DoneBtn.IsEnabled = true;
            }
            else StartBtn.IsEnabled = true;
        }
    }

    private async Task RunPhase(List<PowerSample> samples, int phase, Stopwatch sw, TimeSpan end)
    {
        while (sw.Elapsed < end)
        {
            var t = await _probe();
            if (t is not null)
            {
                var s = new PowerSample(sw.Elapsed.TotalSeconds, phase,
                    t.Milliamps, t.Volts, t.TempC, t.Percent, t.Charging,
                    t.NegotiatedWatts, t.SocTempC);
                samples.Add(s);
                Scope.AddSample(s);
                Live.Text = Format(t);
            }
            Elapsed.Text = L10n.F("bench.elapsed",
                (int)sw.Elapsed.TotalSeconds, (int)(end - sw.Elapsed).TotalSeconds);
            await Task.Delay(2000, _cts!.Token);
        }
    }

    private static string Format(LiveTelemetry t)
    {
        var parts = new List<string>();
        if (t.Milliamps is { } m)
            parts.Add($"{m}mA{(t.Charging == true ? "↑" : t.Charging == false ? "↓" : "")}");
        if (t.Volts is { } v) parts.Add($"{v:0.00}V");
        if (t.TempC is { } c) parts.Add($"{c:0.#}°C");
        if (t.SocTempC is { } sc) parts.Add($"SoC {sc:0.#}°C");
        if (t.Percent is { } p) parts.Add($"batt {p}%");
        if (t.NegotiatedWatts is { } w) parts.Add($"neg {w:0}W");
        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
