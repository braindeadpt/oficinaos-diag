using System.Globalization;

namespace OficinaDiag.Maintenance;

/// <summary>
/// O antes/depois do relatório de manutenção — função pura, sem adb: recebe
/// duas medições e devolve as linhas formatadas. É aqui que se garante a
/// regra do pedido: nenhum número inventado — sem medição não há linha.
/// </summary>
public static class MaintenanceDiff
{
    /// <summary>1 GiB em bytes — a partir daqui mostra-se em GB.</summary>
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    public static List<MetricDelta> Compute(MaintenanceSnapshot? before, MaintenanceSnapshot? after)
    {
        if (before is null || after is null) return [];

        var rows = new List<MetricDelta>();
        AddBytes(rows, "storage.free", "Armazenamento livre",
            before.StorageFreeBytes, after.StorageFreeBytes, "up-good");
        AddBytes(rows, "memory.available", "RAM disponível",
            before.MemAvailableBytes, after.MemAvailableBytes, "up-good");
        AddCount(rows, "processes.running", "Processos em execução",
            before.RunningProcesses, after.RunningProcesses, "down-good");

        AddScale(rows, "anim.window", "Animação — janela",
            before.Animations?.Window, after.Animations?.Window);
        AddScale(rows, "anim.transition", "Animação — transição",
            before.Animations?.Transition, after.Animations?.Transition);
        AddScale(rows, "anim.animator", "Animação — durator",
            before.Animations?.Animator, after.Animations?.Animator);

        // Contexto informativo: o cliente lê-o, mas a limpeza não o move de
        // forma significativa — sem variação calculada (delta null).
        AddInfo(rows, "battery.level", "Bateria",
            before.BatteryLevelPct is { } bl ? $"{bl}%" : null,
            after.BatteryLevelPct is { } al ? $"{al}%" : null);
        AddInfo(rows, "battery.temp", "Temperatura da bateria",
            before.BatteryTempC is { } bt ? $"{bt:0.#} °C" : null,
            after.BatteryTempC is { } at ? $"{at:0.#} °C" : null);
        AddInfo(rows, "uptime", "Tempo ligado",
            before.UptimeSeconds is { } bu ? FormatUptime(bu) : null,
            after.UptimeSeconds is { } au ? FormatUptime(au) : null);
        AddInfo(rows, "power.saver", "Modo poupança",
            PowerSaverText(before.LowPowerMode), PowerSaverText(after.LowPowerMode));

        return rows;
    }

    private static void AddBytes(List<MetricDelta> rows, string key, string label,
        long? before, long? after, string direction)
    {
        if (before is null && after is null) return;
        rows.Add(new MetricDelta
        {
            Key = key,
            LabelPt = label,
            Before = before is { } b ? FormatBytes(b) : null,
            After = after is { } a ? FormatBytes(a) : null,
            Delta = before is { } bv && after is { } av && av != bv
                ? FormatSignedBytes(av - bv) : null,
            Direction = direction,
        });
    }

    private static void AddCount(List<MetricDelta> rows, string key, string label,
        int? before, int? after, string direction)
    {
        if (before is null && after is null) return;
        rows.Add(new MetricDelta
        {
            Key = key,
            LabelPt = label,
            Before = before?.ToString(CultureInfo.InvariantCulture),
            After = after?.ToString(CultureInfo.InvariantCulture),
            Delta = before is { } b && after is { } a && a != b
                ? (a - b).ToString(CultureInfo.InvariantCulture) : null,
            Direction = direction,
        });
    }

    private static void AddScale(List<MetricDelta> rows, string key, string label,
        double? before, double? after)
    {
        if (before is null && after is null) return;
        rows.Add(new MetricDelta
        {
            Key = key,
            LabelPt = label,
            Before = before is { } b ? FormatScale(b) : null,
            After = after is { } a ? FormatScale(a) : null,
            Delta = before is { } bv && after is { } av && av != bv
                ? FormatSigned((av - bv).ToString("0.##", CultureInfo.InvariantCulture)) : null,
            Direction = "down-good",
        });
    }

    private static void AddInfo(List<MetricDelta> rows, string key, string label,
        string? before, string? after)
    {
        if (before is null && after is null) return;
        rows.Add(new MetricDelta
        {
            Key = key,
            LabelPt = label,
            Before = before,
            After = after,
            Delta = null,
            Direction = "neutral",
        });
    }

    /// <summary>Bytes → "512 MB" / "1.5 GB" legível; invariante para o relatório
    /// (a interpolação normal usaria a cultura do Windows do técnico).</summary>
    public static string FormatBytes(long bytes)
    {
        var b = Math.Abs(bytes);
        if (b >= GiB) return (bytes / (double)GiB).ToString("0.#", CultureInfo.InvariantCulture) + " GB";
        if (b >= MiB) return (bytes / (double)MiB).ToString("0", CultureInfo.InvariantCulture) + " MB";
        return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
    }

    /// <summary>Variação com sinal: "+512 MB" / "-256 MB".</summary>
    public static string FormatSignedBytes(long delta)
    {
        var sign = delta >= 0 ? "+" : "-";
        return sign + FormatBytes(Math.Abs(delta));
    }

    private static string FormatSigned(string value) =>
        value.StartsWith('-') ? value : "+" + value;

    private static string FormatScale(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Uptime legível: "3d 4h" a partir de um dia, senão "5h 12m".</summary>
    public static string FormatUptime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalDays >= 1
            ? $"{(int)ts.TotalDays}d {ts.Hours}h"
            : $"{ts.Hours}h {ts.Minutes}m";
    }

    private static string? PowerSaverText(int? lowPower) => lowPower switch
    {
        1 => "ligado",
        0 => "desligado",
        _ => null,
    };
}
