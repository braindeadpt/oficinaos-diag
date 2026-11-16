using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OficinaDiag.Reports;

/// <summary>
/// Selo de integridade do relatório: SHA-256 do documento com a própria linha
/// do selo removida. Não impede a alteração — torna-a <em>detetável</em>, que é
/// o que vale num documento que vai à seguradora. A linha é visível (não é um
/// comentário): quem recebe o relatório vê logo que pode conferi-lo.
/// </summary>
public static class ReportIntegrity
{
    /// <summary>Início da linha do selo — a remoção e a verificação usam-no.</summary>
    public const string Prefixo = "<p class=\"hash\">Integridade SHA-256: ";

    private const string Sufixo =
        " — se este código não conferir com o documento recebido, o ficheiro foi alterado depois de emitido.</p>";

    /// <summary>
    /// O selo, como um todo. Sem âncoras de linha: ele entra imediatamente
    /// antes de <c>&lt;/body&gt;</c>, que nos relatórios reais está no fim de
    /// uma linha com conteúdo. Ancorar em <c>^</c> fazia o selo não ser
    /// reconhecido e cada <c>Marcar</c> acrescentava outro.
    /// </summary>
    private static readonly Regex LinhaDoSelo = new(
        Regex.Escape(Prefixo) + "[^\\n]*?</p>",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Acrescenta (ou substitui) o selo. Idempotente: marcar duas vezes dá o
    /// mesmo documento — o selo antigo sai antes de o novo ser calculado.
    /// </summary>
    public static string Marcar(string html)
    {
        var limpo = Desmarcar(html);
        var linha = Prefixo + $"<code>{Calcular(limpo)}</code>" + Sufixo;
        var corte = limpo.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        // Sem fim de linha à volta: a remoção devolve exactamente o texto
        // original, e é isso que faz o hash ser estável.
        return corte < 0 ? limpo + linha : limpo.Insert(corte, linha);
    }

    /// <summary>
    /// Confere o selo. <c>Declarado</c> é o que o documento diz, <c>Calculado</c>
    /// o que o conteúdo dá agora; sem selo, <c>Declarado</c> é <c>null</c> e o
    /// resultado é falso (ausência não é prova de integridade).
    /// </summary>
    public static (bool Ok, string? Declarado, string Calculado) Verificar(string html)
    {
        var match = LinhaDoSelo.Match(html);
        if (!match.Success) return (false, null, Calcular(html));
        var declarado = ExtrairHash(match.Value);
        var calculado = Calcular(html);
        return (declarado is not null && declarado == calculado, declarado, calculado);
    }

    /// <summary>SHA-256 do documento sem o selo, com fins de linha normalizados.</summary>
    public static string Calcular(string html)
    {
        var normalizado = Desmarcar(html).Replace("\r\n", "\n");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizado));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Documento sem o selo. Só o selo sai: o resto fica byte a byte igual,
    /// para o hash não depender de reformatação.
    /// </summary>
    public static string Desmarcar(string html) => LinhaDoSelo.Replace(html, "");

    private static string? ExtrairHash(string linha)
    {
        const string ini = "<code>";
        var i = linha.IndexOf(ini, StringComparison.Ordinal);
        if (i < 0) return null;
        i += ini.Length;
        var f = linha.IndexOf("</code>", i, StringComparison.Ordinal);
        return f < 0 ? null : linha[i..f];
    }
}
