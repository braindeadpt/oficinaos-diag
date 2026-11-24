using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using OficinaDiag.Devices;
using OficinaDiag.Ui;

namespace OficinaDiag;

/// <summary>
/// Gestor de apps iOS — inventário via ideviceinstaller (installation_proxy)
/// com desinstalação de apps de utilizador. Só leitura + uninstall: o iOS
/// não expõe disable/enable/restore por esta via.
/// </summary>
public sealed partial class IosAppDialog : Window
{
    private sealed class AppRow
    {
        public required IosAppEntry Entry { get; init; }
        public required string BundleId { get; init; }
        public required string Kind { get; init; }
        public required string Version { get; init; }
        public required string Size { get; init; }
        public required string Note { get; init; }
        public required long SizeSort { get; init; }
        public required System.Version? VersionNumber { get; init; }
    }

    private readonly IosAppManager _manager;
    private readonly string _udid;
    private IosAppInventory? _inventory;
    private List<AppRow> _allRows = [];
    private AppRow? _selected;
    private bool _running;
    private static readonly string[] FilterModes = ["all", "user", "system"];
    private static readonly string[] SortModes = ["bundle", "kind", "version", "size", "note"];

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

    public IosAppDialog(IosAppManager manager, string udid, string deviceLabel)
    {
        InitializeComponent();
        _manager = manager;
        _udid = udid;

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

        Title = L10n.T("iosapps.title");
        HeaderText.Text = L10n.Section("apps.header");
        DescText.Text = L10n.T("iosapps.desc");
        DeviceText.Text = deviceLabel;
        RefreshBtn.Content = L10n.Btn("apps.btn.refresh");
        UninstallBtn.Content = L10n.Btn("apps.btn.uninstall");
        CloseBtn.Content = L10n.Btn("btn.close");
        SearchBox.ToolTip = L10n.T("iosapps.search");

        var labels = new[]
        {
            L10n.T("apps.filter.all"), L10n.T("apps.filter.user"), L10n.T("apps.filter.system"),
        };
        FilterBox.ItemsSource = labels;
        FilterBox.SelectedIndex = 0;
        ColBundle.Text = L10n.T("apps.col.bundle");
        ColKind.Text = L10n.T("apps.col.kind");
        ColVersion.Text = L10n.T("apps.col.version");
        ColSize.Text = L10n.T("apps.col.size");
        ColNote.Text = L10n.T("apps.col.note");
        SortBox.ItemsSource = new[]
        {
            L10n.T("apps.sort.bundle"), L10n.T("apps.sort.kind"), L10n.T("apps.sort.version"),
            L10n.T("apps.sort.size"), L10n.T("apps.sort.note"),
        };
        SortBox.SelectedIndex = 0;
        AutomationProperties.SetName(SortBox, L10n.T("apps.sort.label"));
        AutomationProperties.SetName(SearchBox, L10n.T("iosapps.search"));
        AutomationProperties.SetName(AppsList, L10n.T("iosapps.title"));
        AddLog(L10n.T("iosapps.log.loading"));
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
            _inventory = await _manager.ListAsync(_udid);
            if (_inventory.Error is not null)
            {
                AddLog(L10n.F("apps.log.error", InventoryErrorText(_inventory.Error)));
                // Refresh falhado: a grelha não pode continuar a mostrar o
                // inventário anterior como se fosse actual — limpa já.
                ClearGrid();
            }
            else
            {
                _allRows = _inventory.Apps.Select(ToRow).ToList();
                var selected = AppsList.SelectedItem as AppRow;
                ApplyFilter();
                if (selected is not null)
                    AppsList.SelectedItem = _allRows.FirstOrDefault(r => r.BundleId == selected.BundleId);
                AddLog(L10n.F("apps.log.loaded", _allRows.Count));
            }
        }
        catch (Exception ex)
        {
            AddLog(L10n.F("apps.log.error", ex.Message));
            ClearGrid();
        }
        finally
        {
            _running = false;
            StatusText.Text = "";
            UpdateActions();
        }
    }

    /// <summary>Inventário inválido: limpa linhas, selecção e detalhe —
    /// uma lista velha nunca pode parecer o estado actual do iPhone.</summary>
    private void ClearGrid()
    {
        AppsList.ItemsSource = null;
        _allRows = [];
        _inventory = null;
        _selected = null;
        DetailsText.Text = "";
    }

    private AppRow ToRow(IosAppEntry e)
    {
        var kind = e.Kind == IosAppKind.User ? L10n.T("apps.kind.user") : L10n.T("apps.kind.system");
        return new AppRow
        {
            Entry = e,
            BundleId = e.BundleId,
            Kind = kind,
            Version = e.Version ?? "—",
            Size = e.HasUsage ? IosAppDecisions.FormatSize(e.TotalBytes) : "—",
            // Componente não reportado é "—", nunca "0 KB" — zero medido
            // pelo iOS é diferente de medição em falta.
            Note = e.HasUsage
                ? L10n.F("iosapps.usage",
                    e.StaticBytes is long s ? IosAppDecisions.FormatSize(s) : "—",
                    e.DynamicBytes is long d ? IosAppDecisions.FormatSize(d) : "—")
                : L10n.T("iosapps.usage.none"),
            SizeSort = e.HasUsage ? e.TotalBytes : long.MaxValue,
            VersionNumber = ParseVersion(e.Version),
        };
    }

    private static System.Version? ParseVersion(string? raw) =>
        System.Version.TryParse(raw, out var v) ? v : null;

    private void ApplyFilter()
    {
        var mode = FilterModes[Math.Clamp(FilterBox.SelectedIndex, 0, FilterModes.Length - 1)];
        var search = SearchBox.Text.Trim();
        var rows = _allRows.Where(r => mode switch
        {
            "user" => r.Entry.Kind == IosAppKind.User,
            "system" => r.Entry.Kind == IosAppKind.System,
            _ => true,
        });
        if (search.Length > 0)
            rows = rows.Where(r => r.BundleId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Entry.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
        AppsList.ItemsSource = SortRows(rows);
        if (AppsList.Items.Count == 0) DetailsText.Text = L10n.T("apps.empty");
    }

    private List<AppRow> SortRows(IEnumerable<AppRow> rows)
    {
        var mode = SortModes[Math.Clamp(SortBox.SelectedIndex, 0, SortModes.Length - 1)];
        return mode switch
        {
            "kind" => rows.OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase).ToList(),
            "version" => rows.OrderBy(r => r.VersionNumber is null)
                .ThenBy(r => r.VersionNumber)
                .ThenBy(r => r.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase).ToList(),
            "size" => rows.OrderBy(r => r.SizeSort).ThenBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase).ToList(),
            "note" => rows.OrderBy(r => r.Note, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => rows.OrderBy(r => r.BundleId, StringComparer.OrdinalIgnoreCase).ToList(),
        };
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
        DetailsText.Text = L10n.F("iosapps.details", e.BundleId, e.DisplayName,
            _selected.Kind, _selected.Version, _selected.Size, _selected.Note,
            e.Kind == IosAppKind.User ? "" : L10n.T("iosapps.protect.system"));
    }

    private void UpdateActions()
    {
        UninstallBtn.IsEnabled = !_running && _selected?.Entry.CanUninstall == true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Sort_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var row = _selected;
        if (row is null || !row.Entry.CanUninstall) return;
        if (MessageBox.Show(this, L10n.F("iosapps.confirm.uninstall", row.BundleId), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _running = true;
        UpdateActions();
        StatusText.Text = L10n.T("apps.status.loading");
        try
        {
            var r = await _manager.UninstallAsync(_udid, row.BundleId);
            if (r.Ok)
            {
                AddLog(L10n.F("apps.log.action.ok", L10n.T("apps.action.uninstall"),
                    row.BundleId, string.IsNullOrWhiteSpace(r.Output) ? "ok" : r.Output.Trim()));
            }
            else
            {
                AddLog(L10n.F("apps.log.action.fail", L10n.T("apps.action.uninstall"),
                    row.BundleId, ActionErrorText(r.Error)));
            }
        }
        catch (Exception ex)
        {
            AddLog(L10n.F("apps.log.action.fail", L10n.T("apps.action.uninstall"),
                row.BundleId, ex.Message));
        }
        finally
        {
            _running = false;
            StatusText.Text = "";
            await RefreshAsync();
        }
    }

    /// <summary>Tokens de erro do inventário → texto L10n do técnico.</summary>
    private static string InventoryErrorText(string error) => error switch
    {
        "parse" => L10n.T("iosapps.err.parse"),
        "empty" => L10n.T("iosapps.err.empty"),
        _ => L10n.T("iosapps.err.tool"),
    };

    /// <summary>Tokens de erro da desinstalação → texto L10n do técnico.</summary>
    private static string ActionErrorText(string? error) => error switch
    {
        "invalid" => L10n.T("iosapps.err.invalid"),
        "protected" => L10n.T("iosapps.err.protected"),
        "notfound" => L10n.T("iosapps.err.notfound"),
        "verify" => L10n.T("iosapps.err.verify"),
        "parse" => L10n.T("iosapps.err.parse"),
        "empty" => L10n.T("iosapps.err.empty"),
        _ => L10n.T("iosapps.err.tool"),
    };

    private void AddLog(string line)
    {
        LogText.AppendText(line + Environment.NewLine);
        LogText.ScrollToEnd();
    }
}
