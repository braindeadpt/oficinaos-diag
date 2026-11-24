using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using OficinaDiag.Devices;
using OficinaDiag.History;
using OficinaDiag.Reports;
using OficinaDiag.Tests;

namespace OficinaDiag;

public sealed class ResultRow
{
    public required string Key { get; init; }
    public required string Status { get; init; }
    public required string StatusLabel { get; init; }
    public required string Display { get; init; }
    public required string CategoryTitle { get; init; }
    public required int CategoryRank { get; init; }
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
    // Último relatório IA de diagnóstico gerado para _report — viaja no envio
    // à loja para a loja o ver no pedido sem gastar outra geração. O relatório
    // de manutenção não entra aqui: é um entregável do técnico, não do intake.
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

    /// <summary>Reaplica todos os textos da janela — corre no arranque e ao trocar de idioma.</summary>
    private void ApplyStrings()
    {
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        HeaderTitle.Text = $"{(ThemeManager.IsTerminal ? "█▓▒░ " : "")}OFICINA-OS // DIAG v{ver}";
        StatusText.Text = _current is { } d ? StatusFor(d) : L10n.T("status.watching");
        ResultsHeader.Text = L10n.Section("results.header");
        ScanButton.Content = L10n.Btn("btn.scan");
        BenchButton.Content = L10n.Btn("btn.bench");
        PerfButton.Content = L10n.Btn("btn.perf");
        TestButton.Content = L10n.Btn("btn.test");
        AppsButton.Content = L10n.Btn("btn.apps");
        ChecklistButton.Content = L10n.Btn("btn.checklist");
        ServiceButton.Content = L10n.Btn("btn.service");
        MaintenanceButton.Content = L10n.Btn("btn.maint");
        MirrorButton.Content = L10n.Btn("btn.mirror");
        CompareButton.Content = L10n.Btn("btn.compare");
        LabelButton.Content = L10n.Btn("btn.label");
        ClientButton.Content = L10n.Btn("btn.client");
        ExportButton.Content = L10n.Btn("btn.export");
        SendButton.Content = L10n.Btn("btn.send");
        AiButton.Content = L10n.Btn("btn.ai");
        MaintenanceAiButton.Content = L10n.Btn("btn.ai.maint");
        InsuranceButton.Content = L10n.Btn("btn.insurance");
        LogButton.Content = L10n.Btn("btn.log");
        ConfigButton.Content = L10n.Btn("btn.settings");
        GroupDeviceText.Text = L10n.T("group.device");
        GroupRecordsText.Text = L10n.T("group.records");
        GroupDeliverText.Text = L10n.T("group.deliver");
        GroupSystemText.Text = L10n.T("group.system");
        EmptyTitle.Text = L10n.T("empty.title");
        EmptyDesc.Text = L10n.T("empty.desc");
        EmptyStep1.Text = L10n.T("empty.step1");
        EmptyStep2.Text = L10n.T("empty.step2");
        EmptyStep3.Text = L10n.T("empty.step3");
        BusyCancelButton.Content = L10n.Btn("busy.cancel");
        // Tema novo = brushes novas: as linhas da lista foram pintadas com as
        // antigas (verde néon sobre fundo branco no tema Fluent).
        RefreshResults();
        if (_update is not null)
            UpdateButton.Content = ThemeManager.IsTerminal
                ? $"[ {L10n.F("btn.update", _update.Tag)} ]"
                : L10n.F("btn.update", _update.Tag);
    }

    private static string ResultStatusLabel(string status) => status switch
    {
        "pass" => L10n.T("status.pass"),
        "warn" => L10n.T("status.warn"),
        "fail" => L10n.T("status.fail"),
        "info" => L10n.T("status.info"),
        "skipped" => L10n.T("status.skipped"),
        _ => status,
    };

    private static (string Title, int Rank) ResultCategory(string key)
    {
        var dot = key.IndexOf('.');
        var prefix = dot >= 0 ? key[..dot] : key;
        var rank = prefix switch
        {
            "identity" => 0,
            "device" => 1,
            "os" => 2,
            "security" => 3,
            "storage" => 4,
            "memory" => 5,
            "display" => 6,
            "sensor" or "sensors" => 7,
            "battery" => 8,
            "ios" or "apps" => 9,
            "usb" => 10,
            "power" => 11,
            "screen" => 12,
            "test" => 13,
            "tpl" or "service" => 14,
            "checklist" => 15,
            "maint" or "maintenance" or "bench" => 16,
            "perf" => 17,
            "grade" => 18,
            "health" => 19,
            "suggest" => 20,
            "logs" => 21,
            "scan" => 22,
            _ => 99,
        };
        var titleKey = $"cat.{prefix}";
        var title = L10n.T(titleKey);
        return (title == titleKey ? prefix.ToUpperInvariant() : title, rank);
    }

    private static List<ResultRow> BuildResultRows(DeviceReport report) =>
        report.Results
            .Select(kv =>
            {
                var (category, rank) = ResultCategory(kv.Key);
                return new ResultRow
                {
                    Key = kv.Key,
                    Status = kv.Value.Status,
                    StatusLabel = ResultStatusLabel(kv.Value.Status),
                    Display = $"{kv.Value.Value ?? ""} {kv.Value.Detail ?? ""}".Trim(),
                    CategoryTitle = category,
                    CategoryRank = rank,
                };
            })
            .OrderBy(r => r.CategoryRank)
            .ThenBy(r => r.Key)
            .ToList();

    private static ICollectionView ResultRowsView(DeviceReport report)
    {
        var view = new ListCollectionView(BuildResultRows(report));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ResultRow.CategoryTitle)));
        return view;
    }

    private void SyncReportButtons()
    {
        // Durante scan/teste o BeginBusy já bloqueou tudo — re-sync (tema/idioma)
        // não pode reativar botões a meio da operação.
        if (BusyBar.Visibility == Visibility.Visible) return;
        // Um relatório criado só para receber o teste no ecrã ainda não tem
        // medições — não pode acordar exportação/envio antes do telemóvel responder.
        var hasReport = _report is not null
            && (_report.Results.Count > 0 || _report.HasManualData());
        ExportButton.IsEnabled = hasReport;
        SendButton.IsEnabled = hasReport && !_sendBusy;
        InsuranceButton.IsEnabled = hasReport;
        CompareButton.IsEnabled = hasReport;
        LabelButton.IsEnabled = hasReport;
        ClientButton.IsEnabled = hasReport;
        UpdateAiButtons();
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
