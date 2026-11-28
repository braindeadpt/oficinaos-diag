using System.Windows;
using System.Windows.Controls;

namespace OficinaDiag;

public partial class SendDialog : Window
{
    private readonly bool _mismatch;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // teto real da área útil deste monitor — o scroll só aparece quando faz falta
        Scroller.MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80);
    }

    /// <param name="reportIdentity">modelo + série do relatório que vai ser enviado.</param>
    /// <param name="mismatch">true quando o relatório não pertence ao aparelho
    /// ativo — o envio fica bloqueado até à confirmação explícita.</param>
    public SendDialog(string? initialPurpose = null, string? reportIdentity = null, bool mismatch = false)
    {
        InitializeComponent();
        _mismatch = mismatch;
        if (reportIdentity is not null)
            DeviceLine.Text = L10n.F("send.device.line", reportIdentity);
        if (mismatch)
        {
            MismatchWarn.Text = L10n.T("send.mismatch.warn");
            MismatchWarn.Visibility = Visibility.Visible;
            MismatchConfirm.Content = L10n.T("send.mismatch.confirm");
            MismatchConfirm.Visibility = Visibility.Visible;
            SendBtn.IsEnabled = false;
        }
        // O checklist pode já ter marcado "venda" — o radio reflete isso em
        // vez de voltar sempre a "reparação" e dessincronizar o payload.
        PurposeSale.IsChecked = initialPurpose == "sale";
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

    private void MismatchConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_mismatch) SendBtn.IsEnabled = MismatchConfirm.IsChecked == true;
    }

    private void ClearError() => ErrorText.Visibility = Visibility.Collapsed;

    private void ShowError(string message, TextBox field)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        field.Focus();
        field.SelectAll();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
        // a cloud gera códigos de loja com exatamente 6 caracteres
        if (ShopCode.Length < 6)
        {
            ShowError(L10n.T("send.err.code"), ShopCodeBox);
            return;
        }
        if (CustomerName.Length == 0 || CustomerPhone.Length < 3)
        {
            ShowError(L10n.T("send.err.contact"), CustomerName.Length == 0 ? NameBox : PhoneBox);
            return;
        }
        // email é opcional, mas se vier tem de passar o z.email() da Cloud
        if (CustomerEmail is not null && !Cloud.IntakeRules.IsValidEmail(CustomerEmail))
        {
            ShowError(L10n.T("send.err.email"), EmailBox);
            return;
        }
        DialogResult = true;
    }
}
