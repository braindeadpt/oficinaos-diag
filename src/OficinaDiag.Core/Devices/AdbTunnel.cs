namespace OficinaDiag.Devices;

/// <summary>
/// Túnel `adb reverse` do teste on-device: guarda o que está montado e
/// remove-o uma única vez. O remove pode falhar (adb morto, telefone
/// desligado) e um túnel que fica montado no telefone aponta para uma
/// porta sem ninguém à escuta — por isso o falhanço avisa pelo log.
/// </summary>
public sealed class AdbTunnel
{
    private readonly Func<string, string, Task<bool>> _adb;
    private readonly Action<string> _warn;
    private (string Serial, int Port)? _active;

    /// <param name="adb">(serial, argumentos) → sucesso do `adb`.</param>
    /// <param name="warn">Destino do aviso quando o remove falha.</param>
    public AdbTunnel(Func<string, string, Task<bool>> adb, Action<string> warn)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>Há um túnel registado para remover.</summary>
    public bool IsMounted => _active is not null;

    public void Mount(string serial, int port) => _active = (serial, port);

    /// <summary>
    /// Remove o túnel. Devolve false quando o `adb reverse --remove` falhou
    /// (e nesse caso já registou o aviso). Chamar sem túnel é no-op.
    /// </summary>
    public async Task<bool> ClearAsync()
    {
        if (_active is not { } tunnel) return true;

        // Limpa antes do await: duas chamadas concorrentes não removem
        // (nem avisam) duas vezes o mesmo túnel.
        _active = null;
        var ok = await _adb(tunnel.Serial, $"reverse --remove tcp:{tunnel.Port}")
            .ConfigureAwait(false);
        if (!ok) _warn(L10n.F("log.test.reverse.fail", tunnel.Port));
        return ok;
    }
}
