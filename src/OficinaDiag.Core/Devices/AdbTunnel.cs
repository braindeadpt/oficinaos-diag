namespace OficinaDiag.Devices;

/// <summary>
/// Túnel `adb reverse` do teste on-device: guarda o que está montado e
/// remove-o uma única vez. Se o remove falhar, avisa — um túnel órfão no
/// telefone aponta para uma porta sem ninguém à escuta.
/// </summary>
public sealed class AdbTunnel
{
    private readonly Func<string, string, Task<bool>> _adb;
    private readonly Action<string> _warn;
    private readonly TimeSpan _timeout;
    private (string Serial, int Port)? _active;

    /// <param name="adb">(serial, argumentos) → sucesso do `adb`.</param>
    /// <param name="warn">Destino do aviso quando o remove falha.</param>
    /// <param name="timeout">
    /// Limite para o remove. Por omissão 2 s — abaixo dos 3 s que o
    /// OnClosing espera, para o aviso sair antes de a app fechar.
    /// </param>
    public AdbTunnel(Func<string, string, Task<bool>> adb, Action<string> warn, TimeSpan? timeout = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _warn = warn ?? throw new ArgumentNullException(nameof(warn));
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

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
        var ok = false;
        try
        {
            // Um adb pendurado (a esperar resposta do telefone) não pode
            // deixar a limpeza sem fim nem o aviso por registar. Timeout ou
            // excepção do adb são falha de limpeza: o túnel pode ter ficado.
            ok = await _adb(tunnel.Serial, $"reverse --remove tcp:{tunnel.Port}")
                .WaitAsync(_timeout)
                .ConfigureAwait(false);
        }
        catch (Exception) { }
        if (!ok) _warn(L10n.F("log.test.reverse.fail", tunnel.Port));
        return ok;
    }
}
