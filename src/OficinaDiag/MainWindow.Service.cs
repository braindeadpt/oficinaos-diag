using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    /// <summary>
    /// Template de serviço (Fase E.6) — o técnico escolhe o tipo de
    /// atendimento no início do trabalho: troca de bateria, troca de ecrã,
    /// compra de usado, otimização ou migração WhatsApp. O checklist do
    /// serviço entra no relatório como checks tpl.* e orienta o relatório IA.
    /// </summary>
    private void Service_Click(object sender, RoutedEventArgs e)
    {
        // Checklist de serviço sem scan é válido — telemóvel que não fala por USB.
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = _current?.Kind == DeviceKind.Ios ? "ios" : "android",
                Device = { Serial = _current?.Id?.TrimEnd('!') },
            };
            _reportDeviceId = _current?.Id;
        }
        var dlg = new ServiceDialog(_report) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        // Otimização/manutenção não grava tpl.* — o serviço já existe como
        // fluxo próprio (janela LIMPEZA, medições adb antes/depois).
        if (dlg.Applied?.OpensMaintenanceDialog == true)
        {
            OpenMaintenance();
            return;
        }
        ApplyGrade();
        ExportButton.IsEnabled = SendButton.IsEnabled = InsuranceButton.IsEnabled =
            CompareButton.IsEnabled = LabelButton.IsEnabled = ClientButton.IsEnabled = true;
        UpdateAiButtons();
        Log(L10n.T("log.svc.saved"));
    }
}
