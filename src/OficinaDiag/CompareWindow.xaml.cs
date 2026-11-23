using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OficinaDiag.Devices;
using OficinaDiag.Health;

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

        // Cobertura do health score = categorias com medições — recomputada
        // dos checks (HealthScore.Compute é puro), não das entradas
        // health.<cat> gravadas: relatórios antigos podem trazê-las
        // desatualizadas. Uma subida de score com MENOS categorias medidas
        // não é melhoria confirmada — pode ser um check que falhava e
        // passou a «não testado».
        static int Cobertura(DeviceReport r) =>
            HealthScore.Compute(r).Categories.Count(c => c.Score is not null);
        var oldCov = Cobertura(past);
        var newCov = Cobertura(_current);

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
            Brush brush;
            if (key.StartsWith("health.", StringComparison.Ordinal))
            {
                // health.* é score 0–100: a cor segue o número e a cobertura,
                // nunca o estado — 96→78 são ambos "pass" mas é descida, e
                // um score perdido por falta de dados (→ skipped) não é
                // melhoria nem queda do aparelho: cinza.
                var isScore = key == "health.score";
                brush = HealthScore.CompareRow(
                        hasOld ? old : null, hasNew ? now : null,
                        isScore ? oldCov : null, isScore ? newCov : null) switch
                {
                    HealthRowTrend.Improved => pass,
                    HealthRowTrend.Declined => fail,
                    _ => dim,
                };
            }
            else
            {
                var oldSev = hasOld ? Severity.GetValueOrDefault(old!.Status) : -1;
                var newSev = hasNew ? Severity.GetValueOrDefault(now!.Status) : -1;
                brush = !hasOld || !hasNew ? dim
                    : newSev > oldSev ? fail
                    : newSev < oldSev ? pass
                    : now!.Value == old!.Value ? dim : pass;
            }
            rows.Add(new DiffRow { Key = key, Before = before, Now = nowText, Brush = brush });
        }
        DiffList.ItemsSource = rows;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
