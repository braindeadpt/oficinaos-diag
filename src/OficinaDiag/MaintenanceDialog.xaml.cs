using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using OficinaDiag.Devices;
using OficinaDiag.Maintenance;

namespace OficinaDiag;

/// <summary>
/// Limpeza/otimização Android medida: mostra o "antes", deixa o técnico
/// escolher as ações e, ao CORRER, executa-as no aparelho e mostra o
/// antes/depois. Só grava no relatório quando o técnico carrega GUARDAR —
/// fechar sem guardar deita fora as medições (e as alterações do telemóvel
/// já feitas ficam, mas não entravam no relatório na mesma).
/// </summary>
public sealed partial class MaintenanceDialog : Window
{
    private readonly DeviceReport _report;
    private readonly string _adbPath;
    private readonly string _serial;
    private readonly Action<string> _log;

    /// <summary>True depois de GUARDAR — o MainWindow liga os botões de relatório.</summary>
    public bool Saved { get; private set; }

    public MaintenanceDialog(DeviceReport report, string adbPath, string serial, Action<string> log)
    {
        InitializeComponent();
        _report = report;
        _adbPath = adbPath;
        _serial = serial;
        _log = log;

        Title = L10n.T("maint.title");
        HeaderText.Text = L10n.T("maint.header");
        DescText.Text = L10n.T("maint.desc");
        ApplyStrings();
        L10n.Changed += ApplyStrings;
        Closed += (_, _) => L10n.Changed -= ApplyStrings;

        // o teto da work area — o mesmo cuidado do ChecklistDialog
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            MaxWidth = Math.Min(MaxWidth, wa.Width);
            MaxHeight = Math.Min(MaxHeight, wa.Height);
            MinWidth = Math.Min(MinWidth, MaxWidth);
            MinHeight = Math.Min(MinHeight, MaxHeight);
            if (Width > wa.Width) Width = wa.Width;
            if (Height > wa.Height) Height = wa.Height;
        };

        // Animações a 0 e repor são mutuamente exclusivas na UI.
        ChkAnimOff.Checked += (_, _) => ChkAnimRestore.IsChecked = false;
        ChkAnimRestore.Checked += (_, _) => ChkAnimOff.IsChecked = false;
        ChkAnimRestore.Visibility = _report.Maintenance?.AnimationsBefore is null
            ? Visibility.Collapsed : Visibility.Visible;

