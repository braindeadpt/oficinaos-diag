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
