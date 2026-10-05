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
        _config.Language = L10n.Current;
        _config.Theme = ThemeManager.Current;
        if (!string.IsNullOrWhiteSpace(CloudBox.Text))
            _config.CloudUrl = CloudBox.Text.Trim();
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
