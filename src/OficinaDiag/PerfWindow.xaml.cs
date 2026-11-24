using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OficinaDiag.Devices;
using OficinaDiag.Tests;

namespace OficinaDiag;

/// <summary>
/// PERFIL — sessão de desempenho/bateria antes/depois (Fase C do Diag Pro).
/// ~5–15 min de stress on-device (página perf-test.html a renderizar
/// WebGL/Canvas) enquanto o host amostra bateria, temperaturas, corrente e
/// frequência de CPU:
/// 1) base 15s → 2) stress N min (a página corre no telemóvel) →
/// 3) recuperação 20s.
/// O sinal perf-started da página alinha a amostragem com o stress real —
/// no iOS o QR pode demorar a ser lido, por isso a espera é generosa.
/// </summary>
public sealed partial class PerfWindow : Window
{
    private const int BaseSec = 15;
    private const int RecoverSec = 20;
    /// <summary>Espera máxima pelo arranque da página — cobre o QR do iOS lido à mão.</summary>
    private const int PageStartTimeoutMs = 120_000;
    /// <summary>
    /// Folga extra na fase de stress: a página corre ?secs=N de tempo ATIVO
    /// (ecrã visível) e tem um teto de relógio de +90 s para ecrã oculto —
    /// o host espera o mesmo para apanhar o resultado dentro da sessão.
    /// </summary>
    private const int PageWallGraceSec = 90;
    /// <summary>Stress pedido fora disto é URL/teclado adulterado — clamp honesto.</summary>
    private const int MinStressSec = 60;
    private const int MaxStressSec = 900;
    private const int DefaultStressMin = 5;

    private readonly Func<Task<LiveTelemetry?>> _probe;
    private readonly Func<Task>? _loadOn;
    private readonly Func<Task>? _loadOff;
    /// <summary>Abre a página de stress no telemóvel — recebe os segundos a correr.</summary>
    private readonly Func<int, Task>? _openPage;
    /// <summary>Stats que a página devolveu (null até ao POST perf-result).</summary>
    private readonly Func<PerfPageStats?>? _pageStats;
    /// <summary>Completa quando a página sinaliza que o stress começou.</summary>
    private readonly Task? _pageStarted;
    /// <summary>Aviso de nova tentativa — o MainWindow repõe o contador de quedas USB.</summary>
    private readonly Action? _sessionStarted;
    /// <summary>Aviso de fim das medições — o MainWindow desarma o contador de quedas.</summary>
    private readonly Action? _sessionEnded;
    private readonly Func<int>? _usbDropCount;
    private CancellationTokenSource? _cts;
    // Sessão a decorrer: fechar a meio pede confirmação em vez de deitar
    // fora minutos de medições com um clique no X.
    private bool _sessaoACorrer;

    /// <summary>Preenchido quando a sessão termina com dados suficientes.</summary>
    public PerfProfileResult? Result { get; private set; }

    public PerfWindow(string deviceLabel, Func<Task<LiveTelemetry?>> probe,
        Func<int, Task>? openPage = null, Func<PerfPageStats?>? pageStats = null,
        Task? pageStarted = null, Func<Task>? loadOn = null, Func<Task>? loadOff = null,
        Action? sessionStarted = null, Action? sessionEnded = null,
        Func<int>? usbDropCount = null)
    {
        InitializeComponent();
        Title = L10n.T("perf.title");
        HeaderText.Text = $"{(ThemeManager.IsTerminal ? "▓ " : "")}{L10n.T("perf.header")}";
        Instr.Text = L10n.T("perf.instr.idle");
        DurationLabel.Text = L10n.T("perf.duration");
        DurationHint.Text = L10n.T("perf.duration.hint");
        TempLegend.Text = L10n.T("bench.legend.temp");
        ResetPhases();
        DurationBox.ItemsSource = new[] { "5", "7", "10" };
        DurationBox.SelectedItem = DefaultStressMin.ToString();
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
        _openPage = openPage;
        _pageStats = pageStats;
        _pageStarted = pageStarted;
        _loadOn = loadOn;
        _loadOff = loadOff;
        _sessionStarted = sessionStarted;
        _sessionEnded = sessionEnded;
        _usbDropCount = usbDropCount;
        DeviceLabel.Text = deviceLabel;
    }

