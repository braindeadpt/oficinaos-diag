using System.IO;
using System.Text.Json;

namespace OficinaDiag.Cloud;

/// <summary>
/// Local config persisted next to the exe (the tool is portable).
/// Only holds the cloud endpoint — shop codes are typed per send.
/// </summary>
public sealed class DiagConfig
{
    private static readonly string Path =
        System.IO.Path.Combine(AppContext.BaseDirectory, "oficinaos-diag.config.json");

    public string CloudUrl { get; set; } =
        Environment.GetEnvironmentVariable("OFICINAOS_CLOUD_URL")
        ?? "http://192.168.1.118:4100";

    public static DiagConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var cfg = JsonSerializer.Deserialize<DiagConfig>(File.ReadAllText(Path));
                if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.CloudUrl))
                    return cfg;
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
