using System.Windows;
using OficinaDiag.Devices;
using OficinaDiag.Templates;

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
        else if (_current is not null && _reportDeviceId is not null
                 && !DeviceIds.Same(_reportDeviceId, _current.Id))
        {
            // O relatório aberto é de OUTRO aparelho — o seletor troca o
            // telefone ativo sem substituir o relatório. Gravar aqui o
            // checklist/finalidade dele misturava atendimentos de dois
            // telemóveis no histórico (mesma regra da limpeza/teste).
            // DeviceIds.Same ignora o '!' de estado pendente: o mesmo
            // Android muda de Id quando o técnico autoriza a depuração.
            // Relatório sem aparelho associado (_reportDeviceId null) passa:
            // quem decide se os dados manuais são deste scan é a confirmação
            // do Scan_Click — não se bloqueia trabalho já começado.
            Log(L10n.T("log.device.mismatch"));
            return;
        }
        var dlg = new ServiceDialog(_report) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        // Otimização/manutenção não grava tpl.* — o serviço já existe como
        // fluxo próprio (janela LIMPEZA, medições adb antes/depois).
        if (dlg.Applied?.OpensMaintenanceDialog == true)
        {
            // A limpeza só passa a ser o serviço do atendimento quando é
            // gravada — cancelar a janela LIMPEZA não pode apagar o
            // checklist de um serviço anterior. Uma vez gravada, substitui
            // o tpl.* anterior: "compra de usado" não pode ficar no relatório
            // com purpose=sale depois de uma otimização (um atendimento = um
            // serviço). O LIMPEZA direto continua complementar — só a escolha
            // explícita no seletor de serviço substitui.
            if (OpenMaintenance() && _report is not null)
            {
                if (ServiceTemplates.ApplyMaintenance(_report))
                    Log(L10n.T("log.svc.superseded"));
                // ApplyGrade sincroniza o histórico e a lista de resultados —
                // os tpl.* removidos e o Maintenance novo têm de lá chegar.
                ApplyGrade();
            }
            return;
        }
        ApplyGrade();
        ExportButton.IsEnabled = SendButton.IsEnabled = InsuranceButton.IsEnabled =
            CompareButton.IsEnabled = LabelButton.IsEnabled = ClientButton.IsEnabled = true;
        UpdateAiButtons();
        Log(L10n.T("log.svc.saved"));
    }
}
