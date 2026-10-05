using System.Windows;

namespace OficinaDiag;

public partial class SendDialog : Window
{
    public SendDialog()
    {
        InitializeComponent();
        Title = L10n.T("send.title");
        HeaderText.Text = L10n.T("send.header");
        DescText.Text = L10n.T("send.desc");
        CodeLabel.Text = L10n.T("send.code");
        NameLabel.Text = L10n.T("send.name");
        PhoneLabel.Text = L10n.T("send.phone");
        EmailLabel.Text = L10n.T("send.email");
        PurposeLabel.Text = L10n.T("send.purpose");
        PurposeRepair.Content = L10n.T("send.purpose.repair");
        PurposeSale.Content = L10n.T("send.purpose.sale");
        SendBtn.Content = L10n.Btn("btn.send.ok");
        CancelBtn.Content = L10n.Btn("btn.cancel");
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
            MessageBox.Show(this, L10n.T("send.err.code"),
                L10n.T("send.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (CustomerName.Length == 0 || CustomerPhone.Length < 3)
        {
            MessageBox.Show(this, L10n.T("send.err.contact"),
                L10n.T("send.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
