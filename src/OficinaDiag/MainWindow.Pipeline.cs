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
        // O realce da etapa vem do MESMO estado que escolhe o painel — nunca
        // do foco do teclado. Sem isto o arranque da janela punha o vídeo
        // inverso do foco no ① com outro painel aberto, e as capturas do
        // UiShots (que não movem o foco) mostravam o botão errado aceso.
        // Vídeo inverso no terminal; nos outros temas os mesmos brushes
        // partilhados dão o contraste equivalente.
        MarcarEtapa(StageDiagButton, stage == PipelineStage.Diagnosticar);
        MarcarEtapa(StageRepararButton, stage == PipelineStage.Reparar);
        MarcarEtapa(StageVerificarButton, stage == PipelineStage.Verificar);
        MarcarEtapa(StageEntregarButton, stage == PipelineStage.Entregar);
    }

    private static void MarcarEtapa(Button botao, bool ativa)
    {
        botao.FontWeight = ativa ? FontWeights.Bold : FontWeights.Normal;
        if (ativa)
        {
            botao.SetResourceReference(Control.BackgroundProperty, "FgBrush");
            botao.SetResourceReference(Control.ForegroundProperty, "BgBrush");
        }
        else
        {
            botao.ClearValue(Control.BackgroundProperty);
            botao.ClearValue(Control.ForegroundProperty);
        }
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
        var verificar = PipelineStages.AllowsVerificar(mode);
        StageDiagButton.Visibility = diag ? Visibility.Visible : Visibility.Collapsed;
        StageVerificarButton.Visibility = verificar ? Visibility.Visible : Visibility.Collapsed;
        StageEntregarButton.Visibility = PipelineStages.AllowsEntregar(mode) ? Visibility.Visible : Visibility.Collapsed;
        // Reparar é a única etapa que nunca se esconde: qualquer modo a pode
        // precisar — e nos modos de firmware é tudo o que existe.
        var firmwareOnly = !diag;
        StageModeHint.Text = firmwareOnly ? L10n.T("stage.modehint.firmware") : "";
        RepararFirmwareHint.Visibility = firmwareOnly ? Visibility.Visible : Visibility.Collapsed;
        RepararActionsCard.Visibility = firmwareOnly || _current is null ? Visibility.Collapsed : Visibility.Visible;
        // Se a etapa atual deixou de existir para este modo, cai na válida.
        if ((!diag && _stage == PipelineStage.Diagnosticar)
            || (!verificar && _stage == PipelineStage.Verificar))
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
        // Uma sessão de cada vez: abrir outra por cima substituía o painel
        // sem confirmação — a primeira continuava em segundo plano e as
        // medições já não tinham onde ser gravadas.
        if (_interactive is not null)
        {
            Log(L10n.T("log.interactive.busy"));
            return;
        }
        _interactive = panel;
        InteractiveContent.Content = panel;
        InteractiveHost.Visibility = Visibility.Visible;
    }

    /// <summary>Há uma sessão interativa aberta (mesmo antes de começar a medir).</summary>
    internal bool HasInteractive => _interactive is not null;

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
