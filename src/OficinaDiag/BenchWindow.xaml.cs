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
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            MaxWidth = wa.Width;
            MaxHeight = wa.Height;
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
            Instr.Text = "fase 1/3 — base: deixa o telemóvel quieto (a carregar do cabo)";
            await RunPhase(samples, 1, sw, sw.Elapsed + TimeSpan.FromSeconds(BaseSec));

            Instr.Text = _loadOn is not null
                ? "fase 2/3 — a aplicar carga: ecrã ligado + brilho máximo"
                : "fase 2/3 — LIGA o ecrã do iPhone e ABRE a Câmara";
            if (_loadOn is not null) await _loadOn();
            await RunPhase(samples, 2, sw, sw.Elapsed + TimeSpan.FromSeconds(LoadSec));

            Instr.Text = _loadOff is not null
                ? "fase 3/3 — a retirar carga: observa a recuperação"
                : "fase 3/3 — desliga o ecrã e deixa o telemóvel quieto";
            if (_loadOff is not null) await _loadOff();
            await RunPhase(samples, 3, sw, sw.Elapsed + TimeSpan.FromSeconds(RecoverSec));

            Instr.Text = "sessão completa";
            Result = PowerAnalyzer.Analyze(samples);
            Verdict.Text = "> " + Result.ChargeDetail +
                           (Result.InternalResistanceMohm is { } ir
                               ? $" · resistência interna ~{ir} mΩ"
                               : Result.VoltageSagMv is { } sag && sag > 0
                                   ? $" · queda {sag} mV sob carga" : "");
            DoneBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Verdict.Text = $"! sessão abortada: {ex.Message}";
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
                    t.Milliamps, t.Volts, t.TempC, t.Percent, t.Charging, t.NegotiatedWatts);
                samples.Add(s);
                Scope.AddSample(s);
                Live.Text = Format(t);
            }
            Elapsed.Text = $"{(int)sw.Elapsed.TotalSeconds}s · restam {(int)(end - sw.Elapsed).TotalSeconds}s";
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