        _ = LoadAppsAsync();
        _ = MeasureAsync();
    }

    private void ApplyStrings()
    {
        Title = L10n.T("maint.title");
        HeaderText.Text = L10n.T("maint.header");
        DescText.Text = L10n.T("maint.desc");
        AppsLabel.Text = L10n.T("maint.apps");
        ChkTrim.Content = L10n.T("maint.act.trim");
        ChkClearCache.Content = L10n.T("maint.act.clearcache");
        ChkForceStop.Content = L10n.T("maint.act.forcestop");
        ChkKillAll.Content = L10n.T("maint.act.killall");
        ChkAnimOff.Content = L10n.T("maint.act.animoff");
        ChkAnimRestore.Content = L10n.T("maint.act.animrestore");
        ChkBatteryOpt.Content = L10n.T("maint.act.batteryopt");
        SaverLabel.Text = L10n.T("maint.act.saver");
        RunBtn.Content = L10n.Btn("btn.run");
        SaveBtn.Content = L10n.Btn("btn.apply");
        CancelBtn.Content = L10n.Btn("btn.cancel");
        MeasureAgainBtn.Content = L10n.Btn("maint.measure.again");
        ResultsLabel.Text = L10n.T("maint.results.header");

        SaverCombo.ItemsSource = new[]
        {
            L10n.T("maint.saver.keep"), L10n.T("maint.saver.on"), L10n.T("maint.saver.off"),
        };
        SaverCombo.SelectedIndex = 0;
    }

    // ── medições ────────────────────────────────────────────────────────

    /// <summary>Snapshot do aparelho — corre em background, nunca na UI thread.</summary>
    private async Task MeasureAsync()
    {
        SnapText.Text = L10n.T("maint.measuring");
        try
        {
            var maint = new AndroidMaintenance(_adbPath, _serial);
            var before = await Task.Run(() => maint.SnapshotAsync());
            RenderSnapshot(before);
        }
        catch (Exception ex)
        {
            SnapText.Text = L10n.F("log.err", ex.Message);
        }
    }

    private void MeasureAgain_Click(object sender, RoutedEventArgs e) => _ = MeasureAsync();

    /// <summary>Formato do painel "antes" — os mesmos números que o relatório mede.</summary>
    private void RenderSnapshot(MaintenanceSnapshot? s)
    {
        if (s is null) { SnapText.Text = "—"; return; }
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"{L10n.T("maint.snap.storage")}: {(s.StorageFreeBytes is { } f ? MaintenanceDiff.FormatBytes(f) : "—")}");
        sb.AppendLine($"{L10n.T("maint.snap.ram")}: {(s.MemAvailableBytes is { } m ? MaintenanceDiff.FormatBytes(m) : "—")}");
        sb.AppendLine($"{L10n.T("maint.snap.battery")}: {(s.BatteryLevelPct is { } b ? $"{b}%" : "—")}" +
            (s.BatteryTempC is { } t ? $" · {t.ToString("0.#", inv)} °C" : ""));
        sb.AppendLine($"{L10n.T("maint.snap.uptime")}: {(s.UptimeSeconds is { } u ? MaintenanceDiff.FormatUptime(u) : "—")}");
        sb.AppendLine($"{L10n.T("maint.snap.anim")}: {AnimText(s.Animations)}");
        sb.AppendLine($"{L10n.T("maint.snap.procs")}: {(s.RunningProcesses?.ToString(inv) ?? "—")}");
        sb.AppendLine($"{L10n.T("maint.snap.powersaver")}: {SaverText(s.LowPowerMode)}");
        SnapText.Text = sb.ToString().TrimEnd();
        MeasureAgainBtn.Visibility = Visibility.Visible;
    }

    private string AnimText(AnimationScales? a)
    {
        if (a is null) return "—";
        var inv = CultureInfo.InvariantCulture;
        string F(double? v) => v?.ToString("0.##", inv) ?? "—";
        return $"{F(a.Window)} · {F(a.Transition)} · {F(a.Animator)}";
    }

    private string SaverText(int? lowPower) => lowPower switch
    {
        1 => L10n.T("maint.saver.on"),
        0 => L10n.T("maint.saver.off"),
        _ => "—",
    };

    // ── apps ────────────────────────────────────────────────────────────

    private async Task LoadAppsAsync()
    {
        try
        {
            var maint = new AndroidMaintenance(_adbPath, _serial);
            var apps = await Task.Run(() => maint.ListUserPackagesAsync());
            // Lista vazia (adb falhou ou aparelho sem apps de utilizador): o
            // técnico continua — as ações de sistema não dependem da lista.
            await Dispatcher.InvokeAsync(() => AppsList.ItemsSource = apps);
        }
        catch { /* ver comentário acima */ }
    }

    private List<string> SelectedApps() =>
        AppsList.SelectedItems.Cast<string>().Distinct().ToList();

    // ── correr ──────────────────────────────────────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApps();
        var plan = BuildPlan(selected);
        if (!plan.HasAnyAction)
        {
            MessageBox.Show(this, L10n.T("maint.noaction"), L10n.T("maint.title"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Guarda explícita — o pedido do pacote exige confirmação antes de mexer no telemóvel.
        if (MessageBox.Show(this, L10n.T("maint.confirm"), L10n.T("maint.confirm.title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetRunning(true);
        _log(L10n.T("log.maint.run"));
        try
        {
            var maint = new AndroidMaintenance(_adbPath, _serial);
            var result = await Task.Run(() => maint.RunCleanupAsync(plan));
            _report.Maintenance = result;
            RenderResult(result);
            _log(L10n.F("log.maint.done", result.Actions.Count, result.Deltas.Count));
        }
        catch (Exception ex)
        {
            _log(L10n.F("log.maint.fail", ex.Message));
            MessageBox.Show(this, L10n.F("maint.err.run", ex.Message), L10n.T("maint.title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetRunning(false); }
    }

    private MaintenancePlan BuildPlan(List<string> selected)
    {
        var saver = SaverCombo.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => (bool?)null,
        };
        return new MaintenancePlan
        {
            TrimCaches = ChkTrim.IsChecked == true,
            ClearCachePackages = ChkClearCache.IsChecked == true ? selected : [],
            ForceStopPackages = ChkForceStop.IsChecked == true ? selected : [],
            KillBackgroundApps = ChkKillAll.IsChecked == true,
            ZeroAnimations = ChkAnimOff.IsChecked == true,
            RestoreAnimations = ChkAnimRestore.IsChecked == true
                ? _report.Maintenance?.AnimationsBefore : null,
            BatterySaver = saver,
            OpenBatteryOptimization = ChkBatteryOpt.IsChecked == true,
        };
    }

    /// <summary>Depois do CORRER: os inputs travam e o GUARDAR aparece.</summary>
    private void SetRunning(bool running)
    {
        RunBtn.IsEnabled = !running;
        RunBtn.Content = running ? L10n.T("maint.running") : L10n.Btn("btn.run");
        AppsList.IsEnabled = !running;
        MeasureAgainBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        foreach (var cb in new[] { ChkTrim, ChkClearCache, ChkForceStop, ChkKillAll,
                     ChkAnimOff, ChkAnimRestore, ChkBatteryOpt })
            cb.IsEnabled = !running;
        SaverCombo.IsEnabled = !running;
    }

    private void RenderResult(MaintenanceReport result)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var a in result.Actions)
        {
            var status = a.Status switch
            {
                "ok" => "OK",
                "partial" => "PARCIAL",
                "fail" => "FALHA",
                _ => "IGNORADO",
            };
            sb.AppendLine($"{status,-8} {a.LabelPt}" +
                (string.IsNullOrWhiteSpace(a.Detail) ? "" : $" — {a.Detail}"));
        }
        sb.AppendLine();
        foreach (var d in result.Deltas)
        {
            var delta = d.Delta is null ? "—" : d.Delta;
            sb.AppendLine($"{d.LabelPt}: {(d.Before ?? "—")} → {(d.After ?? "—")} ({delta})" +
                (d.Direction == "neutral" ? "" : $" · {d.Direction}"));
        }
        ResultsText.Text = sb.ToString().TrimEnd();
        ResultsPanel.Visibility = Visibility.Visible;
        SaveBtn.Visibility = Visibility.Visible;
        SaveBtn.IsDefault = true;
        RunBtn.IsDefault = false;
        // Uma limpeza com animações a 0 guarda os valores anteriores — a opção
        // de repor passa a existir já neste diálogo.
        ChkAnimRestore.Visibility = result.AnimationsBefore is null
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Saved = true;
        DialogResult = true;
        Close();
    }
}
