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
    // Portable-first: junto do exe para quem corre o ZIP numa pen. Quando o
    // dir não é gravável (instalação MSIX/Store — pasta de instalação só de
    // leitura), cai para %APPDATA%\OficinaDiag — sem o fallback o tema, o
    // idioma e o token perdiam-se a cada arranque na versão da Store.
    private static readonly string PortablePath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "oficinaos-diag.config.json");
    private static readonly string AppDataPath =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OficinaDiag", "oficinaos-diag.config.json");

    public const string DefaultCloudUrl = "https://cloud.oficinaos.app";

    public string CloudUrl { get; set; } =
        Environment.GetEnvironmentVariable("OFICINAOS_CLOUD_URL")
        ?? DefaultCloudUrl;

    // Preferências de UI — escolhidas no diálogo Opções.
    public string? Language { get; set; }  // "pt" | "en" — null = auto (idioma do Windows)
    public string? Theme { get; set; }     // "terminal" | "win95" | "fluent"

    // Token Pro (relatórios IA) — nunca em claro no disco: guardamos a versão
    // cifrada com DPAPI (âmbito CurrentUser). Sem ele, o AI pede o token à vez.
    // RESOLVIDO (2026-10): quem cola um código de emparelhamento (XXXX-YYYY)
    // recebe um token com scope "diag" — só serve /reports/diagnostic. Um PC
    // de bancada comprometido deixa de poder publicar a montra ou mexer no
    // WhatsApp da loja. O DPAPI (CurrentUser) continua a proteger contra
    // cópia do ficheiro para outra máquina; contra outro processo do mesmo
    // utilizador, o scope é a segunda linha de defesa.
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
        foreach (var path in new[] { PortablePath, AppDataPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var cfg = JsonSerializer.Deserialize<DiagConfig>(File.ReadAllText(path));
                if (cfg is null) continue;
                // URL inválido não pode partir o arranque — volta ao default.
                if (!TryValidateCloudUrl(cfg.CloudUrl, out _))
                    cfg.CloudUrl = DefaultCloudUrl;
                return cfg;
            }
            catch { /* corrupt config → try the next location / defaults */ }
        }
        return new DiagConfig();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this);
        if (!TryWriteAtomic(PortablePath, json))
            try { TryWriteAtomic(AppDataPath, json); } catch { /* sem disco gravável */ }
    }

    /// <summary>tmp+rename — um crash a meio da escrita não deixa JSON truncado.</summary>
    private static bool TryWriteAtomic(string path, string content)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }
}
