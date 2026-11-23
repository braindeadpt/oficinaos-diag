using System.Globalization;
using System.Text.RegularExpressions;
using OficinaDiag.Devices;
using OficinaDiag.Grading;

namespace OficinaDiag.Health;

/// <summary>Faixa do score — decide a etiqueta mostrada na UI e a cor do check.</summary>
public enum HealthBand
{
    /// <summary>Nenhuma categoria medida — não há base para um número.</summary>
    None,
    /// <summary>≥ 75 — tudo o que foi medido está dentro do esperado.</summary>
    Healthy,
    /// <summary>≥ 50 — funcionais com desgaste ou ressalvas pontuais.</summary>
    Fair,
    /// <summary>&lt; 50 — problemas confirmados que pesam na experiência do cliente.</summary>
    Concerning,
}

/// <summary>Uma categoria do score — Score null = sem dados (fora do peso).</summary>
public sealed record HealthCategoryScore(string Key, int? Score, string? Detail);

/// <summary>
/// Resultado do health score: número 0–100, faixa, breakdown por categoria e
/// veredicto em PT para o relatório (mesma convenção do <see cref="Grader"/>).
/// </summary>
public sealed class HealthScoreResult
{
    /// <summary>0–100, ou null quando nenhum sinal foi medido.</summary>
    public required int? Score { get; init; }
    public required HealthBand Band { get; init; }
    /// <summary>As seis categorias sempre — Score null marca as que ficaram de fora.</summary>
    public required IReadOnlyList<HealthCategoryScore> Categories { get; init; }
    /// <summary>Frase PT para o relatório — "Saudável — bateria 92 · …".</summary>
    public required string Verdict { get; init; }

    /// <summary>Chave L10n da etiqueta da faixa — a UI traduz, o relatório não.</summary>
    public string BandL10nKey => Band switch
    {
        HealthBand.Healthy => "health.band.healthy",
        HealthBand.Fair => "health.band.fair",
        HealthBand.Concerning => "health.band.concerning",
        _ => "health.band.none",
    };
}

/// <summary>
/// Health score 0–100 do telemóvel — índice de marketing/UX que resume o
/// estado do aparelho num número que o cliente entende. <b>Não substitui o
/// grau A–D</b> do <see cref="Grader"/>: o grau decide retoma com regras
/// duras e reproduzíveis; o score é uma leitura suave para vitrine e
/// comparações antes/depois.
///
/// Regras honestas:
/// - Cada ponto rastreia a um check real do <see cref="DeviceReport"/> —
///   nunca se inventam valores.
/// - Um check ausente ou <c>skipped</c> sai do cálculo: o peso da categoria
///   redistribui-se pelas medidas que existem (um iPhone sem auditoria Pro
///   não é penalizado por ela).
/// - <c>info</c> só pontua onde o próprio coletor o usa como veredicto
///   ("dentro do saudável", "caminho de carga íntegro", temperatura normal);
///   onde é só contexto (logs.crashes de apps, storage.wear por baixo do
///   limiar) fica fora — caso contrário todo o scan Android nascia com
///   pontos em falta.
///
/// Mapeamento base status → pontos: pass=100 · warn=55 · fail=20. Os desvios
/// por check estão documentados em cada scorer — são decisões de produto
/// (ex.: PIN/PUK do cartão do cliente não é defeito do aparelho → warn=85),
/// não dados medidos.
/// </summary>
public static class HealthScore
{
    // Pesos das categorias (somam 100). Bateria e segurança pesam mais porque
    // são o que mais decide a experiência do cliente e a retoma; temperatura
    // pesa menos porque uma leitura única é um sinal fraco.
    private const double WBattery = 25;
    private const double WStorage = 15;
    private const double WSecurity = 20;
    private const double WStability = 15;
    private const double WTemperature = 10;
    private const double WTest = 15;

    /// <summary>Faixa → check status: a linha health.score colore como os outros.</summary>
    private static string StatusFor(int score) => score >= 75 ? "pass" : score >= 50 ? "warn" : "fail";

    /// <summary>Status base → pontos. info=-1 = fora do cálculo (contexto, não veredicto).</summary>
    private static double? ByStatus(CheckResult? c, double warn = 55, double fail = 20, double info = -1) =>
        c?.Status switch
        {
            "pass" => 100,
            "warn" => warn,
            "fail" => fail,
            "info" when info >= 0 => info,
            _ => null,
        };

    // ── scorers por check ────────────────────────────────────────────────
    // Assinatura (key, check): o key é preciso para os checklist.* — o peso do
    // item vem do Grader.ChecklistItems e não do status sozinho.

