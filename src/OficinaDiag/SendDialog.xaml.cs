using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace OficinaDiag;

public partial class SendDialog : Window
{
    private readonly bool _mismatch;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // teto da área útil do monitor ONDE o diálogo abre — um secundário
        // mais baixo que o principal não pode deixar o Enviar fora do ecrã
        var (_, h) = WorkAreaDoMonitor();
        Scroller.MaxHeight = Math.Max(320, h - 120);
    }

    // mesmo padrão do AppManagerDialog/IosAppDialog — SystemParameters.WorkArea
    // é sempre o monitor principal e aqui não serve
    private struct RectNativo { public int Left, Top, Right, Bottom; }

    private struct MonitorInfo
    {
        public int cbSize;
        public RectNativo rcMonitor;
        public RectNativo rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private (double Width, double Height) WorkAreaDoMonitor()
    {
        var wa = SystemParameters.WorkArea;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return (wa.Width, wa.Height);
            var info = new MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            const uint MonitorDefaultToNearest = 2;
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
                return (wa.Width, wa.Height);
            var escala = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (!(escala > 0)) escala = 1.0;
            var dpi = 96 * escala;
            var r = info.rcWork;
            return ((r.Right - r.Left) * 96.0 / dpi, (r.Bottom - r.Top) * 96.0 / dpi);
        }
        catch { return (wa.Width, wa.Height); }
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
            MismatchConfirmText.Text = L10n.T("send.mismatch.confirm");
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
        PrivacyTitle.Text = L10n.T("send.privacy.title");
        PrivacyConsentText.Text = L10n.T("send.privacy.consent");
        PrivacyLink.Inlines.Clear();
        PrivacyLink.Inlines.Add(L10n.T("send.privacy.link"));
        UpdatePrivacyWarning();
        SendBtn.Content = L10n.Btn("btn.send.ok");
        CancelBtn.Content = L10n.Btn("btn.cancel");
        SendBtn.IsEnabled = false;
        PrivacyConsentBox.IsEnabled = false;
        ShopCodeBox.TextChanged += (_, _) => ShopCodeChanged();
    }

    public string ShopCode => ShopCodeBox.Text.Trim();
    public string CustomerName => NameBox.Text.Trim();
    public string CustomerPhone => PhoneBox.Text.Trim();
    public string? CustomerEmail =>
        string.IsNullOrWhiteSpace(EmailBox.Text) ? null : EmailBox.Text.Trim();
    // "sale" — the customer wants a buy-quote for a used phone, not a repair.
    public string Purpose => PurposeSale.IsChecked == true ? "sale" : "repair";

    private string? _consentedShopCode;

    private bool ConsentGiven => PrivacyConsentBox.IsChecked == true;
    private bool MismatchConfirmed => !_mismatch || MismatchConfirm.IsChecked == true;
    private string NormalizedShopCode => ShopCode.ToUpperInvariant();

    private void UpdatePrivacyWarning()
    {
        var shop = ShopCode.Length > 0 ? ShopCode : L10n.T("send.privacy.shop.pending");
        PrivacyText.Text = L10n.F("send.privacy.body", shop);
        PrivacyConsentBox.IsEnabled = ShopCode.Length > 0;
    }

    private void UpdateSendState() =>
        SendBtn.IsEnabled = ConsentGiven && MismatchConfirmed && ShopCode.Length > 0;

    private void ShopCodeChanged()
    {
        if (_consentedShopCode != NormalizedShopCode)
            PrivacyConsentBox.IsChecked = false;
        UpdatePrivacyWarning();
        UpdateSendState();
    }

    private void PrivacyConsent_Changed(object sender, RoutedEventArgs e)
    {
        if (PrivacyConsentBox.IsChecked == true && ShopCode.Length == 0)
            PrivacyConsentBox.IsChecked = false;
        _consentedShopCode = PrivacyConsentBox.IsChecked == true ? NormalizedShopCode : null;
        UpdateSendState();
    }

    private void MismatchConfirm_Click(object sender, RoutedEventArgs e) => UpdateSendState();

    private void PrivacyLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }

    private void ClearError() => ErrorText.Visibility = Visibility.Collapsed;

    private void ShowError(string message, TextBox field)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        field.Focus();
        field.SelectAll();
        // O foco desloca o formulário para o campo; sem isto, num ecrã baixo
        // o campo aparece mas a mensagem — lá em cima — sai da vista e o
        // técnico não percebe porque é que o envio não avançou. O foco fica
        // no campo (é lá que se corrige), mas a rolagem volta ao topo, onde
        // a mensagem está. ScrollToHome não depende do layout da mensagem
        // (que acabou de ficar visível e ainda não tem posição medida —
        // foi por isso que o BringIntoView não deslocava nada, provado
        // pela captura envio-erro no CI).
        Scroller.ScrollToHome();
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        ClearError();
        if (!MismatchConfirmed || !ConsentGiven) return;
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
