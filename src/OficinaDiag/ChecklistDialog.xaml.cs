using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OficinaDiag.Devices;
using OficinaDiag.Grading;

namespace OficinaDiag;

/// <summary>
/// Checklist físico — o que o USB não vê (botões, gaveta SIM, porta de carga,
/// humidade…). O técnico marca cada item em segundos; o resultado entra no
/// mesmo relatório como checks checklist.* e alimenta o grau A–D.
/// </summary>
public sealed partial class ChecklistDialog : Window
{
    private readonly DeviceReport _report;
    // estado por item: 1 = ok · 2 = defeito · 3 = não testado
    private readonly Dictionary<string, int> _state = new();
    private readonly Dictionary<string, (Button ok, Button fail, Button na)> _row = new();

    public ChecklistDialog(DeviceReport report)
    {
        InitializeComponent();
        _report = report;

        Title = L10n.T("chk.title");
        HeaderText.Text = L10n.T("chk.header");
        DescText.Text = L10n.T("chk.desc");
        NotesLabel.Text = L10n.T("chk.notes");
        PhotoBtn.Content = L10n.Btn("chk.photo");
        ApplyBtn.Content = L10n.Btn("btn.apply");
        CancelBtn.Content = L10n.Btn("btn.cancel");
        NotesBox.MaxLength = Cloud.IntakeRules.NotesMaxLength;
        NotesBox.Text = _report.Notes ?? "";
        SaleBox.Content = L10n.T("chk.sale");
        SaleBox.IsChecked = _report.Purpose == "sale";

        foreach (var item in Grader.ChecklistItems)
            ItemsPanel.Children.Add(BuildRow(item));
        RefreshPhotoCount();
    }

    private Grid BuildRow(ChecklistItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = L10n.Current == "en" ? item.LabelEn : item.LabelPt,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            Margin = new Thickness(0),
        };
        grid.Children.Add(label);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = MkButton("ok");
        var fail = MkButton(L10n.T("chk.fail"));
        var na = MkButton(L10n.T("chk.na"));
        ok.Click += (_, _) => SetState(item.Key, 1);
        fail.Click += (_, _) => SetState(item.Key, 2);
        na.Click += (_, _) => SetState(item.Key, 3);
        buttons.Children.Add(ok);
        buttons.Children.Add(fail);
        buttons.Children.Add(na);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        _row[item.Key] = (ok, fail, na);

        // prefill — reabrir o checklist mostra as marcas anteriores
        if (_report.Results.TryGetValue(item.Key, out var prev))
            _state[item.Key] = prev.Status switch
            {
                "pass" => 1,
                "warn" or "fail" => 2,
                _ => 3,
            };
        Paint(item.Key);
        return grid;
    }

    private static Button MkButton(string text) => new()
    {
        Content = text,
        Margin = new Thickness(4, 0, 0, 0),
        Padding = new Thickness(10, 2, 10, 2),
        MinWidth = 44,
    };

    private void SetState(string key, int s)
    {
        _state[key] = _state[key] == s ? 0 : s; // tocar de novo desmarca
        Paint(key);
    }

    private void Paint(string key)
    {
        var (ok, fail, na) = _row[key];
        var s = _state.GetValueOrDefault(key);
        var pass = (Brush)Application.Current.FindResource("PassBrush");
        var warn = (Brush)Application.Current.FindResource("WarnBrush");
        var dim = (Brush)Application.Current.FindResource("DimBrush");
        var fg = (Brush)Application.Current.FindResource("FgBrush");
        ok.Foreground = s == 1 ? pass : fg;
        fail.Foreground = s == 2 ? warn : fg;
        na.Foreground = s == 3 ? dim : fg;
        ok.FontWeight = fail.FontWeight = na.FontWeight = FontWeights.Normal;
        if (s == 1) ok.FontWeight = FontWeights.Bold;
        if (s == 2) fail.FontWeight = FontWeights.Bold;
        if (s == 3) na.FontWeight = FontWeights.Bold;
    }

    private void Photo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Fotos|*.jpg;*.jpeg;*.png;*.webp",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            if (!_report.PhotoPaths.Contains(f))
                _report.PhotoPaths.Add(f);
        RefreshPhotoCount();
    }

    private void RefreshPhotoCount() =>
        PhotoCount.Text = _report.PhotoPaths.Count == 0
            ? "" : L10n.F("chk.photos", _report.PhotoPaths.Count);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Grader.ChecklistItems)
        {
            var s = _state.GetValueOrDefault(item.Key);
            if (s == 0) continue;
            var label = L10n.Current == "en" ? item.LabelEn : item.LabelPt;
            _report.Set(item.Key,
                s == 1 ? "pass" : s == 2 ? "fail" : "skipped",
                s == 1 ? "ok" : s == 2 ? "defeito" : "não testado",
                label);
        }
        var notes = NotesBox.Text.Trim();
        // MaxLength trava a escrita, mas notas antigas (histórico) podem vir maiores.
        if (!Cloud.IntakeRules.NotesWithinLimit(notes))
        {
            MessageBox.Show(this, L10n.F("err.notes.long", Cloud.IntakeRules.NotesMaxLength),
                L10n.T("chk.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _report.Notes = notes;
        _report.Purpose = SaleBox.IsChecked == true ? "sale" : null;
        DialogResult = true;
    }
}
