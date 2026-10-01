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

/// <summary>Device identity block — mirrors the cloud /reports/diagnostic contract.</summary>
public sealed class DeviceIdentity
{
    public string? Brand { get; set; }
    public string? Model { get; set; }
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
    public string ToolVersion { get; set; } = "0.1.0";
    public DateTimeOffset CollectedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Platform { get; set; } = "unknown"; // android | ios
    public DeviceIdentity Device { get; set; } = new();

    /// <summary>Check name → result. Sent verbatim to the AI report endpoint.</summary>
    public Dictionary<string, CheckResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw key/value grabs kept for debugging — never sent to the AI.</summary>
    [JsonIgnore]
    public Dictionary<string, string> Raw { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Notes { get; set; }

    public void Set(string key, string status, string? value, string? detail = null) =>
        Results[key] = new CheckResult { Status = status, Value = value, Detail = detail };
}
