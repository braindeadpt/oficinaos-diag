using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OficinaDiag.Cloud;

/// <summary>
/// Local config persisted next to the exe (the tool is portable).
/// Holds the cloud endpoint, shop stamp and the Pro token (DPAPI-protected).
/// </summary>
public sealed class DiagConfig
{
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "oficinaos-diag.config.json");

    public const string DefaultCloudUrl = "https://cloud.oficinaos.app";

    public string CloudUrl { get; set; } =
        Environment.GetEnvironmentVariable("OFICINAOS_CLOUD_URL")
        ?? DefaultCloudUrl;

    // Preferências de UI — escolhidas no diálogo Opções.
    public string? Language { get; set; }  // "pt" | "en" — null = auto (idioma do Windows)
    public string? Theme { get; set; }     // "terminal" | "win95" | "fluent"

    // Token Pro (relatórios IA) — nunca em claro no disco: guardamos a versão
    // cifrada com DPAPI (âmbito CurrentUser). Sem ele, o AI pede o token à vez.
    // RISCO CONHECIDO (revisão 2026-10, sem redesenho nesta versão): isto é
    // o shop token COMPLETO — o mesmo da app (publica o portal, muda o
    // whatsapp-config, lê reservas…) — só para gerar relatórios IA. O DPAPI
    // (CurrentUser) protege contra cópia do ficheiro para outra máquina, mas
    // não contra outro processo do mesmo utilizador num PC de bancada.
    // TODO(security): tokens com âmbito (ex. scope ["ai-reports"]) emitidos no
    // dashboard da Cloud só para o diag, revogáveis por PC.
    public string? ShopTokenProtected { get; set; }

    // Identidade da loja — carimbo do relatório para seguradora.
    // Guardada localmente; pedida na 1ª emissão e pré-preenchida depois.
    public string? ShopName { get; set; }
    public string? ShopNif { get; set; }
    public string? ShopPhone { get; set; }
    public string? ShopAddress { get; set; }

    /// <summary>URL http/https absoluto? Rejeita lixo que partiria `new Uri` no arranque.</summary>
    public static bool TryValidateCloudUrl(string? url, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            && !string.IsNullOrEmpty(u.Host)
            && (uri = u) is not null;
    }

    /// <summary>http:// para fora de localhost — Bearer token e PII em claro na rede.</summary>
    public static bool IsInsecureUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && uri.Host is not ("localhost" or "127.0.0.1" or "::1");

    public string? GetShopToken()
    {
        if (string.IsNullOrEmpty(ShopTokenProtected)) return null;
        try
        {
            var raw = ProtectedData.Unprotect(
                Convert.FromBase64String(ShopTokenProtected), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch { return null; } // cifrado por outro utilizador/máquina — pede de novo
    }

    public void SetShopToken(string? token)
    {
        ShopTokenProtected = string.IsNullOrWhiteSpace(token) ? null
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static DiagConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<DiagConfig>(File.ReadAllText(Path));
                if (cfg is not null)
                {
                    // URL inválido não pode partir o arranque — volta ao default.
                    if (!TryValidateCloudUrl(cfg.CloudUrl, out _))
                        cfg.CloudUrl = DefaultCloudUrl;
                    return cfg;
                }
            }
        }
        catch { /* corrupt config → defaults */ }
        return new DiagConfig();
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this)); }
        catch { /* read-only dir — config just won't persist */ }
    }
}
