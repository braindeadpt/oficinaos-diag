using OficinaDiag.Devices;

namespace OficinaDiag.Tests;

/// <summary>Um check perf.* calculado — ApplyTo escreve-o tal qual no relatório.</summary>
public readonly record struct PerfCheck(string Key, string Status, string? Value, string? Detail);

/// <summary>
/// Resultado analisado do perfil de desempenho [PERFIL] — transforma-se nos
/// checks <c>perf.*</c> do relatório. Os valores são medições reais da sessão
/// (temperaturas, corrente, frequência, drain, quedas USB, fps da página) —
/// a comparação antes/depois é trabalho do CompareWindow/relatório, nunca
/// um veredicto inventado aqui.
/// </summary>
public sealed class PerfProfileResult
{
    /// <summary>Checks calculados — ApplyTo escreve-os tal qual.</summary>
    public required IReadOnlyList<PerfCheck> Checks { get; init; }
    public required string Summary { get; init; }
    public int SampleCount { get; init; }
    public int DurationSec { get; init; }

    /// <summary>Frase PT para o veredicto da janela — "pico SoC 52 °C · ≈340 mA · …".</summary>
    public string Verdict => Summary;

    /// <summary>Escreve os checks perf.* no relatório — viajam no intake/IA como os outros.</summary>
    public void ApplyTo(DeviceReport r)
    {
        foreach (var c in Checks)
            r.Set(c.Key, c.Status, c.Value, c.Detail);
    }
}

/// <summary>
/// Analisa a sessão de perfil: fase 1 = base (telemóvel quieto), fase 2 =
/// stress (página on-device a carregar CPU/GPU), fase 3 = recuperação.
/// Regras honestas:
/// - Métrica que o hardware/driver não expõe → <c>skipped</c>, nunca zero
///   nem «tudo bem» (corrente, frequência de CPU, percentagem de bateria).
/// - <c>perf.workload</c> é a testemunha do stress: sem confirmação da página
///   os números térmicos/corrente podem ser de um telemóvel em idle — diz-se.
/// - Warns são reservados a anomalias reais: temperaturas extremas, quedas
///   USB, stress que não correu ou throttling severo. O resto é medição
///   (info) para comparação antes/depois.
/// </summary>
public static class PerfAnalyzer
{
    /// <summary>SoC acima disto durante stress sustentado é extremo — zonas térmicas Qualcomm chegam a 60–65 °C em carga normal.</summary>
    private const double SocWarnC = 70;
    /// <summary>Célula acima disto é quente para uma bateria — o coletor já avisa a >40 °C numa leitura única; stress justifica margem.</summary>
    private const double BattWarnC = 45;
    /// <summary>fps médio abaixo disto = o stress quase não correu (RAF estrangulado, stress mínimo).</summary>
    private const double FpsWarn = 10;
    /// <summary>Stress que correu menos de metade do pedido não mede nada útil.</summary>
    private const double MinDurationRatio = 0.5;
    /// <summary>Queda sustentada de frequência ≥40% início→fim do stress = throttling térmico provável.</summary>
    private const double ThrottleWarnPct = 40;
    /// <summary>Mínimo de amostras de frequência em cada ponta para a comparação ser significativa.</summary>
    private const int MinFreqSamplesPerSide = 2;

