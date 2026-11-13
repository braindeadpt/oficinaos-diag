using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Testes que dependem do sistema operativo — sockets reais, eleição de
/// interface, processos filhos — correm isolados do resto da suite. Em
/// paralelo, os dois tipos de teste que falharam de forma intermitente
/// (aceitar/fechar socket e matar árvore de processos) competiam por CPU e
/// pelos mesmos waits de 5 s: verde sozinhos, vermelhos de vez em quando na
/// suite completa. Isolar é mais honesto do que inflacionar timeouts.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialCollection
{
    public const string Name = "sistema";
}