    /// <summary>Percentagem real lida ("95%") → pontos diretos; sem número cai no status.</summary>
    private static double? Percent(string _, CheckResult? c)
    {
        if (c is null) return null;
        return ParsePercent(c.Value) ?? ByStatus(c);
    }

    /// <summary>
    /// battery.health: o Android escreve o veredicto do fuel gauge
    /// (good/overheat/dead/…) e relatórios antigos de iOS traziam a
    /// percentagem aqui — aproveita-se o formato mais fino que existir.
    /// </summary>
    private static double? BatteryHealth(string _, CheckResult? c)
    {
        if (c is null) return null;
        var v = c.Value?.Trim();
        if (!string.IsNullOrEmpty(v))
        {
            if (ParsePercent(v) is { } p) return p;
            var word = v.ToLowerInvariant() switch
            {
                "dead" or "failure" => 25.0,
                "overheat" => 45,
                "over-voltage" => 40,
                _ => (double?)null,
            };
            if (word is { } w) return w;
        }
        return ByStatus(c);
    }

    /// <summary>
    /// Espaço livre → pontos por faixas: ≥20% livre = sem stress (100);
    /// &lt;5% = crítico (20) — apps falham e updates OTA bloqueiam. O valor é
    /// medido ("85% usado" no Android, "X GB livres de Y GB" no iOS); sem
    /// número não há o que pontuar — storage é sempre "info".
    /// </summary>
    private static double? StorageFree(string _, CheckResult? c)
    {
        if (c is null) return null;
        var v = c.Value ?? "";
        double? freePct = null;
        var used = Regex.Match(v, @"(\d+(?:[.,]\d+)?)\s*%\s*usado");
        if (used.Success
            && double.TryParse(used.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var u))
            freePct = 100 - u;
        else
        {
            // iOS: "12,3 GB livres de 128 GB" — vírgula decimal pt-PT.
            var ios = Regex.Match(v, @"([\d.,]+)\s*GB livres de\s*([\d.,]+)\s*GB",
                RegexOptions.IgnoreCase);
            if (ios.Success
                && double.TryParse(ios.Groups[1].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var free)
                && double.TryParse(ios.Groups[2].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
                && total > 0)
                freePct = free / total * 100;
        }
        if (freePct is not { } f) return null;
        return f >= 20 ? 100 : f >= 10 ? 75 : f >= 5 ? 50 : 20;
    }

    /// <summary>
    /// Checklist físico: o peso do item (0=cosmético, 1=funcional, 2=crítico —
    /// o mesmo do Grader) modula o "defeito": uma gaveta SIM partida não pesa
    /// o mesmo que líquido detetado.
    /// </summary>
    private static double? Checklist(string key, CheckResult? c)
    {
        if (c is null) return null;
        var item = Grader.ChecklistItems.FirstOrDefault(i => i.Key == key);
        if (item is null) return ByStatus(c);
        return c.Status switch
        {
            "pass" => 100.0,
            "warn" => 55.0,
            "fail" => item.Weight switch { 2 => 20.0, 1 => 40.0, _ => 65.0 },
            _ => null,
        };
    }

    private static int? ParsePercent(string? v)
    {
        if (v is null) return null;
        var m = Regex.Match(v, @"(\d{1,3})\s*%");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? Math.Clamp(n, 0, 100) : null;
    }

    // ── tabelas de sinais por categoria ──────────────────────────────────
    // (check, peso relativo dentro da categoria, scorer). Sinais ausentes ou
    // skipped não entram — o peso redistribui-se pelos presentes.

    private delegate double? Scorer(string key, CheckResult? c);
    private readonly record struct Signal(string Key, double Weight, Scorer Score);
    private sealed record Category(string Key, string LabelPt, double Weight, Signal[] Signals);

    private static readonly Category[] Categories =
    [
        new("battery", "bateria", WBattery,
        [
            // Capacidade real vs design é a medida de saúde da célula —
            // manda sozinha; health e ciclos são sinais secundários.
            new("battery.capacity", 3, Percent),
            new("battery.health", 1, BatteryHealth),
            // ≤800 ciclos = dentro do esperado mas já com desgaste (85);
            // >800 = warn (45).
            new("battery.cycles", 1, (k, c) => ByStatus(c, warn: 45, info: 85)),
            // SCAN+: caminho de carga íntegro é veredicto positivo (95 —
            // o carregador de parede nunca é medido, logo não é 100).
            new("power.charge", 1, (k, c) => ByStatus(c, warn: 45, info: 95)),
            new("power.resistance", 1, (k, c) => ByStatus(c, warn: 40, info: 95)),
        ]),
        new("storage", "armazenamento", WStorage,
        [
            new("storage", 2, StorageFree),
            // Desgaste NAND: info = classe abaixo do limiar de aviso —
            // vida gasta conta um pouco (85), warn = perto do fim (40).
            new("storage.wear", 1, (k, c) => ByStatus(c, warn: 40, info: 85)),
        ]),
        new("security", "segurança", WSecurity,
        [
            // Bloqueios pesam mais: MDM e activation lock são red flags de
            // retoma — um warn aqui vale quase um fail noutra chave.
            new("security.mdm", 2, (k, c) => ByStatus(c, warn: 35)),
            new("device.activation", 2, (k, c) => ByStatus(c, warn: 30, fail: 20)),
            new("security.findmy", 2, (k, c) => c?.Status == "warn" ? 25.0 : ByStatus(c)),
            new("security.root", 1, (k, c) => ByStatus(c, warn: 50)),
            // Contas ativas = risco FRP na revenda, não defeito — warn a 60
            // (todo o telefone de cliente as tem; o Grader já explica).
            new("security.accounts", 1, (k, c) => ByStatus(c, warn: 60)),
            new("security.bootloader", 1, (k, c) => ByStatus(c, warn: 55)),
            new("security.warranty", 1, (k, c) => ByStatus(c, warn: 55, fail: 20)),
            new("security.oemunlock", 1, (k, c) => ByStatus(c, warn: 65)),
            new("security.selinux", 1, (k, c) => ByStatus(c, warn: 50)),
            new("security.build", 1, (k, c) => ByStatus(c, warn: 55)),
            // info = ro.crypto.state != "encrypted" — dados do cliente em
            // claro num telemóvel de retoma é uma falha de segurança real.
            new("security.crypto", 1, (k, c) => c?.Status == "info" ? 60.0 : ByStatus(c)),
            // device.sim warn = PIN/PUK do cartão do cliente — não é
            // defeito do aparelho (o Grader só capa em fail, mesmo raciocínio).
            new("device.sim", 1, (k, c) => ByStatus(c, warn: 85, fail: 20)),
            // Auditoria Pro de apps — só conta quando correu (skipped/info
            // de contexto ficam fora pelo ByStatus).
            new("security.audit.sideload", 1, (k, c) => ByStatus(c)),
            new("security.audit.permissions", 1, (k, c) => ByStatus(c)),
            new("security.audit.installpackages", 1, (k, c) => ByStatus(c)),
            new("security.audit.accessibility", 1, (k, c) => ByStatus(c)),
            new("security.audit.verifier", 1, (k, c) => ByStatus(c)),
            new("security.audit.disabled", 1, (k, c) => ByStatus(c)),
        ]),
        new("stability", "estabilidade", WStability,
        [
            // Kernel panic = suspeita de hardware — o mais grave do grupo.
            new("logs.panics", 2, (k, c) => ByStatus(c, warn: 30)),
            // logs.crashes info = crashes de apps/jetsam — software do
            // cliente, não saúde do aparelho: só conta quando sobe a warn
            // (falhas de sistema repetidas).
            new("logs.crashes", 1, (k, c) => ByStatus(c, warn: 50)),
            // Panic thermalmonitord = padrão de flex/sensor térmico.
            new("logs.thermal", 1, (k, c) => ByStatus(c, warn: 40)),
            new("usb.stability", 1, (k, c) => ByStatus(c, warn: 45)),
        ]),
        new("temperature", "temperatura", WTemperature,
        [
            // Leitura normal é "info" e vale 100 — o warn (>40 °C bateria,
            // >42 °C em carga) é que desconta.
            new("battery.temperature", 1, (k, c) => ByStatus(c, warn: 45, info: 100)),
            new("battery.temp", 1, (k, c) => ByStatus(c, warn: 45, info: 100)),
            new("power.temp", 1, (k, c) => ByStatus(c, warn: 45, info: 100)),
            // Célula vs SoC: info = aquecimento equilibrado/normal (95 —
            // "calor do processador" é esperado sob carga, não prova de
            // saúde térmica da bateria).
            new("power.thermal", 1, (k, c) => ByStatus(c, warn: 40, info: 95)),
        ]),
        new("test", "teste no aparelho", WTest,
        [
            // Só entram checks em que fail/warn refletem o aparelho: toque,
            // ecrã, som, vibração. Os sensores de ambiente (accel/gyro/
            // orient/light) ficam de fora de propósito — dependem de browser
            // API e permissão, e um "no-data" é limitação do browser, não
            // defeito do hardware (o mesmo raciocínio do TestResultMapper).
            // Toque falhado = ecrã defeituoso — o fail mais caro do score.
            new("test.touch", 2, (k, c) => ByStatus(c, fail: 15)),
            new("test.screen", 1, (k, c) => ByStatus(c)),
            new("test.edges", 1, (k, c) => ByStatus(c)),
            new("sensor.multitouch", 1, (k, c) => ByStatus(c)),
            new("sensor.speaker", 1, (k, c) => ByStatus(c, fail: 15)),
            new("sensor.earpiece", 1, (k, c) => ByStatus(c, fail: 15)),
            new("sensor.mic", 1, (k, c) => ByStatus(c, fail: 15)),
            new("sensor.vibrate", 1, (k, c) => ByStatus(c, fail: 25)),
        ]),
    ];

    /// <summary>
    /// Checklist físico entra na categoria "teste no aparelho": são marcas
    /// do técnico sobre funcionalidade real (botões, porta, biometria).
    /// Os 12 itens pesam 1 cada — o peso fino já está no scorer.
    /// </summary>
    private static IEnumerable<Signal> ChecklistSignals()
    {
        foreach (var item in Grader.ChecklistItems)
            yield return new Signal(item.Key, 1, Checklist);
    }

    /// <summary>Cálculo puro — não escreve nada no relatório.</summary>
    public static HealthScoreResult Compute(DeviceReport r)
    {
        CheckResult? Get(string key) =>
            r.Results.TryGetValue(key, out var c) ? c : null;

        var scored = new List<(Category Cat, int Score)>();
        var breakdown = new List<HealthCategoryScore>();

        foreach (var cat in Categories)
        {
            var signals = cat.Signals.AsEnumerable();
            if (cat.Key == "test")
                signals = signals.Concat(ChecklistSignals());
            var contribs = new List<(double W, double Score, string? Trace)>();
            foreach (var s in signals)
            {
                var c = Get(s.Key);
                var pts = s.Score(s.Key, c);
                if (pts is not { } p) continue; // ausente/skipped/info-contexto
                contribs.Add((s.Weight, p,
                    c is null ? null : $"{s.Key}={c.Value ?? c.Status}"));
            }

            if (contribs.Count == 0)
            {
                breakdown.Add(new HealthCategoryScore(cat.Key, null, null));
                continue;
            }

            var wSum = contribs.Sum(x => x.W);
            var avg = contribs.Sum(x => x.W * x.Score) / wSum;
            var score = (int)Math.Round(avg, MidpointRounding.AwayFromZero);
            var detail = DetailOf(contribs);
            scored.Add((cat, score));
            breakdown.Add(new HealthCategoryScore(cat.Key, score, detail));
        }

        if (scored.Count == 0)
        {
            return new HealthScoreResult
            {
                Score = null,
                Band = HealthBand.None,
                Categories = breakdown,
                Verdict = "sem dados de saúde — liga um telefone ou corre o SCAN primeiro",
            };
        }

        var wTotal = scored.Sum(x => x.Cat.Weight);
        var overall = scored.Sum(x => x.Cat.Weight * x.Score) / wTotal;
        var total = (int)Math.Round(overall, MidpointRounding.AwayFromZero);
        var band = total >= 75 ? HealthBand.Healthy : total >= 50 ? HealthBand.Fair : HealthBand.Concerning;

        var bandPt = band switch
        {
            HealthBand.Healthy => "Saudável",
            HealthBand.Fair => "Razoável",
            _ => "Preocupante",
        };
        var parts = scored
            .OrderBy(x => x.Score)
            .Select(x => $"{x.Cat.LabelPt} {x.Score}")
            .ToList();
        var verdict = $"{bandPt} — {string.Join(" · ", parts)}";

        return new HealthScoreResult
        {
            Score = total,
            Band = band,
            Categories = breakdown,
            Verdict = verdict,
        };
    }

    /// <summary>
    /// Escreve o score e o breakdown como checks health.* do relatório —
    /// viajam no JSON/HTML/intake como o grade.overall. Devolve o resultado
    /// para a UI mostrar a etiqueta.
    /// </summary>
    public static HealthScoreResult Apply(DeviceReport r)
    {
        var res = Compute(r);
        r.Set("health.score",
            res.Score is null ? "skipped" : StatusFor(res.Score.Value),
            res.Score is { } s ? $"{s}/100" : "—",
            res.Verdict);
        foreach (var cat in res.Categories)
        {
            if (cat.Score is not { } cs) continue;
            r.Set($"health.{cat.Key}", StatusFor(cs), $"{cs}/100", cat.Detail);
        }
        return res;
    }

    /// <summary>
    /// Detail da categoria = os descontos, rastreáveis ao check que os causou
    /// ("battery.capacity=55%"). Sem descontos → null: o "100" já diz.
    /// </summary>
    private static string? DetailOf(List<(double W, double Score, string? Trace)> contribs)
    {
        var descontos = contribs
            .Where(x => x.Score < 100 && x.Trace is not null)
            .Select(x => x.Trace!)
            .ToList();
        return descontos.Count == 0 ? null : string.Join(" · ", descontos);
    }
}
