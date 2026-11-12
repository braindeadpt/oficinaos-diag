using System.IO;
using System.Text.Json;

namespace OficinaDiag.Devices;

/// <summary>
/// Resolves Android marketing names. Order:
/// 1) vendor marketname props (Xiaomi/Oppo/Huawei/Samsung OneUI ≥4 expose them)
/// 2) bundled models.json — model code (SM-S918B, 2312DRA50G…) → marketing name
/// 3) null — caller keeps the raw model prop
/// The table is data, not code — extend Data/models.json without rebuilding.
/// </summary>
public static class ModelNameTable
{
    /// <summary>getprop keys known to carry a marketing name, most reliable first.</summary>
    public static readonly string[] MarketingProps =
    [
        "ro.product.marketname",          // Xiaomi/Redmi/Poco, Huawei, Honor
        "ro.product.vendor.marketname",   // Xiaomi newer, Samsung OneUI 4+
        "ro.product.system.marketname",   // Samsung OneUI 6+, alguns Oppo
        "ro.product.odm.marketname",      // Xiaomi partições odm
        "ro.product.product.marketname",  // casos product-partition
        "ro.config.marketing_name",       // Samsung mais antigos
        "ro.vendor.oplus.market.name",    // Oppo/Realme/OnePlus
        "ro.product.market.name",         // Oppo/Realme variante
    ];

    private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

    private static Dictionary<string, string> Load()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "models.json");
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return dict is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Table lookup only — null when the code isn't catalogued.</summary>
    public static string? Lookup(string? modelCode) =>
        modelCode is not null && Table.Value.TryGetValue(modelCode.Trim(), out var name) ? name : null;

    /// <summary>Normaliza: prop/table value limpo; rejeita valores que são só o código.</summary>
    public static string? Clean(string? candidate, string? rawModel)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var c = candidate.Trim();
        // alguns OEMs copiam o código para o marketname — inútil
        return rawModel is not null && c.Equals(rawModel.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null : c;
    }
}