    public static PerfProfileResult Analyze(List<PowerSample> samples, PerfPageStats? page,
        int stressSecsRequested, int usbDrops)
    {
        var checks = new List<PerfCheck>();
        var stress = samples.Where(s => s.Phase == 2).ToList();
        var baseline = samples.Where(s => s.Phase == 1).ToList();
        var dur = (int)(samples.LastOrDefault()?.T ?? 0);

        // ── meta da sessão ──────────────────────────────────────────────
        checks.Add(new PerfCheck("perf.session",
            samples.Count == 0 ? "skipped" : "info",
            samples.Count == 0 ? "—" : $"{samples.Count} amostras · {dur}s",
            samples.Count == 0
                ? "sessão sem amostras — repete o perfil"
                : "perfil de desempenho — base · stress · recuperação"));

        // ── a página confirma que o stress correu? ──────────────────────
        checks.Add(WorkloadCheck(page, stressSecsRequested));

        // ── térmica: SoC vs célula, picos durante o stress ──────────────
        checks.Add(ThermalCheck(stress));

        // ── corrente média durante o stress ────────────────────────────
        checks.Add(CurrentCheck(stress, samples));

        // ── quedas USB na janela da sessão ──────────────────────────────
        checks.Add(new PerfCheck("perf.usb",
            samples.Count == 0 ? "skipped" : usbDrops > 0 ? "warn" : "pass",
            samples.Count == 0 ? "sem observação"
                : usbDrops > 0 ? $"{usbDrops} desconexão(ões)" : "ligação estável",
            samples.Count == 0
                ? "sessão sem amostras — estabilidade USB não observada"
                : usbDrops > 0
                    ? "o host viu o USB cair durante a sessão de perfil — porta com folga, pinos gastos ou cabo fraco"
                    : "sem desconexões durante a sessão de perfil"));

        // ── throttling: frequência de CPU início vs fim do stress ──────
        checks.Add(ThrottleCheck(stress));

        // ── autonomia: % de bateria perdida durante o stress ───────────
        checks.Add(BatteryCheck(stress));

        return new PerfProfileResult
        {
            Checks = checks,
            SampleCount = samples.Count,
            DurationSec = dur,
            Summary = SummaryOf(checks, samples.Count),
        };
    }

    // ── checks individuais ──────────────────────────────────────────────

    private static PerfCheck WorkloadCheck(PerfPageStats? page, int requestedSecs)
    {
        if (page is null)
            return new PerfCheck("perf.workload", "skipped", "sem confirmação",
                "a página de stress não devolveu resultado — as medições podem ser de um telemóvel em idle");

        var engine = page.Engine switch
        {
            "webgl2" => "WebGL2", "webgl" => "WebGL",
            "canvas2d" => "Canvas 2D", _ => "—",
        };
        var fps = page.FpsAvg is { } f ? $"≈{f:0} fps" : "fps n/d";
        var value = $"{engine} · {fps} · {page.Secs:0}s";

        if (page.Engine is null or "none")
            return new PerfCheck("perf.workload", "warn", value,
                "a página não conseguiu arrancar o stress (WebGL/canvas indisponível) — as medições não são de carga");
        if (page.ContextLost)
            return new PerfCheck("perf.workload", "warn", value,
                "o contexto gráfico perdeu-se a meio do stress — resultado pode estar incompleto");
        if (page.Secs < requestedSecs * MinDurationRatio)
            return new PerfCheck("perf.workload", "warn", value,
                $"o stress correu {page.Secs:0}s de {requestedSecs}s pedidos — sessão incompleta");
        if (page.FpsAvg is { } avg && avg < FpsWarn)
            return new PerfCheck("perf.workload", "warn", value,
                "fps médio muito baixo — o stress quase não correu (browser estrangulado ou poupança de bateria)");
        return new PerfCheck("perf.workload", "pass", value,
            "stress on-device confirmado pela página — as medições são de carga real");
    }

    private static PerfCheck ThermalCheck(List<PowerSample> stress)
    {
        var batt = stress.Where(s => s.TempC is { }).Select(s => s.TempC!.Value).ToList();
        var soc = stress.Where(s => s.SocTempC is { }).Select(s => s.SocTempC!.Value).ToList();
        if (batt.Count == 0 && soc.Count == 0)
            return new PerfCheck("perf.thermal", "skipped", "sem leitura",
                "o driver não expôs temperatura durante o stress — medição térmica não observada");

        var parts = new List<string>();
        double? socMax = null, battMax = null;
        if (soc.Count > 0) { socMax = soc.Max(); parts.Add($"SoC pico {socMax:0.#}°C"); }
        if (batt.Count > 0) { battMax = batt.Max(); parts.Add($"célula pico {battMax:0.#}°C"); }

        var hot = (socMax is > SocWarnC) || (battMax is > BattWarnC);
        return new PerfCheck("perf.thermal", hot ? "warn" : "info",
            string.Join(" · ", parts),
            hot
                ? "temperaturas extremas durante o stress sustentado — verificar dissipação térmica"
                : "picos medidos durante o stress — comparar com a repetição pós-manutenção");
    }

