namespace OficinaDiag.Log;

/// <summary>O que fazer com mais uma falha na mesma ação.</summary>
public readonly record struct FalhaRepetida(int Total, bool EscreverLog, bool Avisar, bool Desactivado);

/// <summary>
/// Quantas vezes a mesma ação (um botão) rebentou, e o que fazer com isso.
///
/// O handler global do `App` engolia a exceção e seguia: a app ficava viva e
/// errada, e o técnico via um botão que «não faz nada» sem saber porquê — foi
/// assim que o checklist ficou morto em relatórios novos durante dois lotes.
/// À terceira falha seguida na mesma ação a app avisa e desliga o botão, e o
/// `diag.log` deixa de crescer: a mesma exceção em cada clique enterrava a
/// causa em vez de a mostrar.
///
/// A regra vive fora da UI para poder ser testada — o handler global não é
/// sítio para guardar estado sem prova.
/// </summary>
public sealed class FailureGuard
{
    /// <summary>Falhas seguidas na mesma ação antes de avisar e desligar.</summary>
    public const int Limite = 3;

    private readonly Dictionary<string, int> _falhas = new(StringComparer.Ordinal);

    /// <summary>Falhas seguidas nesta ação (0 = sem falhas).</summary>
    public int FalhasDe(string acao) => _falhas.TryGetValue(acao, out var n) ? n : 0;

    /// <summary>
    /// Conta mais uma falha e diz o que fazer. `EscreverLog` só é verdadeiro
    /// até ao limite; `Avisar` é verdadeiro exactamente no limite (uma vez);
    /// `Desactivado` fica verdadeiro daí em diante.
    /// </summary>
    public FalhaRepetida Registar(string acao)
    {
        var total = FalhasDe(acao) + 1;
        _falhas[acao] = total;
        return new FalhaRepetida(
            Total: total,
            EscreverLog: total <= Limite,
            Avisar: total == Limite,
            Desactivado: total >= Limite);
    }

    /// <summary>
    /// A ação voltou a correr sem rebentar: a contagem recomeça. Sem isto, três
    /// falhas espaçadas num dia desligavam o botão para sempre.
    /// </summary>
    public void Sucesso(string acao) => _falhas.Remove(acao);

    /// <summary>
    /// A ação a culpar por uma exceção que chegou pelo handler global, ou null
    /// quando não é de um clique recente. O handler não recebe o remetente: quem
    /// sabe o botão é a janela, e só vale se a falha chegou perto do gesto —
    /// senão uma exceção de um diálogo desligava o último botão do ecrã principal.
    /// </summary>
    public static string? AcaoReconhecida(string? acao, long msDesdeOGesto, long janelaMs = 5000) =>
        acao is { Length: > 0 } && msDesdeOGesto >= 0 && msDesdeOGesto <= janelaMs ? acao : null;
}
