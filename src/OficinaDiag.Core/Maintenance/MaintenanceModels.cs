namespace OficinaDiag.Maintenance;

/// <summary>Escalas de animação do Android (janela, transição, animator).</summary>
/// <param name="Window">window_animation_scale</param>
/// <param name="Transition">transition_animation_scale</param>
/// <param name="Animator">animator_duration_scale</param>
public sealed record AnimationScales(double? Window, double? Transition, double? Animator);

/// <summary>
/// Medição instantânea do aparelho — só o que o adb lê de forma fiável.
/// Cada campo é null quando o aparelho não expôs o valor: o relatório mostra
/// "—" em vez de inventar um número.
/// </summary>
public sealed class MaintenanceSnapshot
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Espaço livre em /data (df).</summary>
    public long? StorageFreeBytes { get; set; }
    public long? StorageUsedBytes { get; set; }

    /// <summary>RAM disponível (MemAvailable do /proc/meminfo).</summary>
    public long? MemAvailableBytes { get; set; }

    public int? BatteryLevelPct { get; set; }
    public double? BatteryTempC { get; set; }

    /// <summary>Tempo desde o último boot (/proc/uptime).</summary>
    public long? UptimeSeconds { get; set; }

    public AnimationScales? Animations { get; set; }

    /// <summary>Processos visíveis no `ps` — proxy de "apps a correr".</summary>
    public int? RunningProcesses { get; set; }

    /// <summary>settings global low_power: 0 = desligado, 1 = modo poupança.</summary>
    public int? LowPowerMode { get; set; }
}

/// <summary>Resultado de uma ação de manutenção — ok | partial | fail | skipped.</summary>
public sealed class MaintenanceActionResult
{
    /// <summary>Chave estável da ação (trim-caches, clear-cache, …).</summary>
    public required string Key { get; init; }

    /// <summary>Rótulo legível para o relatório HTML (PT — segue o resto do relatório).</summary>
    public required string LabelPt { get; init; }

    /// <summary>ok | partial | fail | skipped</summary>
    public required string Status { get; init; }

    /// <summary>Detalhe curto: "12 apps", erro do aparelho, etc.</summary>
    public string? Detail { get; init; }
}

/// <summary>Uma linha do antes/depois — valores já formatados para o relatório.</summary>
public sealed class MetricDelta
{
    /// <summary>Chave estável da métrica (storage.free, memory.available, …).</summary>
    public required string Key { get; init; }

    /// <summary>Rótulo legível (PT).</summary>
    public required string LabelPt { get; init; }

    public string? Before { get; init; }
    public string? After { get; init; }

    /// <summary>Variação formatada ("+512 MB", "-12") — null quando não mensurável.</summary>
    public string? Delta { get; init; }

    /// <summary>up-good | down-good | neutral — direção que melhora o telemóvel.</summary>
    public string Direction { get; init; } = "neutral";
}

/// <summary>
/// Limpeza/otimização medida: o que se mediu antes, as ações executadas e o
/// que se mediu depois. Sem estimativas — cada número vem de uma leitura adb.
/// </summary>
public sealed class MaintenanceReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public MaintenanceSnapshot? Before { get; set; }
    public MaintenanceSnapshot? After { get; set; }
    public List<MaintenanceActionResult> Actions { get; set; } = [];
    public List<MetricDelta> Deltas { get; set; } = [];

    /// <summary>
    /// Escalas de animação lidas ANTES da limpeza — o "repor animações" usa
    /// estes valores; sem eles não há para onde voltar.
    /// </summary>
    public AnimationScales? AnimationsBefore { get; set; }

    /// <summary>Monta o relatório a partir das medições e das ações executadas.</summary>
    public static MaintenanceReport Build(
        MaintenanceSnapshot? before, MaintenanceSnapshot? after,
        IEnumerable<MaintenanceActionResult> actions)
    {
        return new MaintenanceReport
        {
            StartedAt = before?.At ?? after?.At ?? DateTimeOffset.UtcNow,
            FinishedAt = after?.At ?? before?.At ?? DateTimeOffset.UtcNow,
            Before = before,
            After = after,
            Actions = actions.ToList(),
            Deltas = MaintenanceDiff.Compute(before, after),
        };
    }
}
