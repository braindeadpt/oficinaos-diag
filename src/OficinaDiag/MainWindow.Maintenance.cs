using System.Windows;
using OficinaDiag.Devices;

namespace OficinaDiag;

public partial class MainWindow : Window
{
    /// <summary>
    /// Limpeza/otimização Android medida — o diálogo mede o "antes", corre as
    /// ações escolhidas e grava o antes/depois no relatório (fora de Results:
    /// não é um check de diagnóstico, não entra no grau nem nas vistas automáticas).
    /// </summary>
    private void Maintenance_Click(object sender, RoutedEventArgs e) =>
        _ = OpenMaintenance();

    /// <summary>
    /// Corpo da limpeza — chamado pelo botão LIMPEZA e pelo template de
    /// serviço "otimização" (ServiceDialog), que não tem checklist próprio:
    /// a limpeza medida já é o serviço. Devolve true quando a limpeza foi
    /// gravada — quem entrou pelo SERVIÇO usa-o para fechar o serviço.
    /// </summary>
    private bool OpenMaintenance()
    {
        if (_current is null || _current.Kind != DeviceKind.Android || _current.State != "ready")
        {
            Log(L10n.T("log.maint.nodev"));
            return false;
        }
        // Limpeza sem scan é válida — o relatório mínimo é o mesmo do checklist.
        if (_report is null)
        {
            _report = new DeviceReport
            {
                Platform = "android",
                Device = { Serial = _current.Id.TrimEnd('!') },
            };
            _reportDeviceId = _current.Id;
        }
        else if (!DeviceIds.Same(_reportDeviceId, _current.Id))
        {
            // O relatório aberto é de OUTRO aparelho — gravar a limpeza dele
            // aqui misturava medições de dois telemóveis no mesmo documento.
            Log(L10n.T("log.test.mismatch"));
            return false;
        }

        // _busy pausa o ticker de telemetria durante o diálogo inteiro: as
        // leituras do coletor não podem colidir com os comandos da limpeza.
        _busy = true;
        try
        {
            var serial = _current.Id.TrimEnd('!');
            var dlg = new MaintenanceDialog(_report, _adbPath, serial, Log) { Owner = this };
            if (dlg.ShowDialog() != true || !dlg.Saved) return false;

            Log(L10n.T("maint.saved"));
            // A limpeza não mexe em Results nem no grau — mas o relatório ficou
            // mais completo, por isso os botões de exportação acordam como no scan.
            // SyncReportButtons chama UpdateAiButtons: acende também a IA
            // MANUT. — sem isto a entrada via SERVIÇO→Otimização só tinha a
            // IA genérica e perdia o relatório de manutenção dedicado.
            SyncReportButtons();
            return true;
        }
        finally { _busy = false; }
    }
}
