using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OficinaDiag.Devices;

namespace OficinaDiag;

/// <summary>Uma linha do diff antes/depois — check, valor antigo, valor novo.</summary>
public sealed class DiffRow
{
    public required string Key { get; init; }
    public required string Before { get; init; }
    public required string Now { get; init; }
    public required Brush Brush { get; init; }
}

/// <summary>
/// Antes/depois do mesmo serial — compara dois scans do histórico local.
/// Prova para o cliente de que a peça nova ficou bem; munição para disputas.
/// </summary>
public sealed partial class CompareWindow : Window
{
    private readonly DeviceReport _current;
    private readonly IReadOnlyList<DeviceReport> _previous;

    private static readonly Dictionary<string, int> Severity = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fail"] = 3, ["warn"] = 2, ["skipped"] = 1, ["info"] = 0, ["pass"] = 0,
    };

    /// <summary>Entrada da lista: scan anterior + etiqueta legível.</summary>
    private sealed record ScanEntry(DeviceReport Report, string Label)
    {
        public override string ToString() => Label;
    }

    public CompareWindow(DeviceReport current, IReadOnlyList<DeviceReport> previous)
    {
        InitializeComponent();
        _current = current;
        _previous = previous;

        Title = L10n.T("cmp.title");
        HeaderText.Text = L10n.T("cmp.header");
        DescText.Text = L10n.T("cmp.desc");
        ColCheck.Text = L10n.T("cmp.col.check");
        ColBefore.Text = L10n.T("cmp.col.before");
        ColNow.Text = L10n.T("cmp.col.now");
        CloseBtn.Content = L10n.Btn("btn.close");

        foreach (var p in _previous)
        {
            var checks = p.Results.Count;
            var grade = p.Results.TryGetValue("grade.overall", out var g)
                ? L10n.F("cmp.grade", g.Value) : "";
            // Health score gravado no scan (scans antigos não têm — a etiqueta
            // simplesmente omite; a evolução aparece quando há dois valores).
            var health = p.Results.TryGetValue("health.score", out var hc) && hc.Value != "—"
                ? $" · saúde {hc.Value}" : "";
            ScanList.Items.Add(new ScanEntry(p,
                $"{p.CollectedAt:dd-MM-yyyy HH:mm} · {L10n.F("cmp.checks", checks)}{grade}{health}"));
        }
        if (ScanList.Items.Count > 0) ScanList.SelectedIndex = ScanList.Items.Count - 1;
    }

    private void ScanList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ScanList.SelectedItem is not ScanEntry entry) return;
        var past = entry.Report;

        var dim = (Brush)Application.Current.FindResource("DimBrush");
        var pass = (Brush)Application.Current.FindResource("PassBrush");
        var fail = (Brush)Application.Current.FindResource("FailBrush");

        var rows = new List<DiffRow>();
        foreach (var key in _current.Results.Keys.Union(past.Results.Keys)
                     .Where(k => !k.StartsWith("grade.", StringComparison.Ordinal)
                              && !k.StartsWith("suggest.", StringComparison.Ordinal))
                     .OrderBy(k => k))
        {
            var hasOld = past.Results.TryGetValue(key, out var old);
            var hasNew = _current.Results.TryGetValue(key, out var now);
            var before = hasOld ? $"{old!.Value ?? "—"} [{old.Status}]" : "—";
            var nowText = hasNew ? $"{now!.Value ?? "—"} [{now.Status}]" : "—";
            var oldSev = hasOld ? Severity.GetValueOrDefault(old!.Status) : -1;
            var newSev = hasNew ? Severity.GetValueOrDefault(now!.Status) : -1;
            // health.* é um score 0–100: 96→78 são ambos "pass" mas é uma
            // descida real — a cor segue o número, não o estado.
            var healthTrend = key.StartsWith("health.", StringComparison.Ordinal)
                && hasOld && hasNew
                && TryScore(old!.Value, out var oldScore)
                && TryScore(now!.Value, out var newScore)
                ? newScore.CompareTo(oldScore)
                : 0;
            var brush = !hasOld || !hasNew ? dim
                : healthTrend != 0 ? (healthTrend > 0 ? pass : fail)
                : newSev > oldSev ? fail
                : newSev < oldSev ? pass
                : now!.Value == old!.Value ? dim : pass;
            rows.Add(new DiffRow { Key = key, Before = before, Now = nowText, Brush = brush });
        }
        DiffList.ItemsSource = rows;
    }

    /// <summary>Extrai o número de um valor "87/100" — -1 quando não é score.</summary>
    private static bool TryScore(string? value, out int score)
    {
        score = -1;
        if (value is null) return false;
        var head = value.Split('/')[0].Trim();
        return int.TryParse(head, out score) && score >= 0;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
