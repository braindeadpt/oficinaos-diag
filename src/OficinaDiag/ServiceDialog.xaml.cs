using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using OficinaDiag.Templates;

namespace OficinaDiag;

/// <summary>
/// Template de serviço (Fase E.6) — o técnico escolhe o tipo de atendimento
/// (troca de bateria, troca de ecrã, compra de usado, otimização, migração
/// WhatsApp) e marca o checklist específico desse serviço. Os itens entram
/// no relatório como checks tpl.&lt;serviço&gt;.* — prefixo próprio para não
/// colidir com os checks genéricos de scan/checklist físico.
/// </summary>
public sealed partial class ServiceDialog : Window
{
    private readonly DeviceReport _report;
    // estado por check completo (tpl.<id>.<item>) — item keys repetem-se
    // entre templates ("water", "function"), por isso a chave leva o serviço.
    private readonly Dictionary<string, int> _state = new();
    private readonly Dictionary<string, (Button ok, Button fail, Button na)> _row = new();
    private readonly Dictionary<string, TextBox> _measure = new();
    // Texto das medições sobrevive à troca de template — as TextBox são
    // recriadas em cada seleção e sem isto o que o técnico digitou perdia-se.
    private readonly Dictionary<string, string> _measureText = new();
    private ServiceTemplate? _selected;

    /// <summary>
    /// Template aplicado no APLICAR. Quando <see cref="ServiceTemplate.OpensMaintenanceDialog"/>
    /// é true nada foi escrito — quem chamou abre a janela de limpeza (o
    /// serviço de otimização já existe como fluxo próprio, não se duplica).
    /// </summary>
    public ServiceTemplate? Applied { get; private set; }

    public ServiceDialog(DeviceReport report)
    {
        InitializeComponent();
        _report = report;

        Title = L10n.T("svc.title");
        HeaderText.Text = L10n.T("svc.header");
        DescText.Text = L10n.T("svc.desc");
        PickLabel.Text = L10n.T("svc.pick");
        ApplyBtn.Content = L10n.Btn("btn.apply");
        CancelBtn.Content = L10n.Btn("btn.cancel");

        foreach (var tpl in ServiceTemplates.All)
            TemplatePanel.Children.Add(BuildTemplateRadio(tpl));

        // Reabrir mostra o serviço já aplicado — prefill das marcas e das
        // medições guardadas nos checks tpl.* do relatório.
        var appliedId = _report.Results.TryGetValue(ServiceTemplates.AppliedKey, out var c)
            ? c.Value : null;
        var applied = ServiceTemplates.Find(appliedId);
        // Seleção inicial: o template aplicado, ou o primeiro da lista.
        var initial = TemplatePanel.Children
            .OfType<RadioButton>()
            .FirstOrDefault(rb => ReferenceEquals(rb.Tag, applied))
            ?? TemplatePanel.Children.OfType<RadioButton>().FirstOrDefault();
        if (initial is not null) initial.IsChecked = true;

        // Mesma moldura do ChecklistDialog: a janela nunca excede a work area.
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
    }

