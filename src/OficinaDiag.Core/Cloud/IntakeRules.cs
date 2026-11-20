using System.Net.Http;
using System.Text.RegularExpressions;

namespace OficinaDiag.Cloud;

/// <summary>
/// Regras puras (sem UI, sem rede) partilhadas entre os diálogos e o
/// CloudClient, e espelhadas da validação zod da OficinaOS Cloud
/// (<c>cloud/src/routes/intake.ts</c> e <c>reports.ts</c>). Validar aqui
/// evita que o cliente veja "Servidor respondeu 400 — {json}".
/// Coberto por <c>tests/OficinaDiag.Tests</c>.
/// </summary>
public static partial class IntakeRules
{
    /// <summary>Máximo de <c>notes</c> aceite pela Cloud (<c>z.string().max(2000)</c>).</summary>
    public const int NotesMaxLength = 2000;

    /// <summary>Máximo de <c>customerEmail</c> aceite pela Cloud (<c>.max(200)</c>).</summary>
    public const int EmailMaxLength = 200;

    /// <summary>Máximo de <c>log</c> aceite pela Cloud em POST /diag-logs (<c>z.string().min(1).max(200_000)</c>).</summary>
    public const int LogMaxChars = 200_000;

    /// <summary>
    /// Prepara o texto do diag.log para POST /diag-logs: a Cloud recusa logs
    /// vazios (min 1) ou acima de <see cref="LogMaxChars"/>. Devolve null
    /// quando não há nada para enviar; quando passa do limite fica a cauda
    /// com o marcador "início cortado" — o fim é onde estão os erros novos.
    /// </summary>
    public static string? PrepareLogForUpload(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;
        var marker = L10n.T("log.log.truncated") + "\n";
        if (log.Length <= LogMaxChars) return log;
        var room = LogMaxChars - marker.Length;
        var start = log.Length - room;
        // Não partir um par surrogate nem começar a meio de uma linha: salta
        // para depois do primeiro '\n', como o corte do próprio ficheiro.
        if (char.IsLowSurrogate(log[start])) start++;
        var nl = log.IndexOf('\n', start, Math.Min(4096, log.Length - start));
        if (nl >= 0) start = nl + 1;
        return marker + log[start..];
    }

    /// <summary>Cabeçalho que a Cloud usa para deduplicar POST /intake repetidos.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>True quando as notas cabem no limite da Cloud (null/vazio conta como válido).</summary>
    public static bool NotesWithinLimit(string? notes) =>
        notes is null || notes.Length <= NotesMaxLength;

    /// <summary>
    /// Validação de email deliberadamente próxima da do zod <c>z.email()</c>:
    /// parte local com caracteres "normais", sem pontos no início/fim nem
    /// seguidos, e um domínio com pelo menos um ponto e TLD alfabético (2+).
    /// Não tenta cobrir todo o RFC 5322 — basta apanhar gralhas antes do envio.
    /// </summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var e = email.Trim();
        if (e.Length > EmailMaxLength) return false;
        return EmailRegex().IsMatch(e);
    }

    [GeneratedRegex(
        @"^(?!\.)(?!.*\.\.)([A-Za-z0-9_'+\-\.]*)[A-Za-z0-9_+\-]@([A-Za-z0-9][A-Za-z0-9\-]*\.)+[A-Za-z]{2,}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    /// <summary>
    /// Código de emparelhamento da app da loja (XXXX-YYYY)? O alfabeto real
    /// exclui 0/O/1/I, mas aceitamos dígitos — quem os digita recebe o 400
    /// legível da cloud em vez de um "formato inválido" local. Um token
    /// completo colado NÃO passa aqui: o diag só aceita o código, que a
    /// cloud troca por um token com scope "diag" (privilégio mínimo).
    /// </summary>
    public static bool IsPairingCode(string input) =>
        PairingCodeRegex().IsMatch(input.Trim());

    [GeneratedRegex(@"^[A-Z0-9]{4}-[A-Z0-9]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex PairingCodeRegex();

    /// <summary>
    /// Defesa em profundidade no redeem: se a resposta da cloud declara um
    /// scope, tem de ser "diag" — devolve o scope ofensor, ou null quando é
    /// aceitável ("diag" ou ausente). Um token "full" nesta máquina seria
    /// privilégio a mais — bug da cloud que queremos ver, não herdar.
    /// Respostas sem "scope" (clouds antigas) passam: o pedido já pedia
    /// scope="diag" e a default-deny do servidor limita o estrago.
    /// </summary>
    public static string? RedeemScopeError(string? scope) =>
        scope is null or "diag" ? null : scope;

    /// <summary>Uma chave nova por envio — reutilizada em todas as tentativas desse envio.</summary>
    public static string NewIdempotencyKey() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// Põe o cabeçalho <c>Idempotency-Key</c> no pedido (substitui um valor
    /// anterior, se existir). Chamado em cada tentativa com a MESMA chave.
    /// </summary>
    public static HttpRequestMessage WithIdempotencyKey(this HttpRequestMessage req, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        req.Headers.Remove(IdempotencyHeader);
        req.Headers.TryAddWithoutValidation(IdempotencyHeader, key);
        return req;
    }
}
