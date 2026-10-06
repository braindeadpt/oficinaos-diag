using OficinaDiag.Devices;

namespace OficinaDiag.Grading;

/// <summary>Um item do checklist físico — o que o USB não vê, o técnico marca.</summary>
/// <param name="Weight">0 = cosmético (teto B) · 1 = funcional (teto C) · 2 = crítico (teto D)</param>
public sealed record ChecklistItem(string Key, string LabelPt, string LabelEn, int Weight);

/// <summary>Resultado do grading — grau, razões legíveis, peças sugeridas e frase de fecho.</summary>
public sealed class GradeResult
{
    public required string Grade { get; init; } // A | B | C | D
    public required IReadOnlyList<string> Reasons { get; init; }
    public required IReadOnlyList<string> Parts { get; init; }
    public required string Verdict { get; init; }
}

/// <summary>
/// Grade de retoma A–D a partir dos checks do relatório — regras fixas,
/// sem IA, para o veredicto ser reproduzível e defensável numa disputa.
/// Report strings ficam em PT (chegam ao relatório HTML/PDF da loja).
/// </summary>
public static class Grader
{
    /// <summary>Checklist físico partilhado entre o diálogo e o grader.</summary>
    public static readonly ChecklistItem[] ChecklistItems =
    [
        new("checklist.frame", "Moldura / corpo (empenos, mossas)", "Frame / body (bends, dents)", 1),
        new("checklist.backglass", "Vidro traseiro", "Back glass", 1),
        new("checklist.screen_visual", "Ecrã — riscos / manchas visíveis", "Screen — visible scratches / marks", 1),
        new("checklist.buttons", "Botões power / volume", "Power / volume buttons", 1),
        new("checklist.sim_tray", "Gaveta SIM", "SIM tray", 0),
        new("checklist.charge_port", "Porta de carga (folga, pinos, corrosão)", "Charging port (play, pins, corrosion)", 1),
        new("checklist.speaker", "Altifalante", "Loudspeaker", 1),
        new("checklist.earpiece", "Auricular (chamada ao ouvido)", "Earpiece (call to ear)", 1),
        new("checklist.biometrics", "Face ID / Touch ID / leitor", "Face ID / Touch ID / reader", 1),
        new("checklist.cameras", "Câmaras (lente riscada, foco)", "Cameras (scratched lens, focus)", 1),
        new("checklist.liquid", "Sinais de líquido / humidade", "Liquid / moisture signs", 2),
        new("checklist.mic", "Microfone", "Microphone", 1),
    ];

    /// <summary>Locks que inviabilizam revenda — teto D imediato.</summary>
    private static readonly string[] HardLocks =
    [
        "security.mdm", "device.activation",
    ];

