using System.Globalization;
using System.Text.RegularExpressions;

namespace OficinaDiag.Devices;

/// <summary>
/// Parses puros das saídas de adb, separados do I/O para poderem ser testados
/// sob qualquer cultura: em Windows pt-PT um <c>double.TryParse("38.5")</c>
/// sem InvariantCulture devolve 385 (o ponto é separador de milhares).
/// </summary>
public static class AdbParsing
{
    /// <summary>Tipos de conta cujo reset dispara FRP.</summary>
    private const string AccountTypes =
        @"com\.google|com\.samsung|com\.huawei|com\.xiaomi|com\.oppo";

    /// <summary>
    /// Conta contas de fabricante/Google uma única vez. O dump repete
    /// <c>type=com.google</c> em cada secção de sync/cache, por isso contar
    /// ocorrências da substring inflacionava o número (14 para 1 conta real).
    /// </summary>
    public static int? ParseAccountCount(string? dump)
    {
        if (dump is null) return null;
        var matches = Regex.Matches(dump,
            @"Account\s*\{\s*name=([^,}]+),\s*type=(" + AccountTypes + @")\b");
        return matches
            .Select(m => $"{m.Groups[1].Value.Trim()}|{m.Groups[2].Value}".ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    /// <summary>
    /// Existe device/profile owner real? `Device Owner Type: -1` é o dump de
    /// um Android SEM gestão — procurar a substring "Device Owner" dava falso
    /// positivo (e, por estar nos HardLocks, capava o grau em D).
    /// </summary>
    public static bool HasDeviceOwner(string? dump)
    {
        if (dump is null) return false;
        var type = Regex.Match(dump, @"Device Owner Type:\s*(-?\d+)");
        if (type.Success && TryInt(type.Groups[1].Value, out var t) && t > -1) return true;
        // Dumps antigos não têm "Type": aceita só uma linha com componente real.
        return Regex.IsMatch(dump, @"Device Owner:\s*\S+/\S+");
    }

    /// <summary>`present: false` = equipamento sem bateria acessível (bancada).</summary>
    public static bool? ParsePresent(string? dump)
    {
        var m = Regex.Match(dump ?? "", @"present:\s*(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    /// <summary>Corrente em mA + direção. O `status` do kernel ganha ao sinal.</summary>
    public static (int? Milliamps, bool? Charging) ParseProbeCurrent(string? raw, string? status)
    {
        if (!TryLong(raw, out var cur)) return (null, null);
        // drivers variam: µA ou mA — acima de ~20 A assume µA
        var norm = Math.Abs(cur) > 20_000 ? cur / 1000 : cur;
        var mA = (int)Math.Abs(norm);

        // Samsung/Exynos, MediaTek e Huawei invertem o sinal: negativo é a
        // carregar. O status é a fonte de verdade; o sinal só desempata.
        var st = status?.Trim().ToLowerInvariant();
        bool? charging = st switch
        {
            "charging" or "full" => true,
            "discharging" or "not charging" => false,
            _ => null,
        };
        charging ??= cur < 0 ? false : cur > 0 ? true : null;
        return (mA, charging);
    }

    /// <summary>
    /// Saída de `for f in current_now voltage_now temp status capacity`.
    /// Devolve null nos campos com placeholder `_` ou ilegíveis.
    /// </summary>
    public static (int? Milliamps, bool? Charging, double? Volts, double? TempC, int? Percent)
        ParseBatteryProbe(string? output)
    {
        var l = (output ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();

        string? At(int i) => i < l.Length && l[i] != "_" ? l[i] : null;

        var (mA, charging) = ParseProbeCurrent(At(0), At(3));

        double? volts = null;
        if (TryDouble(At(1), out var uv))
            volts = uv > 100_000 ? uv / 1_000_000 : uv / 1000; // µV ou mV

        double? tempC = null;
        if (TryDouble(At(2), out var tp) && tp != 0)
            tempC = tp > 150 ? tp / 10 : tp; // décimos de °C

        int? pct = TryInt(At(4), out var cp) ? cp : null;
        return (mA, charging, volts, tempC, pct);
    }

    /// <summary>
    /// Zonas térmicas: descarta leituras implausíveis (kernels Qualcomm
    /// reportam zonas dummy a 125000 ou -273000 m°C) e devolve a mais quente
    /// entre as que parecem ser de SoC/CPU.
    /// </summary>
    public static double? ParseSocTemp(string? output)
    {
        double? soc = null;
        foreach (var line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':');
            if (parts.Length != 2) continue;
            var type = parts[0].Trim().ToLowerInvariant();
            if (!TryDouble(parts[1].Trim(), out var raw)) continue;
            var c = raw > 500 ? raw / 1000 : raw; // miligraus → °C
            if (c is < -10 or > 115) continue;     // zona dummy / sensor avariado
            var isSoc = type.Contains("cpu") || type.Contains("soc") || type.Contains("ap")
                || type.Contains("kryo") || type.Contains("big") || type.Contains("little")
                || type.Contains("silver") || type.Contains("gold");
            if (isSoc && (soc is null || c > soc)) soc = c;
        }
        return soc;
    }

    /// <summary>Capacidade: kernels reportam µAh ou mAh — normaliza para mAh.</summary>
    public static double NormalizeMah(double value) => value > 100_000 ? value / 1000.0 : value;

    public static bool TryInt(string? s, out int v) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    public static bool TryLong(string? s, out long v) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);

    public static bool TryDouble(string? s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
