using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OficinaDiag.Devices;
using OficinaDiag.Ui;

namespace OficinaDiag;

public sealed partial class AppManagerDialog : Window
{
    private sealed class AppRow
    {
        public required AndroidAppEntry Entry { get; init; }
        public required string Package { get; init; }
        public required string Kind { get; init; }
        public required string State { get; init; }
        public required string Installer { get; init; }
        public required string Protection { get; init; }
        public required IReadOnlyList<AndroidAppAction> Actions { get; init; }
    }

    private readonly AndroidAppManager _manager;
    private readonly string _serial;
    private AndroidAppInventory? _inventory;
    private List<AppRow> _allRows = [];
    private AppRow? _selected;
    private AndroidAppAction? _lastUndo;
    private bool _running;
    private static readonly string[] FilterModes = ["all", "user", "system", "disabled", "uninstalled"];

    private const uint MonitorDefaultToNearest = 2;

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

    private (double Width, double Height) WorkAreaDoMonitor()
    {
        var wa = SystemParameters.WorkArea;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return (wa.Width, wa.Height);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
                return (wa.Width, wa.Height);
            var escala = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (!(escala > 0)) escala = 1.0;
            var dpi = 96 * escala;
            var r = info.rcWork;
            return (WindowFit.ParaDip(r.Right - r.Left, dpi),
                WindowFit.ParaDip(r.Bottom - r.Top, dpi));
        }
        catch { return (wa.Width, wa.Height); }
    }

    public AppManagerDialog(AndroidAppManager manager, string serial, string deviceLabel)
    {
        InitializeComponent();
        _manager = manager;
        _serial = serial;

        // O limite é o da work area do monitor onde o diálogo nasce — um
        // secundário mais pequeno que o principal não pode ficar de fora.
        Loaded += (_, _) =>
        {
            var (w, h) = WorkAreaDoMonitor();
            MaxWidth = Math.Min(MaxWidth, w);
            MaxHeight = Math.Min(MaxHeight, h);
            MinWidth = Math.Min(MinWidth, MaxWidth);
            MinHeight = Math.Min(MinHeight, MaxHeight);
        };

        Title = L10n.T("apps.title");
        HeaderText.Text = L10n.T("apps.header");
        DescText.Text = L10n.T("apps.desc");
        DeviceText.Text = deviceLabel;
        RefreshBtn.Content = L10n.Btn("apps.btn.refresh");
        UninstallBtn.Content = L10n.Btn("apps.btn.uninstall");
        DisableBtn.Content = L10n.Btn("apps.btn.disable");
        EnableBtn.Content = L10n.Btn("apps.btn.enable");
        RestoreBtn.Content = L10n.Btn("apps.btn.restore");
        ForceStopBtn.Content = L10n.Btn("apps.btn.forceStop");
        UndoBtn.Content = L10n.Btn("apps.btn.undo");
        CloseBtn.Content = L10n.Btn("btn.close");
        SearchBox.ToolTip = L10n.T("apps.search");

        var labels = new[]
        {
            L10n.T("apps.filter.all"), L10n.T("apps.filter.user"), L10n.T("apps.filter.system"),
            L10n.T("apps.filter.disabled"), L10n.T("apps.filter.uninstalled"),
        };
        FilterBox.ItemsSource = labels;
        FilterBox.SelectedIndex = 0;
        AddLog(L10n.T("apps.log.loading"));
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_running) return;
        _running = true;
        StatusText.Text = L10n.T("apps.status.loading");
        UpdateActions();
        try
        {
            _inventory = await _manager.ListAsync(_serial);
            if (_inventory.Error is not null)
            {
                AddLog(L10n.F("apps.log.error", _inventory.Error));
                _allRows = [];
            }
            else
            {
                _allRows = _inventory.Apps.Select(ToRow).ToList();
                var selected = AppsList.SelectedItem as AppRow;
                ApplyFilter();
                if (selected is not null)
                    AppsList.SelectedItem = _allRows.FirstOrDefault(r => r.Package == selected.Package);
                AddLog(L10n.F("apps.log.loaded", _allRows.Count));
            }
        }
        catch (Exception ex)
        {
            AddLog(L10n.F("apps.log.error", ex.Message));
        }
        finally
        {
            _running = false;
            StatusText.Text = "";
            UpdateActions();
        }
    }

    private AppRow ToRow(AndroidAppEntry e)
    {
        var kind = e.Kind == AndroidAppKind.User ? L10n.T("apps.kind.user") : L10n.T("apps.kind.system");
        var state = e.State switch
        {
            AndroidAppState.Enabled => L10n.T("apps.state.enabled"),
            AndroidAppState.Disabled => L10n.T("apps.state.disabled"),
            _ => L10n.T("apps.state.uninstalled"),
        };
        var protection = e.ProtectionReason switch
        {
            "critical" => L10n.T("apps.protect.critical"),
            "launcher" => L10n.T("apps.protect.launcher"),
            "invalid" => L10n.T("apps.protect.invalid"),
            _ => "",
        };
        return new AppRow
        {
            Entry = e,
            Package = e.PackageName,
            Kind = kind,
            State = state,
            Installer = e.InstallerPackageName ?? "—",
            Protection = protection,
            Actions = e.Actions,
        };
    }

    private void ApplyFilter()
    {
        var mode = FilterModes[Math.Clamp(FilterBox.SelectedIndex, 0, FilterModes.Length - 1)];
        var search = SearchBox.Text.Trim();
        var rows = _allRows.Where(r => mode switch
        {
            "user" => r.Entry.Kind == AndroidAppKind.User,
            "system" => r.Entry.Kind == AndroidAppKind.System,
            "disabled" => r.Entry.State == AndroidAppState.Disabled,
            "uninstalled" => r.Entry.State == AndroidAppState.Uninstalled,
            _ => true,
        });
        if (search.Length > 0)
            rows = rows.Where(r => r.Package.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.Entry.InstallerPackageName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        AppsList.ItemsSource = rows.ToList();
        if (AppsList.Items.Count == 0) DetailsText.Text = L10n.T("apps.empty");
    }

    private void AppsList_Changed(object sender, SelectionChangedEventArgs e)
    {
        _selected = AppsList.SelectedItem as AppRow;
        UpdateDetails();
        UpdateActions();
    }

    private void UpdateDetails()
    {
        if (_selected is null) { DetailsText.Text = ""; return; }
        var e = _selected.Entry;
        DetailsText.Text = L10n.F("apps.details", e.PackageName, _selected.Kind, _selected.State,
            _selected.Installer, _selected.Protection.Length > 0 ? _selected.Protection : "—");
    }

    private void UpdateActions()
    {
        bool Has(AndroidAppActionKind kind) =>
            !_running && _selected?.Actions.Any(a => a.Kind == kind) == true;
        UninstallBtn.IsEnabled = Has(AndroidAppActionKind.Uninstall);
        DisableBtn.IsEnabled = Has(AndroidAppActionKind.Disable);
        EnableBtn.IsEnabled = Has(AndroidAppActionKind.Enable);
        RestoreBtn.IsEnabled = Has(AndroidAppActionKind.Restore);
        ForceStopBtn.IsEnabled = Has(AndroidAppActionKind.ForceStop);
        UndoBtn.IsEnabled = !_running && _lastUndo is not null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Uninstall_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(AndroidAppActionKind.Uninstall);
    private async void Disable_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(AndroidAppActionKind.Disable);
    private async void Enable_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(AndroidAppActionKind.Enable);
    private async void Restore_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(AndroidAppActionKind.Restore);
    private async void ForceStop_Click(object sender, RoutedEventArgs e) =>
        await RunSelectedAsync(AndroidAppActionKind.ForceStop);

    private async Task RunSelectedAsync(AndroidAppActionKind kind)
    {
        var row = _selected;
        var action = row?.Actions.FirstOrDefault(a => a.Kind == kind);
        if (row is null || action is null) return;
        if (MessageBox.Show(this, ConfirmText(kind, row), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _running = true;
        UpdateActions();
        try
        {
            var r = await _manager.RunAsync(_serial, action, _inventory?.HomePackage);
            if (r.Ok)
            {
                _lastUndo = r.Undo;
                AddLog(L10n.F("apps.log.action.ok", ActionName(kind), row.Package,
                    string.IsNullOrWhiteSpace(r.Output) ? "ok" : r.Output.Trim()));
            }
            else
            {
                AddLog(L10n.F("apps.log.action.fail", ActionName(kind), row.Package, r.Error ?? "erro"));
            }
        }
        catch (Exception ex)
        {
            AddLog(L10n.F("apps.log.action.fail", ActionName(kind), row.Package, ex.Message));
        }
        finally
        {
            _running = false;
            await RefreshAsync();
        }
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var undo = _lastUndo;
        if (undo is null) return;
        if (MessageBox.Show(this, L10n.F("apps.confirm.undo", undo.PackageName), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _running = true;
        UpdateActions();
        try
        {
            var r = await _manager.RunAsync(_serial, undo, _inventory?.HomePackage);
            if (r.Ok)
            {
                _lastUndo = null;
                AddLog(L10n.F("apps.log.undo.ok", ActionName(undo.Kind), undo.PackageName));
            }
            else
            {
                AddLog(L10n.F("apps.log.action.fail", ActionName(undo.Kind), undo.PackageName,
                    r.Error ?? "erro"));
            }
        }
        catch (Exception ex)
        {
            AddLog(L10n.F("apps.log.action.fail", ActionName(undo.Kind), undo.PackageName, ex.Message));
        }
        finally
        {
            _running = false;
            await RefreshAsync();
        }
    }

    private string ConfirmText(AndroidAppActionKind kind, AppRow row) => kind switch
    {
        AndroidAppActionKind.Uninstall => L10n.F("apps.confirm.uninstall", row.Package),
        AndroidAppActionKind.Disable => L10n.F("apps.confirm.disable", row.Package),
        AndroidAppActionKind.Enable => L10n.F("apps.confirm.enable", row.Package),
        AndroidAppActionKind.Restore => L10n.F("apps.confirm.restore", row.Package),
        _ => L10n.F("apps.confirm.forceStop", row.Package),
    };

    private static string ActionName(AndroidAppActionKind kind) => kind switch
    {
        AndroidAppActionKind.Uninstall => L10n.T("apps.action.uninstall"),
        AndroidAppActionKind.Disable => L10n.T("apps.action.disable"),
        AndroidAppActionKind.Enable => L10n.T("apps.action.enable"),
        AndroidAppActionKind.Restore => L10n.T("apps.action.restore"),
        _ => L10n.T("apps.action.forceStop"),
    };

    private void AddLog(string line)
    {
        LogText.AppendText(line + Environment.NewLine);
        LogText.ScrollToEnd();
    }
}
