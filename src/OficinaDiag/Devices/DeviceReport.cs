using System.Text.Json.Serialization;

namespace OficinaDiag.Devices;

/// <summary>A single check inside a scan — what the AI report and the UI consume.</summary>
public sealed class CheckResult
{
    /// <summary>pass | warn | fail | info | skipped</summary>
    public string Status { get; set; } = "info";
    public string? Value { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Ambient battery readout for the header ticker — polled while a device is connected.</summary>
public sealed record LiveTelemetry(int? Milliamps, double? Volts, double? TempC, int? Percent,
    bool? Charging, double? NegotiatedWatts = null, double? SocTempC = null);

/// <summary>Device identity block — mirrors the cloud /reports/diagnostic contract.</summary>
public sealed class DeviceIdentity
{
    public string? Brand { get; set; }
    public string? Model { get; set; }
    /// <summary>Nome de marketing resolvido (prop ou tabela) — "Galaxy S23 Ultra" em vez de "SM-S918B".</summary>
    public string? MarketingName { get; set; }
    public string? Os { get; set; }
    public string? OsVersion { get; set; }
    public string? Serial { get; set; }
}

/// <summary>
/// A complete scan. Serializes to the JSON contract the cloud
/// /reports/diagnostic and /intake endpoints accept.
/// </summary>
public sealed class DeviceReport
{
    public string Tool { get; set; } = "oficinaos-diag";
    public string ToolVersion { get; set; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Platform { get; set; } = "unknown"; // android | ios
    public DeviceIdentity Device { get; set; } = new();

    /// <summary>Check name → result. Sent verbatim to the AI report endpoint.</summary>
    /// <remarks>
    /// Populate (não Replace): com setter privado o STJ criava um Dictionary
    /// novo com o comparer por defeito — o OrdinalIgnoreCase perdia-se no
    /// round-trip do histórico e lookups case-insensitive falhavam.
    /// </remarks>
    [JsonInclude]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<string, CheckResult> Results { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw key/value grabs kept for debugging — never sent to the AI.</summary>
    [JsonIgnore]
    public Dictionary<string, string> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tail dos logs de crash/panic do dispositivo — enviado à IA para
    /// interpretação (iOS panics = suspeita de hardware; app crashes = info).
    /// </summary>
    public string? LogsText { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// "sale" quando o técnico marca a avaliação como retoma/compra (ou o
    /// cliente envia para venda); null = reparação (o default da Cloud).
    /// Enviado como <c>purpose</c> ao /reports/diagnostic.
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// Fotos de bancada anexadas pelo técnico — paths locais, embutidas em
    /// base64 só na renderização HTML. Não viajam para a cloud (os payloads
    /// de intake/IA são construídos campo a campo, sem este).
    /// </summary>
    [JsonInclude]
    public List<string> PhotoPaths { get; private set; } = [];

    // No timeout do scan iOS a task órfã pode continuar a chamar Set() —
    // o gate torna essa escrita segura e o snapshot dá uma cópia estável.
    private readonly object _gate = new();

    public void Set(string key, string status, string? value, string? detail = null)
    {
        lock (_gate)
            Results[key] = new CheckResult { Status = status, Value = value, Detail = detail };
    }

    /// <summary>Cópia consistente de <see cref="Results"/> — os CheckResult são
    /// partilhados mas nunca mutados (Set substitui sempre o objeto inteiro).</summary>
    public Dictionary<string, CheckResult> SnapshotResults()
    {
        lock (_gate)
            return new Dictionary<string, CheckResult>(Results, Results.Comparer);
    }
}