    private RadioButton BuildTemplateRadio(ServiceTemplate tpl)
    {
        var rb = new RadioButton
        {
            GroupName = "svc",
            Tag = tpl,
            Margin = new Thickness(0, 5, 0, 5),
            Cursor = Cursors.Hand,
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = L10n.Current == "en" ? tpl.LabelEn : tpl.LabelPt,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        });
        sp.Children.Add(new TextBlock
        {
            Text = L10n.Current == "en" ? tpl.DescriptionEn : tpl.DescriptionPt,
            TextWrapping = TextWrapping.Wrap,
        });
        rb.Content = sp;
        rb.Checked += (_, _) => SelectTemplate(tpl);
        return rb;
    }

    /// <summary>Troca o painel de itens para o template escolhido + prefill.</summary>
    private void SelectTemplate(ServiceTemplate tpl)
    {
        _selected = tpl;
        // Snapshot do que está digitado antes de deitar fora as TextBox.
        foreach (var (ck, box) in _measure)
            if (!string.IsNullOrWhiteSpace(box.Text)) _measureText[ck] = box.Text;
        ItemsPanel.Children.Clear();
        _row.Clear();
        _measure.Clear();
        // _state e _measureText ficam: marcas e medições de outros templates
        // sobrevivem à troca de seleção dentro do mesmo diálogo.

        foreach (var item in tpl.Items)
            ItemsPanel.Children.Add(BuildRow(tpl, item));

        if (tpl.RequiredChecks.Count > 0)
        {
            RequiredLabel.Text = L10n.T("svc.required");
            RequiredLabel.Visibility = Visibility.Visible;
            RequiredText.Text = string.Join("\n", tpl.RequiredChecks.Select(RequiredLine));
            RequiredText.Visibility = Visibility.Visible;
        }
        else
        {
            RequiredLabel.Visibility = Visibility.Collapsed;
            RequiredText.Visibility = Visibility.Collapsed;
        }

        // Otimização não tem checklist tpl.* — usa a janela de limpeza medida.
        if (tpl.OpensMaintenanceDialog)
        {
            MaintHint.Text = L10n.T("svc.maint.hint");
            MaintHint.Visibility = Visibility.Visible;
        }
        else MaintHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Linha das medições que o scan deve trazer — com o valor atual ou "em falta".</summary>
    private string RequiredLine(string key)
    {
        if (_report.Results.TryGetValue(key, out var c))
            return $"• {key} — {c.Value ?? c.Status}";
        return $"• {key} — {L10n.T("svc.missing")}";
    }

    private Grid BuildRow(ServiceTemplate tpl, ServiceTemplateItem item)
    {
        var checkKey = ServiceTemplates.CheckKey(tpl.Id, item.Key);
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        // label | [medição] | botões
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (item.Measurement)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = L10n.Current == "en" ? item.LabelEn : item.LabelPt,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        };
        grid.Children.Add(label);

        var col = 1;
        TextBox? box = null;
        if (item.Measurement)
        {
            box = new TextBox
            {
                Width = 110,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = L10n.Current == "en" ? item.LabelEn : item.LabelPt,
                Text = _measureText.GetValueOrDefault(checkKey, ""),
            };
            grid.Children.Add(box);
            Grid.SetColumn(box, col++);
            _measure[checkKey] = box;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var ok = MkButton(L10n.T("chk.ok"));
        var fail = MkButton(L10n.T("chk.fail"));
        var na = MkButton(L10n.T("chk.na"));
        ok.Click += (_, _) => SetState(checkKey, 1);
        fail.Click += (_, _) => SetState(checkKey, 2);
        na.Click += (_, _) => SetState(checkKey, 3);
        buttons.Children.Add(ok);
        buttons.Children.Add(fail);
        buttons.Children.Add(na);
        grid.Children.Add(buttons);
        Grid.SetColumn(buttons, col);
        _row[checkKey] = (ok, fail, na);

        // prefill — reabrir mostra as marcas/medições anteriores. Só entra o
        // que o técnico ainda não tocou nesta sessão: uma marca pendente
        // (ainda não aplicada) ganha sempre ao valor velho do relatório.
        if (!_state.ContainsKey(checkKey)
            && _report.Results.TryGetValue(checkKey, out var prev))
        {
            _state[checkKey] = prev.Status switch
            {
                "pass" => 1,
                "warn" or "fail" => 2,
                _ => 3,
            };
            // O valor guardado numa medição é o texto do técnico; os literais
            // "ok"/"defeito" são de itens de verificação — não vão para a caixa.
            if (box is not null && !_measureText.ContainsKey(checkKey)
                && !string.IsNullOrWhiteSpace(prev.Value)
                && prev.Value is not "ok" and not "defeito" and not "não testado")
                box.Text = prev.Value;
        }
        Paint(checkKey);
        return grid;
    }

    private static Button MkButton(string text) => new()
    {
        Content = text,
        Margin = new Thickness(4, 0, 0, 0),
        Padding = new Thickness(10, 2, 10, 2),
        MinWidth = 44,
    };

    private void SetState(string checkKey, int s)
    {
        // Toggle lê com TryGetValue — item sem marca vale 0 (mesma regra do
        // checklist físico; o indexer direto rebentava no primeiro toque).
        ChecklistMarks.Toggle(_state, checkKey, s);
        Paint(checkKey);
    }

    private void Paint(string checkKey)
    {
        var (ok, fail, na) = _row[checkKey];
        var s = _state.GetValueOrDefault(checkKey);
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

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var tpl = _selected;
        if (tpl is null) return;
        // Otimização/manutenção: nada a gravar aqui — o serviço já existe como
        // janela de limpeza medida. Quem chamou abre-a a seguir.
        if (tpl.OpensMaintenanceDialog)
        {
            Applied = tpl;
            DialogResult = true;
            return;
        }

        // _state/_measure estão chaveados por check completo; o Core quer-os
        // por item.Key — a tradução faz-se aqui, uma vez.
        var marcas = new Dictionary<string, int>();
        var medidas = new Dictionary<string, string>();
        foreach (var item in tpl.Items)
        {
            var ck = ServiceTemplates.CheckKey(tpl.Id, item.Key);
            if (_state.TryGetValue(ck, out var m)) marcas[item.Key] = m;
            if (_measure.TryGetValue(ck, out var box) && !string.IsNullOrWhiteSpace(box.Text))
            {
                medidas[item.Key] = box.Text;
                _measureText[ck] = box.Text;
            }
        }
        ServiceTemplates.Apply(_report, tpl, marcas, medidas,
            item => L10n.Current == "en" ? item.LabelEn : item.LabelPt);
        Applied = tpl;
        DialogResult = true;
    }
}
