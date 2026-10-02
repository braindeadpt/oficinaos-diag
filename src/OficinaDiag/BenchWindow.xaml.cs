using System.Diagnostics;
using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

/// <summary>
/// SCAN+ — sessão guiada de potência (~60s):
/// 1) repouso 15s → 2) "liga o carregador" → 3) carga 30s.
/// A transição repouso→carga dá a resistência interna (ΔV/ΔI);
/// a fase de carga compara watts negociados com reais.
/// </summary>
public sealed partial class BenchWindow : Window
{
    private const int RestSec = 15;
    private const int PlugWaitSec = 45;
    private const int ChargeSec = 30;

    private readonly Func<Task<LiveTelemetry?>> _probe;
    private CancellationTokenSource? _cts;

    /// <summary>Preenchido quando a sessão termina com dados suficientes.</summary>
    public PowerSessionResult? Result { get; private set; }

    public BenchWindow(string deviceLabel, Func<Task<LiveTelemetry?>> probe)
    {
        InitializeComponent();
        _probe = probe;
        DeviceLabel.Text = deviceLabel;
        Scope.Reset(RestSec + PlugWaitSec + ChargeSec);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var samples = new List<PowerSample>();
        var sw = Stopwatch.StartNew();
        try
        {
            // ── fase 1: repouso ──────────────────────────────────────────
            Instr.Text = $"fase 1/3 — repouso: mantém o telemóvel SEM carregador";
            var restEnd = RestSec;
            while (sw.Elapsed.TotalSeconds < restEnd)
            {
                var t = await Probe(samples, 1, sw);
                if (t is null) return;
                Elapsed.Text = $"{(int)sw.Elapsed.TotalSeconds}s · restam {(int)(restEnd - sw.Elapsed.TotalSeconds)}s";
            }

            // se já estava a carregar durante o repouso todo, salta a espera
            var restCharging = samples.Count(s => s.Phase == 1 && s.Charging == true);
            var alreadyCharging = restCharging > samples.Count(s => s.Phase == 1) * 0.6;
            var restMa = samples.Where(s => s.Phase == 1 && s.Milliamps is { })
                .Select(s => (double)s.Milliamps!.Value).DefaultIfEmpty(0).Average();

            bool detected = alreadyCharging;
            if (!detected)
            {
                // ── fase 2: ligar o carregador ───────────────────────────
                Instr.Text = "fase 2/3 — LIGA O CARREGADOR agora";
                var waitEnd = sw.Elapsed + TimeSpan.FromSeconds(PlugWaitSec);
                while (sw.Elapsed < waitEnd)
                {
                    var t = await Probe(samples, 2, sw);
                    if (t is null) return;
                    detected = t.Charging == true ||
                               (t.Milliamps is { } m && m >= Math.Max(400, restMa * 2.5));
                    if (detected) break;
                }
            }

            if (!detected)
            {
                Verdict.Text = $"! nunca detetou carga em {PlugWaitSec}s — " +
                               "porta, cabo ou carregador não entregam nada";
                Finish(samples, sw);
                return;
            }

            // ── fase 3: carga ────────────────────────────────────────────
            Instr.Text = "fase 3/3 — a medir carga… não mexas no telemóvel";
            var chargeEnd = sw.Elapsed + TimeSpan.FromSeconds(ChargeSec);
            while (sw.Elapsed < chargeEnd)
            {
                var t = await Probe(samples, 3, sw);
                if (t is null) return;
                Elapsed.Text = $"{(int)sw.Elapsed.TotalSeconds}s · restam {(int)(chargeEnd - sw.Elapsed).TotalSeconds}s";
            }

            Instr.Text = "sessão completa";
            Verdict.Text = "> " + (PowerAnalyzer.Analyze(samples).ChargeDetail ?? "");
            Finish(samples, sw);
        }
        catch (Exception ex)
        {
            Verdict.Text = $"! sessão abortada: {ex.Message}";
            StartBtn.IsEnabled = true;
        }
    }

    /// <summary>Probe + registo + desenho. Devolve null se o dispositivo se desligou.</summary>
    private async Task<LiveTelemetry?> Probe(List<PowerSample> samples, int phase, Stopwatch sw)
    {
        var t = await _probe();
        if (t is null)
        {
            await Task.Delay(2000, _cts!.Token);
            return new LiveTelemetry(null, null, null, null, null); // mantém o relógio
        }
        var s = new PowerSample(sw.Elapsed.TotalSeconds, phase,
            t.Milliamps, t.Volts, t.TempC, t.Percent, t.Charging, t.NegotiatedWatts);
        samples.Add(s);
        Scope.AddSample(s);
        Live.Text = Format(t);
        await Task.Delay(2000, _cts!.Token);
        return t;
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

    private void Finish(List<PowerSample> samples, Stopwatch sw)
    {
        Result = PowerAnalyzer.Analyze(samples);
        DoneBtn.IsEnabled = true;
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
