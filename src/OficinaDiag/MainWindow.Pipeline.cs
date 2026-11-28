using System.Windows;
using System.Windows.Controls;
using OficinaDiag.Devices;

namespace OficinaDiag;

/// <summary>
/// O pipeline da bancada (plano §2): ① Diagnosticar ② Reparar ③ Verificar
/// ④ Entregar. A etapa ativa troca o painel de conteúdo e o grupo de ações;
/// o modo do aparelho decide que etapas aparecem de todo — um aparelho em
/// BROM/EDL/Odin/DFU só tem reparação possível, e esconder o não-aplicável
/// elimina o ruído sem tirar poder nenhum.
/// </summary>
public partial class MainWindow : Window
{
    private enum PipelineStage { Diagnosticar, Reparar, Verificar, Entregar }

    private PipelineStage _stage = PipelineStage.Diagnosticar;

    private void StageDiag_Click(object sender, RoutedEventArgs e) => SelectStage(PipelineStage.Diagnosticar);
    private void StageReparar_Click(object sender, RoutedEventArgs e) => SelectStage(PipelineStage.Reparar);
    private void StageVerificar_Click(object sender, RoutedEventArgs e) => SelectStage(PipelineStage.Verificar);
    private void StageEntregar_Click(object sender, RoutedEventArgs e) => SelectStage(PipelineStage.Entregar);

    /// <summary>Troca a etapa visível — os toggles são exclusivos à mão.</summary>
    private void SelectStage(PipelineStage stage)
    {
        _stage = stage;
        StageDiagContent.Visibility = stage == PipelineStage.Diagnosticar ? Visibility.Visible : Visibility.Collapsed;
        StageRepararContent.Visibility = stage == PipelineStage.Reparar ? Visibility.Visible : Visibility.Collapsed;
        StageVerificarContent.Visibility = stage == PipelineStage.Verificar ? Visibility.Visible : Visibility.Collapsed;
        StageEntregarContent.Visibility = stage == PipelineStage.Entregar ? Visibility.Visible : Visibility.Collapsed;
        // As ações da etapa vivem dentro do respetivo painel de conteúdo;
        // na barra inferior só fica o grupo da etapa ① (o núcleo) e o sistema.
        ActionsDiag.Visibility = stage == PipelineStage.Diagnosticar ? Visibility.Visible : Visibility.Collapsed;
        // A etapa ativa fica em negrito — o retro não tem pills nem tabs.
        StageDiagButton.FontWeight = stage == PipelineStage.Diagnosticar ? FontWeights.Bold : FontWeights.Normal;
        StageRepararButton.FontWeight = stage == PipelineStage.Reparar ? FontWeights.Bold : FontWeights.Normal;
        StageVerificarButton.FontWeight = stage == PipelineStage.Verificar ? FontWeights.Bold : FontWeights.Normal;
        StageEntregarButton.FontWeight = stage == PipelineStage.Entregar ? FontWeights.Bold : FontWeights.Normal;
    }

    /// <summary>
    /// Aplica a visibilidade das etapas ao modo do aparelho ativo. Sem
    /// aparelho (ou modo incógnito) tudo fica visível — a bancada normal é
    /// o caminho feliz, a restrição é a exceção.
    /// </summary>
    private void SyncStageVisibility()
    {
        var mode = _current?.Mode ?? DeviceMode.Unknown;
        var diag = PipelineStages.AllowsDiagnosticar(mode);
        var verificarEntregar = PipelineStages.AllowsVerificarEntregar(mode);
        StageDiagButton.Visibility = diag ? Visibility.Visible : Visibility.Collapsed;
        StageVerificarButton.Visibility = verificarEntregar ? Visibility.Visible : Visibility.Collapsed;
        StageEntregarButton.Visibility = verificarEntregar ? Visibility.Visible : Visibility.Collapsed;
        // Reparar é a única etapa que nunca se esconde: qualquer modo a pode
        // precisar — e nos modos de firmware é tudo o que existe.
        var firmwareOnly = !diag;
        StageModeHint.Text = firmwareOnly ? L10n.T("stage.modehint.firmware") : "";
        RepararFirmwareHint.Visibility = firmwareOnly ? Visibility.Visible : Visibility.Collapsed;
        RepararActionsCard.Visibility = firmwareOnly || _current is null ? Visibility.Collapsed : Visibility.Visible;
        // Se a etapa atual deixou de existir para este modo, cai na válida.
        if ((!diag && _stage == PipelineStage.Diagnosticar)
            || (!verificarEntregar && _stage is PipelineStage.Verificar or PipelineStage.Entregar))
            SelectStage(PipelineStage.Reparar);
    }

    // ── painel interativo embutido (Bench/Perf) ─────────────────────

    /// <summary>
    /// Sessão interativa a decorrer dentro da etapa — cobre o console até
    /// o técnico voltar. Uma de cada vez: fechar uma sessão a meio deita
    /// fora as medições, por isso o painel manda na janela enquanto vive.
    /// </summary>
    private UIElement? _interactive;

    internal void OpenInteractive(UIElement panel)
    {
        _interactive = panel;
        InteractiveContent.Content = panel;
        InteractiveHost.Visibility = Visibility.Visible;
    }

    internal void CloseInteractive()
    {
        InteractiveContent.Content = null;
        InteractiveHost.Visibility = Visibility.Collapsed;
        _interactive = null;
    }

    private void InteractiveBack_Click(object sender, RoutedEventArgs e)
    {
        if (_interactive is IInteractiveSession session) session.RequestClose();
    }
}
