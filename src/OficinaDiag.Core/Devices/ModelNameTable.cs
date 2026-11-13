using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            var path = Path.Combine(AppContext.BaseDirectory, PackagePaths.ModelsJsonRelativePath);
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return dict is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Table lookup only — null when the code isn't catalogued.
    /// Códigos Samsung com sufixo regional/dual-SIM (SM-S918U1, SM-S918B/DS,
    /// SM-S918F…) caem na variante base SM-S918B em vez de triplicar o JSON.
    /// </summary>
    public static string? Lookup(string? modelCode)
    {
        if (string.IsNullOrWhiteSpace(modelCode)) return null;
        var code = modelCode.Trim();
        if (Table.Value.TryGetValue(code, out var exact)) return exact;
        foreach (var candidate in SamsungBaseCodes(code))
            if (Table.Value.TryGetValue(candidate, out var name)) return name;
        return null;
    }

    /// <summary>Candidatos base de um código Samsung, do mais para o menos específico.</summary>
    private static IEnumerable<string> SamsungBaseCodes(string code)
    {
        if (!code.StartsWith("SM-", StringComparison.OrdinalIgnoreCase)) yield break;

        // SM-S918B/DS → SM-S918B; o sufixo dual-SIM vem depois da barra.
        var slash = code.IndexOf('/');
        var stem = (slash > 0 ? code[..slash] : code).ToUpperInvariant();
        if (stem != code && Table.Value.ContainsKey(stem)) yield return stem;

        // SM-S918U1 / SM-S918U / SM-S918W / SM-S918F … → SM-S918B (a base
        // europeia do mesmo modelo; a tabela só guarda uma variante por modelo).
        var m = Regex.Match(stem, @"^(SM-[A-Z]\d{3})[A-Z0-9]*$", RegexOptions.IgnoreCase);
        if (!m.Success) yield break;
        var baseCode = m.Groups[1].Value.ToUpperInvariant() + "B";
        if (!string.Equals(baseCode, stem, StringComparison.OrdinalIgnoreCase))
            yield return baseCode;
    }

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
