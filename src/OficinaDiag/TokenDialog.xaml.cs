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
        // Default ON: um token só-em-memória morre ao fechar a app e o código
        // (uso único) já não o repõe — a loja teria de gerar outro.
        RememberBox.IsChecked = true;
        LangLabel.Text = L10n.T("dlg.ai.lang");
        LangBox.Text = L10n.Current; // arranca no idioma da app, não sempre "pt"
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
        // Antes saía calado: o botão parecia avariado para quem não escreveu nada.
        if (Token.Length == 0)
        {
            MessageBox.Show(this, L10n.T("dlg.ai.token.empty"), Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TokenBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
