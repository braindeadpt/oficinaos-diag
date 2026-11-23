using System.Globalization;
using System.Text.RegularExpressions;

namespace OficinaDiag.Maintenance;

/// <summary>
/// Parsers das saídas adb do módulo de manutenção. Cada um devolve null quando
/// a saída não tem o valor — o chamador nunca recebe um zero inventado.
/// </summary>
public static class MaintenanceParsing
{
    /// <summary>
    /// `df /data`: a linha de dados tem … used available capacity% mount.
    /// Localiza o token percentual e lê os dois KB anteriores — a mesma
    /// lógica do coletor de scan, mas a devolver números.
    /// </summary>
    public static (long UsedKb, long AvailKb)? ParseDf(string? df)
    {
        if (string.IsNullOrWhiteSpace(df)) return null;
        foreach (var line in df.Split('\n'))
        {
            var cols = Regex.Split(line.Trim(), @"\s+");
            if (cols.Length < 6) continue;
            // O token de capacidade é o único que termina em '%'.
            var capIndex = -1;
            for (var i = 0; i < cols.Length; i++)
                if (cols[i].EndsWith('%') && cols[i].TrimEnd('%').All(char.IsDigit)) { capIndex = i; break; }
            if (capIndex < 2) continue;
            if (!long.TryParse(cols[capIndex - 1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var availKb)) continue;
            if (!long.TryParse(cols[capIndex - 2], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var usedKb)) continue;
            // Só a linha de /data interessa — um df de outro mount não é storage do sistema.
            if (!line.Contains("/data", StringComparison.Ordinal)) continue;
            return (usedKb, availKb);
        }
        return null;
    }

    /// <summary>MemAvailable do /proc/meminfo, em kB.</summary>
    public static long? ParseMemAvailableKb(string? meminfo)
    {
        if (meminfo is null) return null;
        var m = Regex.Match(meminfo, @"MemAvailable:\s+(\d+)");
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var kb) ? kb : null;
    }

    /// <summary>/proc/uptime: "12345.67 89012.34" — o primeiro campo é o boot.</summary>
    public static double? ParseUptimeSeconds(string? uptime)
    {
        if (string.IsNullOrWhiteSpace(uptime)) return null;
        var first = uptime.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? s : null;
    }

    /// <summary>dumpsys battery → nível (%) e temperatura (°C). Ausente = null.</summary>
    public static (int? Level, double? TempC) ParseBattery(string? dumpsys)
    {
        if (dumpsys is null) return (null, null);
        int? level = null;
        double? temp = null;
        var lm = Regex.Match(dumpsys, @"level:\s*(\d+)");
        if (lm.Success) level = int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
        var tm = Regex.Match(dumpsys, @"temperature:\s*(\d+)");
        if (tm.Success)
        {
            var t = int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture) / 10.0;
            temp = t > 0 ? t : null; // 0 é ausência de leitura, não 0 °C
        }
        return (level, temp);
    }

    /// <summary>
    /// `settings get global <chave>` devolve "1.0", "0.5" ou "null" quando a
    /// chave nunca foi escrita (o default do sistema). "null" → null: a
    /// escala efetiva é a predefinição, não um valor medido.
    /// </summary>
    public static double? ParseScale(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>As 4 linhas de `settings get` em sequência → escalas + low_power.</summary>
    public static (AnimationScales? Animations, int? LowPower) ParseSettingsDump(string? dump)
    {
        // Sem leitura nenhuma não há nada a repor: null (desconhecido) tem de
        // ficar distinto de "chave nunca escrita" (que o settings devolve como
        // "null" por linha) — senão uma reposição apagava definições do cliente.
        if (string.IsNullOrWhiteSpace(dump)) return (null, null);
        var lines = dump
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var window = lines.Length > 0 ? ParseScale(lines[0]) : null;
        var transition = lines.Length > 1 ? ParseScale(lines[1]) : null;
        var animator = lines.Length > 2 ? ParseScale(lines[2]) : null;
        int? lowPower = lines.Length > 3 && int.TryParse(lines[3], NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var lp) ? lp : null;
        return (new AnimationScales(window, transition, animator), lowPower);
    }

    /// <summary>`ps -A | wc -l` → número de processos (header incluído, consistente antes/depois).</summary>
    public static int? ParseProcessCount(string? wcOutput)
    {
        if (string.IsNullOrWhiteSpace(wcOutput)) return null;
        var first = wcOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n : null;
    }

    /// <summary>
    /// Nomes de pacote do `pm list packages -3` (só apps de utilizador — as que
    /// o cliente pode desinstalar). Linhas "package:com.foo".
    /// </summary>
    public static List<string> ParseUserPackages(string? pmOutput)
    {
        var list = new List<string>();
        if (pmOutput is null) return list;
        foreach (var line in pmOutput.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("package:", StringComparison.Ordinal)) continue;
            var pkg = t[8..].Trim();
            if (IsValidPackageName(pkg)) list.Add(pkg);
        }
        return list;
    }

    /// <summary>
    /// Nome de pacote Android válido — letras, dígitos, '_' e pontos, a começar
    /// por letra. O comando vai para o sh do telefone: sem esta validação um
    /// nome hostil era interpretado como shell remoto.
    /// </summary>
    public static bool IsValidPackageName(string? pkg) =>
        !string.IsNullOrWhiteSpace(pkg)
        && pkg.Length <= 200
        && Regex.IsMatch(pkg, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$");
}
