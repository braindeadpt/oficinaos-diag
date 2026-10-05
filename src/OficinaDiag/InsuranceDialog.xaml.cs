using System.Windows;
using OficinaDiag.Cloud;
using OficinaDiag.Reports;

namespace OficinaDiag;

public partial class InsuranceDialog : Window
{
    private readonly DiagConfig _config;

    public InsuranceDialog(DiagConfig config)
    {
        _config = config;
        InitializeComponent();
        Title = L10n.T("ins.title");
        HeaderText.Text = L10n.T("ins.header");
        DescText.Text = L10n.T("ins.desc");
        InsuredLabel.Text = L10n.T("ins.insured");
        InsurerLabel.Text = L10n.T("ins.insurer");
        PolicyLabel.Text = L10n.T("ins.policy");
        DamageLabel.Text = L10n.T("ins.damage");
        RepairLabel.Text = L10n.T("ins.repair");
        RepairBox.ToolTip = L10n.T("ins.repair.tip");
        CostLabel.Text = L10n.T("ins.cost");
        CostBox.ToolTip = L10n.T("ins.cost.tip");
        TechLabel.Text = L10n.T("ins.tech");
        ShopSecLabel.Text = L10n.Section("ins.shopsec");
        ShopNameLabel.Text = L10n.T("ins.shopname");
        ShopNifLabel.Text = L10n.T("ins.shopnif");
        ShopPhoneLabel.Text = L10n.T("ins.shopphone");
        ShopAddrLabel.Text = L10n.T("ins.shopaddr");
        GenerateBtn.Content = L10n.Btn("btn.generate");
        CancelBtn.Content = L10n.Btn("btn.cancel");
        ShopNameBox.Text = config.ShopName ?? "";
        ShopNifBox.Text = config.ShopNif ?? "";
        ShopPhoneBox.Text = config.ShopPhone ?? "";
        ShopAddressBox.Text = config.ShopAddress ?? "";
    }

    public InsuranceForm? Form { get; private set; }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (InsuredNameBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, L10n.T("ins.err.insured"),
                L10n.T("ins.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (DamageBox.Text.Trim().Length < 5)
        {
            MessageBox.Show(this, L10n.T("ins.err.damage"),
                L10n.T("ins.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ShopNameBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, L10n.T("ins.err.shop"),
                L10n.T("ins.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Form = new InsuranceForm
        {
            InsuredName = InsuredNameBox.Text.Trim(),
            Insurer = Empty(InsurerBox),
            PolicyNo = Empty(PolicyNoBox),
            DamageDescription = DamageBox.Text.Trim(),
            RepairEstimate = Empty(RepairBox),
            EstimatedCost = Empty(CostBox),
            Technician = Empty(TechnicianBox),
            ShopName = ShopNameBox.Text.Trim(),
            ShopNif = Empty(ShopNifBox),
            ShopPhone = Empty(ShopPhoneBox),
            ShopAddress = Empty(ShopAddressBox),
        };

        // O carimbo da loja repete-se em todos os relatórios — guarda para
        // pré-preencher da próxima vez.
        _config.ShopName = Form.ShopName;
        _config.ShopNif = Form.ShopNif;
        _config.ShopPhone = Form.ShopPhone;
        _config.ShopAddress = Form.ShopAddress;
        _config.Save();

        DialogResult = true;
    }

    private static string? Empty(System.Windows.Controls.TextBox b) =>
        string.IsNullOrWhiteSpace(b.Text) ? null : b.Text.Trim();
}
