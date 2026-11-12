using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OficinaDiag.Cloud;
using OficinaDiag.History;

namespace OficinaDiag;

/// <summary>
/// Opções — idioma, tema e URL da cloud. Tema e idioma aplicam-se em preview
/// ao escolher; cancelar repõe o estado anterior.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly DiagConfig _config;
    private readonly ScanHistory? _history;
    private readonly string _origLang;
    private readonly string _origTheme;
    private readonly CancellationTokenSource _cts = new();
    private bool _closing;
    private bool _saving;

    public SettingsDialog(DiagConfig config, ScanHistory? history = null)
    {
        _config = config;
        _history = history;
        _origLang = L10n.Current;
        _origTheme = ThemeManager.Current;
        InitializeComponent();
        ApplyStrings();
        ThemeManager.Changed += ApplyStrings;

        LangPt.IsChecked = L10n.Current == "pt";
        LangEn.IsChecked = L10n.Current == "en";
        ThemeTerminal.IsChecked = ThemeManager.Current == ThemeManager.Terminal;
        ThemeWin95.IsChecked = ThemeManager.Current == ThemeManager.Win95;
        ThemeFluent.IsChecked = ThemeManager.Current == ThemeManager.Fluent;
        CloudBox.Text = config.CloudUrl;
        // Retenção RGPD: 30 / 90 / sem limite (0).
        RetentionBox.SelectedItem = RetentionBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == config.HistoryRetentionDays.ToString())
            ?? RetentionBox.Items[1];
        ClearHistoryBtn.Visibility = history is null ? Visibility.Collapsed : Visibility.Visible;

        LangPt.Checked += (_, _) => PreviewLang("pt");
        LangEn.Checked += (_, _) => PreviewLang("en");
        ThemeTerminal.Checked += (_, _) => PreviewTheme(ThemeManager.Terminal);
        ThemeWin95.Checked += (_, _) => PreviewTheme(ThemeManager.Win95);
        ThemeFluent.Checked += (_, _) => PreviewTheme(ThemeManager.Fluent);
    }

    private void ApplyStrings()
    {
        Title = L10n.T("set.title");
        HeaderText.Text = L10n.T("set.header");
        LangLabel.Text = L10n.T("set.lang");
        ThemeLabel.Text = L10n.T("set.theme");
        CloudLabel.Text = L10n.T("set.cloud");
        TokenLabel.Text = L10n.T("set.token");
        TokenHint.Text = _config.ShopTokenProtected is not null
            ? L10n.T("set.token.set") : L10n.T("set.token.unset");
        ThemeTerminal.Content = L10n.T("theme.terminal");
        ThemeWin95.Content = L10n.T("theme.win95");
        ThemeFluent.Content = L10n.T("theme.fluent");
        RetentionLabel.Text = L10n.T("set.hist.label");
        foreach (var item in RetentionBox.Items.OfType<ComboBoxItem>())
            item.Content = item.Tag?.ToString() switch
            {
                "30" => L10n.T("set.hist.30"),
                "90" => L10n.T("set.hist.90"),
                _ => L10n.T("set.hist.never"),
            };
        ClearHistoryBtn.Content = L10n.Btn("set.hist.clear");
        SaveBtn.Content = L10n.Btn("btn.save");
        CancelBtn.Content = L10n.Btn("btn.cancel");
    }

    private void PreviewLang(string code)
    {
        L10n.Set(code);
        ApplyStrings(); // o evento Changed re-traduz a janela principal; esta re-aplica-se aqui
    }

    private static void PreviewTheme(string name) => ThemeManager.Apply(name);

    private int SelectedRetention() =>
        (RetentionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { } tag
        && int.TryParse(tag, out var days) ? days : 90;

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_history is null) return;
        if (MessageBox.Show(this, L10n.T("set.hist.confirm"), Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _history.ClearAll();
        MessageBox.Show(this, L10n.T("set.hist.cleared"), Title,
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _closing) return;

        // Validação primeiro, mutação depois: cancelar ou falhar a meio deixa
        // _config exatamente como estava (URL inválida não fica meio gravada).
        string? validatedUrl = null;
        if (!string.IsNullOrWhiteSpace(CloudBox.Text))
        {
            var url = CloudBox.Text.Trim();
            if (!DiagConfig.TryValidateCloudUrl(url, out var uri))
            {
                MessageBox.Show(this, L10n.T("set.err.url"),
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // http:// para a Internet envia o token Pro e dados do cliente em
            // claro — só localhost/LAN de testes é razoável; exige confirmação.
            if (DiagConfig.IsInsecureUrl(uri) && MessageBox.Show(this,
                    L10n.T("set.warn.http"), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            validatedUrl = url;
        }

        var pasted = TokenBox.Password;
        // Só o código de emparelhamento (XXXX-YYYY da app da loja) entra
        // aqui — é trocado na cloud por um token scoped "diag". Um token
        // completo colado é recusado: este PC nunca deve guardar poder
        // total sobre a loja.
        if (!string.IsNullOrEmpty(pasted) && !IntakeRules.IsPairingCode(pasted))
        {
            MessageBox.Show(this, L10n.T("set.token.codeonly"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? token = null;
        string? shop = null;
        if (!string.IsNullOrEmpty(pasted))
        {
            _saving = true;
            SaveBtn.IsEnabled = false;
            try
            {
                var cloud = new CloudClient(_config);
                // O pairing tem de bater na URL que o utilizador acabou de
                // escrever, mesmo que ainda não esteja gravada em _config.
                if (validatedUrl is not null && DiagConfig.TryValidateCloudUrl(validatedUrl, out var vuri))
                    cloud.BaseUri = new Uri(vuri.ToString().TrimEnd('/') + "/");
                var (ok, t, s, err) = await cloud.RedeemPairingAsync(pasted, _cts.Token);
                // Fechado (X/Esc) enquanto o pedido corria: não tocar na
                // janela nem em _config — DialogResult aqui crashava a UI.
                if (_closing || !IsLoaded) return;
                if (!ok)
                {
                    MessageBox.Show(this, err, Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                token = t;
                shop = s;
            }
            catch (OperationCanceledException) { return; }
            finally
            {
                _saving = false;
                if (!_closing && IsLoaded) SaveBtn.IsEnabled = true;
            }
        }

        // Tudo validado e resolvido: gravar de uma vez, sem await pelo meio.
        if (validatedUrl is not null) _config.CloudUrl = validatedUrl;
        _config.Language = L10n.Current;
        _config.Theme = ThemeManager.Current;
        _config.HistoryRetentionDays = SelectedRetention();
        if (token is not null) _config.SetShopToken(token, "diag");

        // O aviso de emparelhamento abre ANTES de fechar: depois de
        // DialogResult = true a janela já não é dona de um MessageBox (o
        // utilizador via-o sem contexto, ou nem o via).
        if (shop is not null)
            MessageBox.Show(this, L10n.F("set.paired", shop), Title,
                MessageBoxButton.OK, MessageBoxImage.Information);

        _closing = true; // o preview já é o estado final — OnClosing não repõe nada
        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _cts.Cancel();
        ThemeManager.Changed -= ApplyStrings;
        // fechar sem guardar (cancelar, Esc, X) repõe o preview
        if (DialogResult != true)
        {
            L10n.Set(_origLang);
            ThemeManager.Apply(_origTheme);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Dispose();
        base.OnClosed(e);
    }
}
