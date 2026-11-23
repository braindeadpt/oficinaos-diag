using System.ComponentModel;
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
    /// <summary>Aviso de nova tentativa — o MainWindow repõe o contador de quedas USB.</summary>
    private readonly Action? _sessionStarted;
    private CancellationTokenSource? _cts;
    // Sessão a decorrer: fechar a meio pede confirmação em vez de deitar
    // fora 60 s de medições com um clique no X.
    private bool _sessaoACorrer;

    /// <summary>Preenchido quando a sessão termina com dados suficientes.</summary>
    public PowerSessionResult? Result { get; private set; }

    public BenchWindow(string deviceLabel, Func<Task<LiveTelemetry?>> probe,
        Func<Task>? loadOn = null, Func<Task>? loadOff = null, Action? sessionStarted = null)
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
        _sessionStarted = sessionStarted;
        DeviceLabel.Text = deviceLabel;
        Scope.Reset(BaseSec + LoadSec + RecoverSec);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        _sessaoACorrer = true;
        _cts = new CancellationTokenSource();
        // Uma sessão inconclusiva pode ser repetida — o osciloscópio
        // recomeça limpo em vez de sobrepor as amostras da tentativa morta.
        Scope.Reset(BaseSec + LoadSec + RecoverSec);
        // E as quedas USB contam-se por tentativa: as da tentativa anterior
        // não podem manchar uma repetição estável.
        _sessionStarted?.Invoke();
        var samples = new List<PowerSample>();
        var sw = Stopwatch.StartNew();
        var loadApplied = false;
        try
        {
            Instr.Text = L10n.T("bench.phase1");
            await RunPhase(samples, 1, sw, sw.Elapsed + TimeSpan.FromSeconds(BaseSec));

            Instr.Text = L10n.T(_loadOn is not null ? "bench.phase2.auto" : "bench.phase2.ios");
            if (_loadOn is not null) { await _loadOn(); loadApplied = true; }
            await RunPhase(samples, 2, sw, sw.Elapsed + TimeSpan.FromSeconds(LoadSec));

            Instr.Text = L10n.T(_loadOff is not null ? "bench.phase3.auto" : "bench.phase3.ios");
            if (_loadOff is not null) { await _loadOff(); loadApplied = false; }
            await RunPhase(samples, 3, sw, sw.Elapsed + TimeSpan.FromSeconds(RecoverSec));

            Instr.Text = L10n.T("bench.done");
            Result = PowerAnalyzer.Analyze(samples);
            if (Result.SampleCount == 0)
            {
                // Zero amostras = o telemóvel não expôs leitura nenhuma em
                // 60 s. Gravar daria «saúde 100/100» e grau A à custa de
                // verificações fabricadas — a sessão é inconclusiva e o
                // técnico repete a bancada (Start volta a ficar ativo).
                Verdict.Text = "> " + L10n.T("bench.inconclusive");
                StartBtn.IsEnabled = true;
            }
            else
            {
                Verdict.Text = "> " + Result.ChargeDetail +
                               (Result.InternalResistanceMohm is { } ir
                                   ? L10n.F("bench.verdict.ir", ir)
                                   : Result.VoltageSagMv is { } sag && sag > 0
                                       ? L10n.F("bench.verdict.sag", sag) : "");
                DoneBtn.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Utilizador fechou a janela a meio da sessão: não é falha do
            // telefone, só repõe a carga que ficou aplicada e sai calado.
            if (loadApplied && _loadOff is not null)
                try { await _loadOff(); } catch { /* telefone pode ter saído */ }
        }
        catch (Exception ex)
        {
            // Abort a meio da fase 2 (janela fechada, probe morto) — repõe
            // o estado do telefone, senão o stay-on ficava ligado no aparelho
            // do cliente.
            if (loadApplied && _loadOff is not null)
                try { await _loadOff(); } catch { /* telefone pode ter saído */ }
            Verdict.Text = L10n.F("bench.aborted", ex.Message);
            if (samples.Count > 10)
            {
                Result = PowerAnalyzer.Analyze(samples);
                DoneBtn.IsEnabled = true;
            }
            else StartBtn.IsEnabled = true;
        }
        finally { _sessaoACorrer = false; }
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
        // Só Close(): pôr DialogResult antes disparava o fecho e o OnClosing a
        // seguir, o que podia abrir a confirmação de abandono duas vezes. Sem
        // DialogResult, ShowDialog devolve false na mesma.
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Só pergunta se há dados a perder; fechar no fim da sessão é direto.
        if (_sessaoACorrer && MessageBox.Show(this, L10n.T("bench.confirm.abort"), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
