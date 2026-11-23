using System.Text.Json;

namespace OficinaDiag.Tests;

/// <summary>
/// O que a página de perfil (<c>perf-test.html</c>) mede do lado do
/// telemóvel e devolve no POST <c>perf-result</c>: motor usado, frames,
/// segundos corridos e fps médio/pior janela de 1 s. É dado medido, não
/// veredicto — os limiares vivem no <see cref="Devices.PerfAnalyzer"/>.
/// </summary>
public sealed record PerfPageStats(
    /// <summary>"webgl2" | "webgl" | "canvas2d" | "none" — none = stress não arrancou.</summary>
    string? Engine,
    int Frames,
    /// <summary>Segundos ATIVOS de stress (ecrã visível) — tempo oculto não conta.</summary>
    double Secs,
    double? FpsAvg,
    /// <summary>Pior janela de 1 s em fps — stalls do browser/thermal aparecem aqui.</summary>
    double? FpsMin,
    bool ContextLost,
    /// <summary>Segundos com o ecrã oculto/bloqueado durante a sessão — excluídos de Secs.</summary>
    double HiddenSecs = 0)
{
    /// <summary>
    /// Parse tolerante do payload da LAN — TryGet* em vez de Get*: um tipo
    /// errado não pode rebentar a análise. Exige <c>perfFrames</c> e
    /// <c>perfSecs</c> numéricos (o mínimo que distingue uma medição real de
    /// ruído); sem eles devolve null e o check de workload sai skipped.
    /// </summary>
    public static PerfPageStats? FromJson(IReadOnlyDictionary<string, JsonElement> data)
    {
        if (Num(data, "perfFrames") is not { } frames
            || Num(data, "perfSecs") is not { } secs)
            return null;

        var engine = data.TryGetValue("perfEngine", out var e)
            && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var lost = data.TryGetValue("perfContextLost", out var l)
            && l.ValueKind is JsonValueKind.True;
        return new PerfPageStats(engine, (int)frames, secs,
            Num(data, "perfFpsAvg"), Num(data, "perfFpsMin"), lost,
            Num(data, "perfHiddenSecs") ?? 0);
    }

    /// <summary>Número opcional do payload — tipo errado não rebenta o parse.</summary>
    private static double? Num(IReadOnlyDictionary<string, JsonElement> data, string key) =>
        data.TryGetValue(key, out var v)
        && v.ValueKind == JsonValueKind.Number
        && v.TryGetDouble(out var d) ? d : null;
}
