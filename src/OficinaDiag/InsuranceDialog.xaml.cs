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
            MessageBox.Show(this, "Falta o nome do segurado.",
                "Relatório para seguradora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (DamageBox.Text.Trim().Length < 5)
        {
            MessageBox.Show(this, "Descreve o dano — é o que a seguradora pede primeiro.",
                "Relatório para seguradora", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ShopNameBox.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "O nome da loja é o carimbo do relatório — obrigatório.",
                "Relatório para seguradora", MessageBoxButton.OK, MessageBoxImage.Warning);
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
