using System.Windows;

namespace OficinaDiag;

public partial class SendDialog : Window
{
    public SendDialog()
    {
        InitializeComponent();
    }

    public string ShopCode => ShopCodeBox.Text.Trim();
    public string CustomerName => NameBox.Text.Trim();
    public string CustomerPhone => PhoneBox.Text.Trim();
    public string? CustomerEmail =>
        string.IsNullOrWhiteSpace(EmailBox.Text) ? null : EmailBox.Text.Trim();
    // "sale" — the customer wants a buy-quote for a used phone, not a repair.
    public string Purpose => PurposeSale.IsChecked == true ? "sale" : "repair";

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (ShopCode.Length < 4)
        {
            MessageBox.Show(this, "Falta o código da loja (6 letras).",
                "Enviar à loja", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (CustomerName.Length == 0 || CustomerPhone.Length < 3)
        {
            MessageBox.Show(this, "Precisamos do teu nome e telefone — a loja usa-os para te identificar.",
                "Enviar à loja", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
