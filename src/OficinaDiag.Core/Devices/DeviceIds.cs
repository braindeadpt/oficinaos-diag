namespace OficinaDiag.Devices;

/// <summary>
/// Comparação de identificadores de aparelho. O DeviceDetector sufixa o Id
/// com '!' nos estados pendentes (unauthorized/recovery/sideload/fastboot)
/// e tira-o quando o aparelho fica ready — o MESMO telemóvel muda de Id à
/// autorização. Toda a comparação de "é o mesmo aparelho?" (guardas de
/// relatório, preservação no re-scan, ACK do teste no ecrã) tem de ignorar
/// o sufixo ou bloqueia trabalho legítimo depois de o utilizador autorizar.
/// </summary>
public static class DeviceIds
{
    /// <summary>Id canónico — sem o sufixo '!' de estado pendente.</summary>
    public static string? Canonical(string? id) =>
        string.IsNullOrEmpty(id) ? id : id.TrimEnd('!');

    /// <summary>true = os dois ids descrevem o mesmo aparelho (sufixo ignorado).</summary>
    public static bool Same(string? a, string? b) =>
        Canonical(a) == Canonical(b);
}
