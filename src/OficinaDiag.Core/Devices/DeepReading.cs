namespace OficinaDiag.Devices;

/// <summary>Estado de uma leitura profunda — nunca rebenta, nunca inventa.</summary>
public enum ReadingState
{
    /// <summary>Leitura com valor real.</summary>
    Value,

    /// <summary>O aparelho não expõe esta fonte (nó sysfs ausente, contador inexistente).</summary>
    Unavailable,

    /// <summary>A fonte existe mas o shell não a consegue ler (permissão) — precisa de root.</summary>
    RequiresRoot,
}

/// <summary>
/// Uma leitura profunda do aparelho (pipeline fase 2): bateria, térmicas,
/// desgaste NAND, sensores, modem. Cada leitura declara o seu estado —
/// «não disponível neste aparelho» e «requer root» são resultados
/// explícitos, nunca a ausência silenciosa da chave.
/// </summary>
public sealed record DeepReading(string Key, ReadingState State, string? Value, string? Note)
{
    public static DeepReading Of(string key, string value, string? note = null) =>
        new(key, ReadingState.Value, value, note);

    public static DeepReading Missing(string key, string? note = null) =>
        new(key, ReadingState.Unavailable, null, note);

    public static DeepReading NeedsRoot(string key, string? note = null) =>
        new(key, ReadingState.RequiresRoot, null, note);
}

public static class DeepReadingParsing
{
    /// <summary>
    /// Interpreta a saída do probe sysfs marcado (`E:`/`P:`/`V:` por nó —
    /// ver AndroidCollector.ProbeSysfsDeepAsync): um nó que existe mas não
    /// deixa ler o conteúdo é «requer root»; ausente é «não disponível».
    /// </summary>
    public static DeepReading FromMarkedProbe(string key, string? probeOutput, string node)
    {
        if (probeOutput is null) return DeepReading.Missing(key);
        foreach (var rawLine in probeOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith(node + ":", StringComparison.Ordinal)) continue;
            var rest = line[(node.Length + 1)..];
            if (rest.StartsWith("missing", StringComparison.Ordinal)) return DeepReading.Missing(key);
            if (rest.StartsWith("denied", StringComparison.Ordinal)) return DeepReading.NeedsRoot(key);
            if (rest.StartsWith("value:", StringComparison.Ordinal))
                return DeepReading.Of(key, rest[6..]);
        }
        return DeepReading.Missing(key);
    }

    /// <summary>
    /// Ciclos de bateria a partir dos contadores sysfs habituais
    /// (`charge_counter`/`cycle_count` por marca) — cada marca expõe nomes
    /// diferentes e muitas não expõem nenhum.
    /// </summary>
    public static int? ParseCycleCount(string? output)
    {
        if (output is null) return null;
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (int.TryParse(t, out var n) && n is >= 0 and < 100000) return n;
        }
        return null;
    }

    /// <summary>
    /// Parse do dump do probe marcado (um nó por linha: «nó:value:X»,
    /// «nó:denied» ou «nó:missing») para os dois dicionários que o coletor
    /// consome. Shells antigas sem marcadores devolvem o resto como valor.
    /// Caminho único — os testes de marcas cobrem exatamente o que o
    /// coletor entrega à oficina.
    /// </summary>
    public static void ParseMarkedSysfs(string? dump,
        IDictionary<string, string> values, IDictionary<string, string> states)
    {
        foreach (var line in (dump ?? "").Split('\n'))
        {
            var sep = line.IndexOf(':');
            if (sep <= 0) continue;
            var node = line[..sep].Trim();
            if (values.ContainsKey(node) || states.ContainsKey(node)) continue;
            var rest = line[(sep + 1)..].Trim();
            if (rest.StartsWith("value:", StringComparison.Ordinal))
            {
                states[node] = "value";
                values[node] = rest[6..].Trim();
            }
            else if (rest is "denied" or "missing")
            {
                states[node] = rest;
            }
            else if (rest.Length > 0)
            {
                states[node] = "value";
                values[node] = rest;
            }
        }
    }

    /// <summary>
    /// Estado da leitura de capacidade da bateria quando o par real+design
    /// não sai completo. A regra explícita: «requer root» só quando root
    /// CHEGA para a percentagem — se a outra família está em falta no
    /// aparelho, o root não resolve e a resposta honesta é «não
    /// disponível» com nota do que existe.
    /// </summary>
    /// <param name="realDenied">algum nó da família real bloqueado — valor bruto,
    /// sem filtrar: a função ignora-o quando a família já produziu valor.</param>
    /// <param name="designDenied">idem para a família de fábrica.</param>
    /// <returns>(estado, precisaDeNotaParcial) ou null quando o par sai.</returns>
    public static (string state, bool partial)? CapacityFallbackState(
        bool realOk, bool designOk, bool realDenied, bool designDenied)
    {
        if (realOk && designOk) return null;
        // Um nó alternativo bloqueado só conta quando a família não produziu
        // valor — com a capacidade real já lida no battery/, um bms/ negado
        // é irrelevante e não pode puxar o diagnóstico para «requer root».
        realDenied = realDenied && !realOk;
        designDenied = designDenied && !designOk;
        var anyDenied = realDenied || designDenied;
        var otherMissing = (realDenied && !designOk && !designDenied)
            || (designDenied && !realOk && !realDenied);
        if (anyDenied && !otherMissing) return ("requer root", false);
        if (anyDenied) return ("não disponível neste aparelho", true);
        return ("não disponível neste aparelho", false);
    }

    /// <summary>Classe de desgaste eMMC (hex «0xNN» de life_time) — pior classe presente.</summary>
    public static int? ParseEmmcLifeClass(string? output)
    {
        if (output is null) return null;
        int worst = -1;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(output, @"0x([0-9A-Fa-f]{2})"))
        {
            var v = Convert.ToInt32(m.Groups[1].Value, 16);
            if (v is >= 1 and <= 11 && v > worst) worst = v;
        }
        return worst >= 1 ? worst : null;
    }
}