    private static PerfCheck CurrentCheck(List<PowerSample> stress, List<PowerSample> all)
    {
        // Mesma honestidade da #53: se NENHUMA amostra trouxe mA, o driver não
        // expõe corrente — skipped, não «0 mA» nem defeito.
        if (!all.Any(s => s.Milliamps is not null))
            return new PerfCheck("perf.current", "skipped", "—",
                "corrente não exposta pelo driver — consumo não medido (não é defeito do aparelho)");
        var withMa = stress.Where(s => s.Milliamps is not null).ToList();
        if (withMa.Count == 0)
            return new PerfCheck("perf.current", "skipped", "—",
                "sem amostras de corrente na fase de stress");

        var avg = withMa.Average(s => s.Milliamps!.Value);
        var charging = withMa.Count(s => s.Charging == true) > withMa.Count / 2;
        return new PerfCheck("perf.current", "info",
            $"≈{avg:0} mA médios{(charging ? " · a carregar" : "")}",
            "corrente média durante o stress — comparável antes/depois");
    }

    private static PerfCheck ThrottleCheck(List<PowerSample> stress)
    {
        var freq = stress.Where(s => s.CpuFreqMhz is not null)
            .OrderBy(s => s.T).Select(s => s.CpuFreqMhz!.Value).ToList();
        if (freq.Count < MinFreqSamplesPerSide * 2)
            return new PerfCheck("perf.throttle", "skipped", "—",
                "frequência de CPU não exposta pelo kernel — throttling não medido");

        // Primeiros vs últimos 25% do stress: a carga é a mesma (a página), por
        // isso uma descida sustentada é a assinatura do throttling térmico.
        var edge = Math.Max(MinFreqSamplesPerSide, freq.Count / 4);
        var early = freq.Take(edge).Average();
        var late = freq.Skip(freq.Count - edge).Average();
        var dropPct = early > 0 ? (early - late) / early * 100 : 0;
        var value = $"~{early:0} → ~{late:0} MHz";

        if (dropPct >= ThrottleWarnPct)
            return new PerfCheck("perf.throttle", "warn", value,
                $"frequência caiu {dropPct:0}% no fim do stress — throttling térmico provável");
        return new PerfCheck("perf.throttle", "info", value,
            dropPct > 0
                ? $"frequência descent {dropPct:0}% — dentro do esperado sob stress"
                : "frequência estável ou subiu durante o stress");
    }

    private static PerfCheck BatteryCheck(List<PowerSample> stress)
    {
        var pct = stress.Where(s => s.Percent is not null)
            .OrderBy(s => s.T).Select(s => s.Percent!.Value).ToList();
        if (pct.Count < 2)
            return new PerfCheck("perf.battery", "skipped", "—",
                "percentagem de bateria não exposta durante o stress");
        // A carregar o drain é corrompido (a célula pode até subir) — a prova
        // de autonomia só existe a descarregar.
        if (stress.Any(s => s.Charging == true))
            return new PerfCheck("perf.battery", "skipped", "—",
                "a carregar durante o stress — autonomia não medida");

        var drain = pct[0] - pct[^1];
        var secs = stress.Count > 1 ? stress[^1].T - stress[0].T : 0;
        return new PerfCheck("perf.battery", "info",
            $"−{drain:0.#}% em {secs / 60:0.#} min de stress",
            "percentagem de bateria perdida durante o stress — prova de autonomia comparável");
    }

    /// <summary>Resumo de uma linha para o veredicto da janela — só números medidos.</summary>
    private static string SummaryOf(IReadOnlyList<PerfCheck> checks, int samples)
    {
        if (samples == 0) return "sessão sem amostras";
        var parts = new List<string>();
        var thermal = checks.FirstOrDefault(c => c.Key == "perf.thermal");
        if (thermal.Value is { } tv) parts.Add(tv);
        var cur = checks.FirstOrDefault(c => c.Key == "perf.current");
        if (cur.Value is { } cv) parts.Add(cv);
        var batt = checks.FirstOrDefault(c => c.Key == "perf.battery");
        if (batt.Value is { } bv) parts.Add(bv);
        var usb = checks.FirstOrDefault(c => c.Key == "perf.usb");
        if (usb.Value is { } uv) parts.Add(uv);
        return parts.Count > 0 ? string.Join(" · ", parts) : $"{samples} amostras";
    }
}
