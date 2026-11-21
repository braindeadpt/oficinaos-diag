using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.History;
using OficinaDiag.Reports;
using OficinaDiag.Tests;
using OficinaDiag.Ui;
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
        Path.Combine(AppContext.BaseDirectory, PackagePaths.AdbRelativePath);

    private DeviceDetector? _detector;
    // Túnel `adb reverse` ativo (serial, porta) — removido sempre no fim.
    private readonly Devices.AdbTunnel _tunnel;
    private Tests.TestServer? _testServer;
    private readonly ScanHistory _history;
    /// <summary>Dispositivos ligados agora — alimenta o seletor do header.</summary>
    private readonly ObservableCollection<Devices.DetectedDevice> _devices = new();
    private readonly Cloud.DiagConfig _config = Cloud.DiagConfig.Load();
    private readonly Cloud.CloudClient _cloud;
    private DetectedDevice? _current;
    private DeviceReport? _report;
    // Id do dispositivo que gerou _report (adb serial / UDID). Resultados do
    // teste no ecrã e do bench só entram no relatório do MESMO aparelho —
    // trocar de telefone a meio não pode contaminar o diagnóstico do cliente.
    private string? _reportDeviceId;
    // Último relatório IA gerado para _report — viaja no envio à loja para a
    // loja o ver no pedido sem gastar outra geração.
    private string? _lastAiReport;
    // Ticker do header — leitura ambiente da bateria enquanto há dispositivo.
    private CancellationTokenSource? _telemetryCts;
    private bool _busy;
    // Falhas seguidas por ação: à terceira o botão desliga e o técnico é avisado.
    private readonly Log.FailureGuard _falhas = new();
    private string? _acaoEmCurso;
    private long _acaoQuando;

    public MainWindow() : this(null) { }

    /// <summary>
    /// O histórico entra por parâmetro para o harness poder comparar dois
    /// scans sem escrever no histórico de quem corre o CI — e para o histórico
    /// dele não ser podado pela retenção. A app usa o construtor sem
    /// argumentos, que fica com o histórico do utilizador.
    /// </summary>
    internal MainWindow(ScanHistory? historico)
    {
        _history = historico ?? new ScanHistory();
        _cloud = new Cloud.CloudClient(_config);
        _tunnel = new Devices.AdbTunnel(RunAdbAsync, WarnTunnelLeft);
        InitializeComponent();
        DeviceSelector.ItemsSource = _devices;
        // RGPD: a retenção vive no DiagConfig e aplica-se logo no arranque.
        _history.RetentionDays = _config.HistoryRetentionDays;
        Loaded += OnLoaded;

        // Quem rebentou: o handler global não recebe o remetente do clique, e
        // sem isto três falhas não podiam ser atribuídas a um botão. O preview
        // chega antes do handler do botão; o `Click` que volta a subir sem
        // exceção limpa a contagem daquela ação.
        AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, e) => LembrarAcao(e.OriginalSource)), true);
        AddHandler(PreviewKeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space) LembrarAcao(Keyboard.FocusedElement);
            }), true);
        AddHandler(ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is ButtonBase { Name.Length: > 0 } botao)
                    _falhas.Sucesso(botao.Name);
            }), true);
    }

    /// <summary>
    /// Guarda qual foi o botão do gesto (o handler global não o recebe).
    /// `internal` para a prova de produção do harness: um rato sintético não
    /// entrega `OriginalSource` num runner sem desktop, mas a atribuição em si
    /// — achar o botão pelo alvo e carimbar o instante — é esta.
    /// </summary>
    internal void LembrarAcao(object? origem)
    {
        if (BotaoDe(origem) is { Name.Length: > 0 } botao)
        {
            _acaoEmCurso = botao.Name;
            _acaoQuando = Environment.TickCount64;
        }
    }

    /// <summary>O botão de um elemento da árvore (o alvo do clique).</summary>
    private static ButtonBase? BotaoDe(object? origem)
    {
        for (var atual = origem as DependencyObject; atual is not null;)
        {
            if (atual is ButtonBase botao) return botao;
            try { atual = VisualTreeHelper.GetParent(atual); }
            catch { atual = LogicalTreeHelper.GetParent(atual); }
        }
        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Arranque é a melhor altura para escoar envios que ficaram à espera de
        // rede: o técnico acabou de ligar o PC à net da loja.
        _ = DrenarFilaAsync();
        // A área útil é a do monitor onde a janela está, não a do principal:
        // `SystemParameters.WorkArea` num portátil com dock deixa a janela
        // maior do que o ecrã onde ela nasceu.
        AjustarAoEcra();
        // Mudar a escala com a app aberta (125% → 150%) não passava por aqui.
        // Linha única de propósito: o delegado fica em `_assinaturaDpi`, que só
        // existe se **esta** linha correr — é o que a prova do harness lê, porque
        // o WPF não deixa enumerar nem levantar o `DpiChanged` à mão.
        DpiChanged += (_assinaturaDpi = AoMudarEscala);
        // Dois ecrãs com a mesma escala: o DPI não muda, a área sim.
        LocationChanged += (_, _) => TalvezMudouDeMonitor();
        ApplyStrings();
        L10n.Changed += ApplyStrings;
        ThemeManager.Changed += ApplyStrings;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        Title = $"OFICINA-OS // DIAG v{ver}";
        var area = AreaCorrente();
        Log(L10n.F("log.screen", area.Width, area.Height, Width, Height, Left, Top));
        Log(L10n.T("log.boot"));
        Log(L10n.T("log.tagline"));
        // Uma atualização que falhou a meio é dita no arranque seguinte — o
        // log do updater vive em %TEMP% e ninguém o vai lá ler.
        if (Cloud.UpdateChecker.ConsumeUpdateFailure(AppContext.BaseDirectory) is { } falhaDaAtualizacao)
            Log(L10n.F("log.update.failed", falhaDaAtualizacao));
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

    /// <summary>
    /// adb assíncrono com timeout — o telefone pode estar a dormir e três
    /// chamadas encadeadas bloqueavam a UI até ~10 s.
    /// </summary>
    private async Task<bool> RunAdbAsync(string serial, string args)
    {
        // Serial vem do `adb devices` — um valor a começar por '-' era lido
        // pelo adb como flag dele, não como o alvo -s.
        if (serial.Length == 0 || !char.IsLetterOrDigit(serial[0])) return false;
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // ConfigureAwait(false): o cleanup no fecho espera de forma síncrona
            // por este task — retomar no dispatcher bloqueado daria deadlock.
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            await outTask.ConfigureAwait(false);
            await errTask.ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Aviso do AdbTunnel quando o remove falha: o túnel pode continuar
    /// montado e o telefone fica a chegar a 127.0.0.1 sem ninguém à escuta.
    /// No OnClosing a UI está bloqueada à espera deste task — despachar
    /// para o dispatcher daria deadlock, por isso fora da UI o aviso vai
    /// só para o diag.log.
    /// </summary>
    private void WarnTunnelLeft(string msg)
    {
        if (Dispatcher.CheckAccess()) Log(msg);
        else OficinaDiag.Log.AppLog.Write(msg);
    }

    /// <summary>
    /// Limpeza síncrona e limitada para os sítios onde não se pode esperar
    /// (handler do resultado, fecho da app). Fire-and-forget deixava o
    /// `adb reverse` montado no telefone quando o processo saía primeiro.
    /// </summary>
    private void ClearReverseBlocking()
    {
        try { _tunnel.ClearAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { /* adb morto/timeout — nada a fazer */ }
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
        var old = _telemetryCts;
        old?.Cancel();
        var cts = _telemetryCts = new CancellationTokenSource();
        // Token (não o source) é seguro de tocar depois de Dispose — evita
        // ObjectDisposedException na volta do loop quando o source morre.
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
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
                    catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
                    try { await Task.Delay(4000, token); } catch { return; }
                }
            }
            finally { old?.Dispose(); }
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

    /// <summary>
    /// Os dois passos de uma atualização que uma prova de UI tem de substituir:
    /// descarregar a release a sério gasta 100 MB de rede, e o
    /// `Application.Current.Shutdown()` mata o processo do harness a meio. Foi
    /// por isso que o aviso de reinício nunca teve prova. Os valores por
    /// omissão são exactamente os de produção — o handler em si não mudou.
    /// </summary>
    internal Func<Cloud.UpdateChecker.UpdateInfo, Version, Task<(bool ok, string message)>> Baixar =
        BaixarDeProducao;

    internal Action Terminar = TerminarDeProducao;

    /// <summary>
    /// Os defeitos por omissão em métodos com nome. Um campo com um lambda era
    /// invisível: a prova substituía-o e mostrava que o aviso funciona, nunca
    /// que o caminho de produção é este. Com nome, a prova compara o
    /// `Method.Name` do delegate e chama o resto directamente.
    /// </summary>
    internal static string PastaDeInstalacao => AppContext.BaseDirectory;

    internal static Task<(bool ok, string message)> BaixarDeProducao(
        Cloud.UpdateChecker.UpdateInfo info, Version current) =>
        BaixarPara(PastaDeInstalacao, info, current);

    /// <summary>O mesmo com a pasta à escolha — é assim que a prova vê o argumento chegar.</summary>
    internal static Task<(bool ok, string message)> BaixarPara(
        string appDir, Cloud.UpdateChecker.UpdateInfo info, Version current) =>
        Cloud.UpdateChecker.DownloadAndStageAsync(info, appDir, current);

    internal static void TerminarDeProducao() => Application.Current.Shutdown();

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
        var (ok, msg) = await Baixar(_update, current);
        Log(ok ? $"> {msg}" : L10n.F("log.update.fail", msg));
        if (ok)
        {
            // Antes a janela desaparecia sem explicação — a meio de uma sessão
            // de bancada parecia uma falha da app.
            MessageBox.Show(this, L10n.T("log.update.ready"), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            Terminar();
        }
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

    /// <summary>
    /// Uma exceção chegou pelo handler global. Se foi durante um clique recente
    /// neste ecrã, conta para aquele botão; fora disso (um diálogo, uma
    /// continuação tardia) fica só no log, como antes — atribuir ao último botão
    /// desligava um botão que não tem culpa.
    /// </summary>
    internal void RegistarFalha(Exception ex)
    {
        var acao = OficinaDiag.Log.FailureGuard.AcaoReconhecida(_acaoEmCurso,
            Environment.TickCount64 - _acaoQuando);
        if (acao is not null) Falhou(acao, ex);
        else OficinaDiag.Log.AppLog.Exception("UI", ex);
    }

    /// <summary>
    /// Conta a falha, escreve no log até ao limite e, à terceira, avisa o técnico
    /// e desliga o botão. O log para de crescer daí em diante: a mesma exceção a
    /// cada clique enterrava a causa.
    /// </summary>
    internal void Falhou(string acao, Exception ex)
    {
        var r = _falhas.Registar(acao);
        if (!r.EscreverLog) return;

        OficinaDiag.Log.AppLog.Exception("UI", ex);
        // Consola da app: o erro tem de ser visível sem abrir o diag.log.
        Console.AppendText($"! UI: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        Console.ScrollToEnd();
        if (!r.Avisar) return;

        var rotulo = (FindName(acao) as ContentControl)?.Content?.ToString() ?? acao;
        Log(L10n.F("log.err.disabled", rotulo));
        if (FindName(acao) is UIElement alvo) alvo.IsEnabled = false;
        MessageBox.Show(this, L10n.F("err.repeated", rotulo), L10n.T("err.repeated.title"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // Os WaitForExit dentro dos coletores são síncronos — sem o
            // Task.Run cada chamada adb bloqueava a UI thread e a janela
            // congelava durante o scan inteiro.
            var scannedId = _current.Id;
            _report = _current.Kind == DeviceKind.Android
                ? await Task.Run(() => new AndroidCollector(_adbPath).CollectAsync(scannedId.TrimEnd('!')))
                : await Task.Run(() => new IosCollector().CollectAsync(scannedId));
            _reportDeviceId = scannedId;

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
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = _current?.Kind == DeviceKind.Ios ? "ios" : "android",
                Device = { Serial = _current?.Id?.TrimEnd('!') },
            };
            _reportDeviceId = _current?.Id;
        }
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
        OpenHtmlFile(dlg.FileName);
    }

    /// <summary>
    /// Abre um ficheiro que acabámos de gravar — só .html: o nome vem do
    /// SaveFileDialog e o utilizador podia trocar a extensão à mão; com
    /// UseShellExecute isso corria a associação arbitrária da shell.
    /// </summary>
    private static void OpenHtmlFile(string path)
    {
        if (!path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _tunnel.ClearAsync(); // túnel da sessão anterior não fica pendurado
            _testServer?.Dispose(); // sessão anterior abandonada — liberta a porta
            var server = _testServer = new TestServer();
            if (!server.Start())
            {
                _testServer = null;
                server.Dispose();
                Log(L10n.F("log.test.fail", L10n.T("err.test.htmlmissing")));
                return;
            }
            // InvokeAsync + await: a UI atualiza já e a limpeza do túnel espera
            // fora do dispatcher, sem prender a janela 3 s.
            var forReport = _report;
            var testedId = _current?.Id;
            server.ResultReceived += data => Dispatcher.InvokeAsync(async () =>
            {
                // O resultado pertence ao aparelho em teste — se o relatório
                // foi substituído ou descreve outro dispositivo, recusar em
                // vez de contaminar o diagnóstico do cliente seguinte.
                if (_report is null || !ReferenceEquals(_report, forReport)
                    || _reportDeviceId != testedId)
                {
                    Log(L10n.T("log.test.wrongdev"));
                    return;
                }
                TestResultMapper.Apply(_report, data);
                Log(L10n.T("log.test.received"));
                ApplyGrade();
                await _tunnel.ClearAsync(); // o telefone não precisa mais do túnel
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
                if (await RunAdbAsync(serial, $"reverse tcp:{server.Port} tcp:{server.Port}"))
                {
                    _tunnel.Mount(serial, server.Port);
                    await RunAdbAsync(serial, $"shell am start -a android.intent.action.VIEW -d {loop}");
                    Log(L10n.T("log.test.reverse"));
                }
                else
                {
                    // adb sem reverse: fica o caminho LAN (Wi-Fi) como antes.
                    await RunAdbAsync(serial, $"shell am start -a android.intent.action.VIEW -d {url}");
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
        if (_report is not null && _reportDeviceId != d.Id)
        {
            // O bench correu no aparelho d mas o relatório aberto descreve
            // outro — gravar aqui contaminava o diagnóstico desse cliente.
            Log(L10n.T("log.test.wrongdev"));
            return;
        }
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = d.Kind == DeviceKind.Android ? "android" : "ios",
                Device = { Serial = d.Id },
            };
            _reportDeviceId = d.Id;
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
                isHtml ? ReportBuilder.ToHtml(_report, ThemeManager.Current)
                       : ReportBuilder.ToJson(_report));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
        Log(L10n.F("log.export.saved", dlg.FileName));
        // HTML é para ver/partilhar — abre já no browser predefinido.
        if (isHtml)
            OpenHtmlFile(dlg.FileName);
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
        OpenHtmlFile(save.FileName);
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
                _lastAiReport, dlg.Purpose, Fila);
            // "Ficou em fila" não é um falhanço para o técnico: o relatório sai
            // quando a rede voltar, e não deve aparecer com um "!" à frente.
            Log(ok || msg == L10n.T("cloud.send.queued")
                ? $"> {msg}"
                : L10n.F("log.send.fail", msg));
            if (!ok) _ = DrenarFilaAsync();
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
                    File.WriteAllText(dlg.FileName, ReportBuilder.AiReportToHtml(_report, text, ThemeManager.Current));
                }
                catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); return; }
                Log(L10n.F("log.ai.saved", dlg.FileName));
                OpenHtmlFile(dlg.FileName);
            }
        }
        finally { AiButton.IsEnabled = true; }
    }

    private Cloud.CloudOutbox? _outbox;

    /// <summary>
    /// Fila durável dos envios que falharam por rede. Criada à primeira
    /// utilização para não abrir pasta nenhuma em quem nunca envia à loja.
    /// </summary>
    private Cloud.CloudOutbox Fila => _outbox ??= new Cloud.CloudOutbox(
        Cloud.CloudOutbox.PastaPorDefeito,
        (code, corpo, chave, ct) => _cloud.SendIntakePayloadAsync(code, corpo, chave, ct));

    private async Task DrenarFilaAsync()
    {
        try
        {
            var enviados = await Fila.DrenarAsync();
            if (enviados > 0) Log(L10n.F("log.outbox.enviados", enviados));
        }
        catch (Exception ex) { Log(L10n.F("log.err", ex.Message)); }
    }

    // ── a janela dentro do ecrã: DPI e multi-monitor ──────────────────────

    private const uint MonitorDefaultToNearest = 2;
    private IntPtr _monitorAtual;
    private bool _aAjustar;
    private DpiChangedEventHandler? _assinaturaDpi;

    /// <summary>A prova do harness lê isto: a assinatura do `DpiChanged` existe?</summary>
    internal bool DpiLigado => _assinaturaDpi is not null;

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNativo { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectNativo rcMonitor;
        public RectNativo rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// Área útil do monitor onde a janela está, em DIP. A conversão usa o DPI
    /// da janela: a 150%, 1920 px são 1280 DIP — e é por isso que a janela
    /// deixa de caber num portátil.
    /// </summary>
    private AreaEcra? AreaDoMonitor()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return null;
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info)) return null;
            var escala = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (!(escala > 0)) escala = 1.0;
            var dpi = 96 * escala;
            var r = info.rcWork;
            return new AreaEcra(
                WindowFit.ParaDip(r.Left, dpi),
                WindowFit.ParaDip(r.Top, dpi),
                WindowFit.ParaDip(r.Right - r.Left, dpi),
                WindowFit.ParaDip(r.Bottom - r.Top, dpi));
        }
        catch { return null; }
    }

    private AreaEcra AreaCorrente()
    {
        if (WindowFit.AreaForcada is { } forcada) return forcada;
        if (AreaDoMonitor() is { } medida) return medida;
        // Último recurso: a work area do WPF (monitor principal), como antes.
        var wa = SystemParameters.WorkArea;
        return new AreaEcra(wa.Left, wa.Top, wa.Width, wa.Height);
    }

    /// <summary>
    /// Aplica a área útil à janela. É o único sítio que o faz: o arranque, o
    /// `DpiChanged` e a mudança de monitor passam todos por aqui.
    /// <paramref name="recentrar"/> falso só limita e encosta para dentro —
    /// a janela que o técnico arrastou fica onde ele a deixou.
    /// </summary>
    internal void AjustarAoEcra(bool recentrar = true)
    {
        if (_aAjustar) return;
        _aAjustar = true;
        try
        {
            var caixa = WindowFit.Calcular(
                AreaCorrente(), Pedida(Width, MinWidth), Pedida(Height, MinHeight),
                recentrar, Left, Top);
            MaxWidth = caixa.MaxWidth;
            MaxHeight = caixa.MaxHeight;
            if (double.IsFinite(caixa.Width)) Width = caixa.Width;
            if (double.IsFinite(caixa.Height)) Height = caixa.Height;
            // Posicionar depois de encolher: centrar com o tamanho antigo
            // empurrava a janela para fora do ecrã pequeno.
            if (caixa.Left is { } left) Left = left;
            if (caixa.Top is { } top) Top = top;
        }
        finally { _aAjustar = false; }
    }

    /// <summary>
    /// Mudança de escala a meio da sessão (125% → 150%, ou arrastar entre
    /// ecrãs de escalões diferentes). Não recentra: o técnico já escolheu onde
    /// quer a janela.
    /// </summary>
    private void AoMudarEscala(object? sender, DpiChangedEventArgs e) =>
        AjustarAoEcra(recentrar: false);

    /// <summary>`Width` pode estar a NaN antes do primeiro layout.</summary>
    private static double Pedida(double valor, double minimo) =>
        double.IsFinite(valor) && valor > 0 ? valor : minimo;

    /// <summary>
    /// Mudar de monitor sem mudar de escalão não levanta `DpiChanged` — a
    /// janela ficava presa à área do ecrã anterior.
    /// </summary>
    private void TalvezMudouDeMonitor()
    {
        if (_aAjustar || WindowFit.AreaForcada is not null) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == _monitorAtual) return;
            _monitorAtual = monitor;
            // Só limitar e encostar: o utilizador arrastou-a para ali de propósito.
            AjustarAoEcra(recentrar: false);
        }
        catch { /* sem interop, fica a área anterior */ }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Sem estas saídas o MainWindow ficava refletido em L10n/ThemeManager
        // e continuava a ser notificado depois de fechado.
        ClearReverseBlocking(); // fechar a app não deixa o túnel adb montado
        L10n.Changed -= ApplyStrings;
        ThemeManager.Changed -= ApplyStrings;
        _testServer?.Dispose();
        _detector?.Dispose();
        _telemetryCts?.Cancel();
        base.OnClosing(e);
    }
}