    public static GradeResult Compute(DeviceReport r)
    {
        var reasons = new List<string>();
        var parts = new List<string>();
        // 0=A 1=B 2=C 3=D — o grau final é o pior teto encontrado
        var ceiling = 0;
        void Cap(int level, string reason)
        {
            if (level > ceiling) ceiling = level;
            reasons.Add(reason);
        }

        string? Val(string key) =>
            r.Results.TryGetValue(key, out var c) ? c.Value : null;
        string? Stat(string key) =>
            r.Results.TryGetValue(key, out var c) ? c.Status : null;

        // ── bloqueios: o que decide compra de usados ─────────────────────
        foreach (var key in HardLocks)
        {
            var s = Stat(key);
            if (s is "fail" or "warn")
                Cap(3, $"{ReasonLabel(key)}: {Val(key)}");
        }
        if (Stat("security.accounts") is "warn")
            Cap(2, $"conta(s) ativas ({Val("security.accounts")}) — desvincular antes de vender (FRP dispara num reset)");
        if (Stat("security.warranty") is "fail")
            Cap(2, "Knox/warranty bit queimado — fabricante recusa garantia");
        if (Stat("security.bootloader") is "warn" || Stat("security.root") is "warn" || Stat("security.oemunlock") is "warn")
            Cap(2, "software modificado / bootloader desbloqueado — afeta garantia e retoma");

        // ── bateria: o componente que mais se vende ──────────────────────
        var cap = ParsePct(Val("battery.capacity"));
        var capText = cap is { } c ? $"{c}%" : Val("battery.capacity") ?? "?";
        if (Stat("battery.capacity") is "fail" || cap is < 60)
        {
            Cap(3, $"bateria a {capText} — abaixo do mínimo vendável");
            parts.Add("bateria");
        }
        else if (Stat("battery.capacity") is "warn" || cap is < 80)
        {
            Cap(2, $"bateria a {capText} — substituição recomendada");
            parts.Add("bateria");
        }
        else if (Stat("battery.health") is "fail" or "warn")
            Cap(2, $"estado da bateria: {Val("battery.health")}");
        if (Stat("battery.cycles") is "warn")
            Cap(2, $"ciclos altos ({Val("battery.cycles")}) — degradação esperada");

        // ── sessão de potência: porta/cabo + resistência interna ─────────
        if (Stat("power.charge") is "warn" or "fail")
        {
            Cap(2, $"caminho de carga fraco ({Val("power.charge")})");
            parts.Add("porta de carga");
        }
        if (Stat("power.resistance") is "warn")
        {
            Cap(2, $"resistência interna alta ({Val("power.resistance")}) — célula envelhecida");
            parts.Add("bateria");
        }

        // ── ecrã / toque ─────────────────────────────────────────────────
        if (Stat("test.touch") is "fail")
        {
            Cap(3, $"toque falhou ({Val("test.touch")}) — ecrã defeituoso");
            parts.Add("ecrã");
        }
        else if (Stat("test.touch") is "warn")
        {
            Cap(2, $"toque parcial ({Val("test.touch")})");
            parts.Add("ecrã");
        }
        if (Stat("test.screen") is "warn" or "fail")
        {
            Cap(Stat("test.screen") == "fail" ? 3 : 2, "ecrã com defeitos visuais");
            parts.Add("ecrã");
        }

        // ── checklist físico ─────────────────────────────────────────────
        var checklistDone = 0;
        foreach (var item in ChecklistItems)
        {
            var s = Stat(item.Key);
            if (s is null or "skipped") continue;
            checklistDone++;
            if (s is "warn" or "fail")
            {
                Cap(item.Weight, $"{item.LabelPt} — defeito marcado pelo técnico");
                var part = PartFor(item.Key);
                if (part is not null) parts.Add(part);
            }
        }

        // ── restantes falhas funcionais ──────────────────────────────────
        if (Stat("sensor.vibrate") is "fail")
        {
            Cap(2, "motor de vibração não respondeu");
            parts.Add("motor de vibração");
        }
        if (Stat("logs.panics") is "fail" or "warn")
            Cap(2, $"kernel panics / falhas de sistema no histórico ({Val("logs.panics")}) — investigar placa");

        // ── montagem do veredicto ────────────────────────────────────────
        var grade = "ABCD"[ceiling].ToString();
        var coverage = r.Results.Count(kv =>
            !kv.Key.StartsWith("grade.", StringComparison.Ordinal)
            && !kv.Key.StartsWith("suggest.", StringComparison.Ordinal));
        if (coverage == 0)
        {
            grade = "—";
            reasons.Clear();
            reasons.Add("sem dados de scan — liga um telefone ou corre o SCAN primeiro");
        }
        else if (checklistDone == 0)
        {
            reasons.Add("checklist físico por preencher — o grau pode melhorar ou piorar");
        }

        var verdict = reasons.Count == 0
            ? $"Grau {grade} — sem anomalias nas {coverage} verificações."
            : $"Grau {grade} — {string.Join("; ", reasons.Distinct())}.";

        return new GradeResult
        {
            Grade = grade,
            Reasons = reasons.Distinct().ToList(),
            Parts = parts.Distinct().ToList(),
            Verdict = verdict,
        };
    }

    /// <summary>Escreve o grau, veredicto e peças como checks do relatório — viajam no intake/IA.</summary>
    public static void ApplyTo(DeviceReport r, GradeResult g)
    {
        r.Set("grade.overall", g.Grade switch
        {
            "D" => "fail",
            "C" => "warn",
            _ => "pass",
        }, g.Grade, g.Verdict);
        if (g.Parts.Count > 0)
            r.Set("suggest.parts", "info", string.Join(", ", g.Parts),
                "peças prováveis a partir das falhas — confirma antes de orçamentar");
    }

    /// <summary>Atalho: recomputa e grava o grau no próprio relatório.</summary>
    public static GradeResult Apply(DeviceReport r)
    {
        var g = Compute(r);
        ApplyTo(r, g);
        return g;
    }

    private static string? PartFor(string checklistKey) => checklistKey switch
    {
        "checklist.charge_port" => "porta de carga",
        "checklist.screen_visual" => "ecrã",
        "checklist.backglass" => "tampa/vidro traseiro",
        "checklist.buttons" => "flex de botões",
        "checklist.speaker" => "altifalante",
        "checklist.earpiece" => "auricular",
        "checklist.biometrics" => "sensor biométrico",
        "checklist.cameras" => "câmara",
        "checklist.mic" => "microfone",
        _ => null,
    };

    private static string ReasonLabel(string key) => key switch
    {
        "security.mdm" => "gestão MDM ativa",
        "device.activation" => "bloqueio de ativação",
        _ => key,
    };

    private static int? ParsePct(string? v)
    {
        if (v is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(v, @"(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
    }
}
