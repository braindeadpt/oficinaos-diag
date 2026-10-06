using System.Windows;

namespace OficinaDiag;

/// <summary>
/// Pede o token Pro + idioma do relatório IA — mascarado (o InputBox antigo
/// mostrava o token em claro). "Memorizar" guarda-o cifrado no DiagConfig.
/// </summary>
public partial class TokenDialog : Window
{
    public TokenDialog()
    {
        InitializeComponent();
        Title = L10n.T("dlg.ai.token.title");
        TokenLabel.Text = L10n.T("dlg.ai.token");
        RememberBox.Content = L10n.T("dlg.token.remember");
        LangLabel.Text = L10n.T("dlg.ai.lang");
        OkBtn.Content = "OK";
        CancelBtn.Content = L10n.Btn("btn.cancel");
    }

    public string Token => TokenBox.Password.Trim();
    public bool Remember => RememberBox.IsChecked == true;

    public string Lang =>
        (LangBox.Text ?? "pt").Trim().ToLowerInvariant() is "pt" or "en" or "fr" or "es"
            ? (LangBox.Text ?? "pt").Trim().ToLowerInvariant() : "pt";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Token.Length == 0) return;
        DialogResult = true;
    }
}