    /// <summary>Minutos do ComboBox editável → segundos de stress (1–15 min).</summary>
    private int StressSeconds()
    {
        var text = DurationBox.Text?.Trim() ?? "";
        // TryParse com a cultura atual aceita "7" e "7,5" (pt) — o técnico
        // pode escrever frações; fora do intervalo faz clamp, nunca exceção.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var mins)
            || !double.IsFinite(mins))
            mins = DefaultStressMin;
        mins = Math.Clamp(mins, 1, 15);
        return (int)Math.Round(Math.Clamp(mins * 60, MinStressSec, MaxStressSec));
    }

    private void ResetPhases()
    {
        Phase1.Text = L10n.T("perf.step.base");
        Phase2.Text = L10n.T("perf.step.stress");
        Phase3.Text = L10n.T("perf.step.recover");
        SetPhase(0, 0);
    }

    private void SetPhase(int done, int current)
    {
        MarkPhase(Phase1, done >= 1 ? 2 : current == 1 ? 1 : 0);
        MarkPhase(Phase2, done >= 2 ? 2 : current == 2 ? 1 : 0);
        MarkPhase(Phase3, done >= 3 ? 2 : current == 3 ? 1 : 0);
    }

    private void MarkPhase(TextBlock tb, int state)
    {
        tb.Foreground = state switch
        {
            2 => (Brush)FindResource("PassBrush"),
            1 => (Brush)FindResource("WarnBrush"),
            _ => (Brush)FindResource("FaintBrush"),
        };
        tb.FontWeight = state == 1 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var stressSecs = StressSeconds();
        StartBtn.IsEnabled = false;
        _sessaoACorrer = true;
        _cts = new CancellationTokenSource();
        // Janela larga: base + stress (+ folga de ecrã oculto) + recuperação
        // + espera pela página.
        Scope.Reset(BaseSec + stressSecs + PageWallGraceSec + RecoverSec
            + PageStartTimeoutMs / 1000.0);
        // Quedas USB contam-se por tentativa e só na janela de medição.
        _sessionStarted?.Invoke();
        var samples = new List<PowerSample>();
        var sw = Stopwatch.StartNew();
        var loadApplied = false;
        var stressRan = false;
        try
        {
            SetPhase(0, 1);
            Instr.Text = L10n.T("perf.phase1");
            await RunPhase(samples, 1, sw, sw.Elapsed + TimeSpan.FromSeconds(BaseSec));

            // Abre a página no telemóvel e espera o sinal de arranque —
            // no Android são ~2 s; no iOS depende do QR ser lido.
            SetPhase(1, 2);
            Instr.Text = L10n.T("perf.phase2.wait");
            if (_loadOn is not null) { await _loadOn(); loadApplied = true; }
            if (_openPage is not null) await _openPage(stressSecs);
            var started = await WaitPageStartAsync();

            if (started)
            {
                Instr.Text = L10n.F("perf.phase2.run", stressSecs);
                // O stress acaba ao fim do tempo ativo + folga de relógio
                // (ecrã oculto) OU quando a página entrega o resultado — um
                // fim antecipado (contexto perdido, motor 'none') não
                // justifica minutos de amostragem em idle.
                await RunPhase(samples, 2, sw,
                    sw.Elapsed + TimeSpan.FromSeconds(stressSecs + PageWallGraceSec),
                    earlyExit: () => _pageStats?.Invoke() is not null);
                stressRan = true;

                SetPhase(2, 3);
                Instr.Text = L10n.T("perf.phase3");
                if (_loadOff is not null) { await _loadOff(); loadApplied = false; }
                await RunPhase(samples, 3, sw, sw.Elapsed + TimeSpan.FromSeconds(RecoverSec));
            }
            else
            {
                // A página nunca arrancou (QR por ler, Wi-Fi, adb falhou) —
                // não há stress para medir; repõe a carga e analisa o que há.
                Instr.Text = L10n.T("perf.phase2.nostart");
                if (_loadOff is not null && loadApplied)
                { await _loadOff(); loadApplied = false; }
            }

            SetPhase(stressRan ? 3 : 1, 0);
            Instr.Text = L10n.T("perf.done");
            Result = PerfAnalyzer.Analyze(samples, _pageStats?.Invoke(),
                stressSecs, _usbDropCount?.Invoke() ?? 0);
            if (Result.SampleCount == 0)
            {
                // Zero amostras = o telemóvel não expôs leitura nenhuma.
                // Gravar daria verificações fabricadas — inconclusivo.
                // StartBtn fica desarmado: os recursos da página (servidor,
                // sinal de arranque, stats) são da MainWindow e morrem com o
                // primeiro resultado — uma segunda tentativa nesta janela
                // reutilizaria uma sessão morta. Repete-se reabrindo o perfil.
                Verdict.Text = "> " + L10n.T("perf.inconclusive") + " · " + L10n.T("perf.retry");
            }
            else
            {
                Verdict.Text = "> " + Result.Summary;
                DoneBtn.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Utilizador fechou a janela a meio: repõe o estado do telefone.
            if (loadApplied && _loadOff is not null)
                try { await _loadOff(); } catch { /* telefone pode ter saído */ }
        }
        catch (Exception ex)
        {
            if (loadApplied && _loadOff is not null)
                try { await _loadOff(); } catch { /* telefone pode ter saído */ }
            Verdict.Text = L10n.F("perf.aborted", ex.Message);
            if (samples.Count > 10)
            {
                Result = PerfAnalyzer.Analyze(samples, _pageStats?.Invoke(),
                    stressSecs, _usbDropCount?.Invoke() ?? 0);
                DoneBtn.IsEnabled = true;
            }
            else
            {
                // Mesma regra da sessão vazia: a página pode já ter entregue
                // o resultado e o servidor estar morto — repetir aqui
                // reutilizaria stats antigas. Nova janela = nova sessão.
                Verdict.Text += " · " + L10n.T("perf.retry");
            }
        }
        finally
        {
            _sessaoACorrer = false;
            // As medições acabaram — quedas a partir daqui já não são do perfil.
            _sessionEnded?.Invoke();
        }
        if (!string.IsNullOrEmpty(Verdict.Text)) VerdictCard.Visibility = Visibility.Visible;
    }

    /// <summary>Espera o sinal perf-started da página — true se arrancou, false se estourou o timeout.</summary>
    private async Task<bool> WaitPageStartAsync()
    {
        if (_pageStarted is null) return true; // sem servidor — segue sem alinhar
        if (_pageStarted.IsCompleted) return true;
        var timeout = Task.Delay(PageStartTimeoutMs, _cts!.Token);
        var done = await Task.WhenAny(_pageStarted, timeout);
        if (done == _pageStarted) return true;
        if (_cts.IsCancellationRequested) throw new OperationCanceledException();
        return false; // timeout — a página não abriu
    }

    private async Task RunPhase(List<PowerSample> samples, int phase, Stopwatch sw,
        TimeSpan end, Func<bool>? earlyExit = null)
    {
        while (sw.Elapsed < end && (earlyExit?.Invoke() != true))
        {
            var t = await _probe();
            if (t is not null)
            {
                var s = new PowerSample(sw.Elapsed.TotalSeconds, phase,
                    t.Milliamps, t.Volts, t.TempC, t.Percent, t.Charging,
                    t.NegotiatedWatts, t.SocTempC, t.CpuFreqMhz);
                samples.Add(s);
                Scope.AddSample(s);
                Live.Text = Format(t);
            }
            Elapsed.Text = L10n.F("bench.elapsed",
                (int)sw.Elapsed.TotalSeconds, (int)Math.Max(0, (end - sw.Elapsed).TotalSeconds));
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
        if (t.CpuFreqMhz is { } f) parts.Add($"{f:0}MHz");
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
        // seguir, o que podia abrir a confirmação de abandono duas vezes.
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_sessaoACorrer && MessageBox.Show(this, L10n.T("perf.confirm.abort"), Title,
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
