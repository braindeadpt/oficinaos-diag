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
        // O Core tem a sua própria versão: quem manda é a da app publicada
        // (o `/p:Version=` injeta no executável, não nesta biblioteca).
        (System.Reflection.Assembly.GetEntryAssembly()
            ?? System.Reflection.Assembly.GetExecutingAssembly()).GetName().Version?.ToString(3)
        ?? "0.0.0";
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
    /// cliente envia para venda); "repair"/null = reparação (o default da Cloud).
    /// "maintenance" é aceite no /reports/diagnostic para o relatório IA de
    /// manutenção — a app envia-o como override do pedido, sem o gravar aqui.
    /// </summary>
    public string? Purpose { get; set; }

    /// <summary>
    /// Fotos de bancada anexadas pelo técnico — paths locais, embutidas em
    /// base64 só na renderização HTML. Não viajam para a cloud (os payloads
    /// de intake/IA são construídos campo a campo, sem este).
    /// </summary>
    [JsonInclude]
    public List<string> PhotoPaths { get; private set; } = [];

    /// <summary>
    /// Limpeza/otimização medida (antes/ações/depois). Fica FORA de Results
    /// porque não é um check de diagnóstico: inflaria a cobertura do
    /// Grader e apareceria nas vistas cliente/seguro. Serializado só no
    /// JSON da cloud (campo aditivo — o schema zod do lado dela ignora
    /// chaves desconhecidas).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Maintenance.MaintenanceReport? Maintenance { get; set; }

    // No timeout do scan iOS a task órfã pode continuar a chamar Set() —
    // o gate torna essa escrita segura e o snapshot dá uma cópia estável.
    private readonly object _gate = new();

    public void Set(string key, string status, string? value, string? detail = null)
    {
        lock (_gate)
            Results[key] = new CheckResult { Status = status, Value = value, Detail = detail };
    }

    // Namespaces que um re-scan NÃO volta a produzir — checklist manual,
    // teste no ecrã e bancada de potência. Tudo o resto (battery.*,
    // security.*, device.*, derivados como battery.swap) é refeito na
    // recolha e não pode ser arrastado de um scan anterior.
    private static readonly string[] ManualPrefixes = ["checklist.", "test.", "sensor.", "power."];

    /// <summary>
    /// Copia para ESTE relatório os resultados manuais de
    /// <paramref name="previous"/> que aqui não existem — checklist,
    /// teste no ecrã e bancada — mais os anexos do checklist (notas,
    /// finalidade, fotos). Chamado quando um novo scan substitui o
    /// relatório do mesmo aparelho: sem isto as medições manuais
    /// perdiam-se; com chaves a mais arrastava-se hardware obsoleto.
    /// </summary>
    public void PreserveManualFrom(DeviceReport previous)
    {
        foreach (var kv in previous.Results)
            if (!Results.ContainsKey(kv.Key)
                && (ManualPrefixes.Any(kv.Key.StartsWith) || kv.Key == "usb.stability"))
                Results[kv.Key] = kv.Value;
        Notes ??= previous.Notes;
        Purpose ??= previous.Purpose;
        PhotoPaths.AddRange(previous.PhotoPaths.Where(p => !PhotoPaths.Contains(p)));
        // A limpeza medida sobrevive ao re-scan: é trabalho manual do técnico,
        // como o checklist — um novo scan não a reproduz.
        Maintenance ??= previous.Maintenance;
    }

    /// <summary>Cópia consistente de <see cref="Results"/> — os CheckResult são
    /// partilhados mas nunca mutados (Set substitui sempre o objeto inteiro).</summary>
    public Dictionary<string, CheckResult> SnapshotResults()
    {
        lock (_gate)
            return new Dictionary<string, CheckResult>(Results, Results.Comparer);
    }
}
