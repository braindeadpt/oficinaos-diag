using System.Windows;
using System.Windows.Controls;
using OficinaDiag.Cloud;

namespace OficinaDiag;

/// <summary>
/// Opções — idioma, tema e URL da cloud. Tema e idioma aplicam-se em preview
/// ao escolher; cancelar repõe o estado anterior.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly DiagConfig _config;
    private readonly string _origLang;
    private readonly string _origTheme;

    public SettingsDialog(DiagConfig config)
    {
        _config = config;
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
        SaveBtn.Content = L10n.Btn("btn.save");
        CancelBtn.Content = L10n.Btn("btn.cancel");
    }

    private void PreviewLang(string code)
    {
        L10n.Set(code);
        ApplyStrings(); // o evento Changed re-traduz a janela principal; esta re-aplica-se aqui
    }

    private static void PreviewTheme(string name) => ThemeManager.Apply(name);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
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
            _config.CloudUrl = url;
        }
        _config.Language = L10n.Current;
        _config.Theme = ThemeManager.Current;
        if (!string.IsNullOrEmpty(TokenBox.Password))
            _config.SetShopToken(TokenBox.Password);
        DialogResult = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // fechar sem guardar (cancelar, Esc, X) repõe o preview
        ThemeManager.Changed -= ApplyStrings;
        if (DialogResult != true)
        {
            L10n.Set(_origLang);
            ThemeManager.Apply(_origTheme);
        }
        base.OnClosing(e);
    }
}
