namespace OficinaDiag;

/// <summary>
/// Sessão interativa embutida no painel da etapa (Bench/Perf). O host
/// mostra o painel por cima do console; o fecho é sempre negociado pelo
/// painel — uma sessão a meio pede confirmação antes de deitar fora as
/// medições.
/// </summary>
internal interface IInteractiveSession
{
    /// <summary>O painel aceitou fechar — o host lê o resultado e desmonta.</summary>
    event EventHandler? CloseRequested;

    /// <summary>True quando o técnico gravou (Done) em vez de sair sem gravar.</summary>
    bool SavedAndDone { get; }

    /// <summary>O host pede o fecho (botão voltar) — o painel confirma se houver dados a perder.</summary>
    void RequestClose();

    /// <summary>Sessão a decorrer — o host espera por ela terminar antes de sair da app.</summary>
    bool SessionRunning { get; }

    /// <summary>
    /// Saída da aplicação já confirmada pelo técnico: cancela sem perguntar
    /// outra vez — o restauro do estado do aparelho acontece na continuação
    /// cancelada da sessão.
    /// </summary>
    void AbortForAppExit();
}
