namespace OficinaDiag.Devices;

/// <summary>Uma amostra da sessão de potência [SCAN+]. Phase: 1=base, 2=carga de trabalho, 3=recuperação.</summary>
public sealed record PowerSample(double T, int Phase, int? Milliamps, double? Volts,
    double? TempC, int? Percent, bool? Charging, double? NegotiatedWatts);

/// <summary>Resultado analisado da sessão — transforma-se em checks do relatório.</summary>
public sealed class PowerSessionResult
{
    public required string ChargeStatus { get; init; }
    public required string ChargeValue { get; init; }
    public required string ChargeDetail { get; init; }
    public double? RealWatts { get; init; }
    public int? InternalResistanceMohm { get; init; }
    public int? VoltageSagMv { get; init; }
    public double? MaxTempC { get; init; }
    public int SampleCount { get; init; }
    public int DurationSec { get; init; }

    /// <summary>Escreve os checks da sessão no relatório — viajam no intake/IA como os outros.</summary>
    public void ApplyTo(DeviceReport r)
    {
        r.Set("power.charge", ChargeStatus, ChargeValue, ChargeDetail);
        if (InternalResistanceMohm is { } ir)
            r.Set("power.resistance", ir > 400 ? "warn" : "info", $"{ir} mΩ",
                ir > 400
                    ? "célula envelhecida — previsível desligar antes dos 10–15%"
                    : "ΔV/ΔI sob carga de ecrã — dentro do saudável");
        if (VoltageSagMv is { } sag && InternalResistanceMohm is null)
            r.Set("power.resistance", sag > 150 ? "warn" : "info", $"{sag} mV de queda",
                "sag de tensão sob carga — resistência interna indeterminada neste driver");
        if (MaxTempC is { } t)
            r.Set("power.temp", t > 42 ? "warn" : "info", $"{t:0.#} °C",
                t > 42 ? "aqueceu durante o teste" : null);
        r.Set("power.session", "info", $"{SampleCount} amostras · {DurationSec}s",
            "sessão SCAN+ — base · carga de ecrã · recuperação");
    }
}

public static class PowerAnalyzer
{
    /// <summary>
    /// Corrente líquida com sinal: + a carregar, − a descarregar.
    /// Drivers Android sem sinal ficam positivos — o IR pode ficar indeterminado.
    /// </summary>
    private static double? NetMa(PowerSample s) => s.Milliamps is { } m
        ? s.Charging == false ? -m : m
        : null;

    /// <summary>Analisa: fase 1 = base (cabo PC, ~2.5W), fase 2 = ecrã+câmara ligada, fase 3 = recuperação.</summary>
    public static PowerSessionResult Analyze(List<PowerSample> samples)
    {
        var baseSamples = samples.Where(s => s.Phase == 1).ToList();
        var load = samples.Where(s => s.Phase == 2).ToList();
        var maxTemp = samples.Where(s => s.TempC is { }).Select(s => s.TempC!.Value)
            .DefaultIfEmpty().Max();
        var dur = (int)(samples.LastOrDefault()?.T ?? 0);

        // Carga via porta do PC — honestamente é só isso que o cabo de dados permite medir
        var charging = samples.Where(s => s.Phase != 2 && s.Milliamps is > 30 && s.Charging != false).ToList();
        double? realW = null;
        var withW = charging.Where(s => s.Volts is { }).ToList();
        if (withW.Count > 2)
            realW = withW.Average(s => s.Volts!.Value * s.Milliamps!.Value / 1000.0);
        var avgMa = charging.Count > 0 ? charging.Average(s => s.Milliamps ?? 0) : 0;

        // Resistência interna: ΔV/ΔI na transição base→carga de trabalho.
        // Sob carga o telemóvel puxa mais da bateria → tensão afunda I·R.
        int? irMohm = null;
        int? sagMv = null;
        var baseTail = baseSamples.Where(s => s.Volts is { } && s.Milliamps is { }).TakeLast(5).ToList();
        var loadHead = load.Skip(1).Where(s => s.Volts is { } && s.Milliamps is { }).Take(5).ToList();
        if (baseTail.Count >= 3 && loadHead.Count >= 2)
        {
            var vBase = baseTail.Average(s => s.Volts!.Value);
            var vLoad = loadHead.Average(s => s.Volts!.Value);
            var iBase = baseTail.Select(s => NetMa(s) ?? 0).Average();
            var iLoad = loadHead.Select(s => NetMa(s) ?? 0).Average();
            sagMv = (int)((vBase - vLoad) * 1000);
            var dI = (iBase - iLoad) / 1000.0; // A — positivo quando a bateria passa a dar mais
            var ir = vBase - vLoad;            // V — sag positivo
            if (dI > 0.15 && ir > 0.010)
            {
                var mohm = ir / dI * 1000;
                if (mohm is > 20 and < 2000) irMohm = (int)mohm;
            }
        }

        // Veredicto do caminho de carga (porta USB do PC — 2.5W é o normal)
        string status, detail;
        if (charging.Count == 0)
        {
            status = "warn";
            detail = "não entra carga pela porta — cabo sem dados+power, porta suja ou circuito de carga morto";
        }
        else if (realW is { } rw)
        {
            status = rw < 0.8 ? "warn" : "info";
            detail = rw < 0.8
                ? $"só {rw:0.#} W nem pela porta do PC — suspeita de porta/cabo"
                : $"{rw:0.#} W via porta USB do PC — caminho de carga íntegro (carregador de parede não medido)";
        }
        else
        {
            status = "info";
            detail = $"aceita carga (~{avgMa:0} mA) via porta do PC";
        }

        return new PowerSessionResult
        {
            ChargeStatus = status,
            ChargeValue = realW is { } rw2 ? $"{rw2:0.#} W" : $"{avgMa:0} mA",
            ChargeDetail = detail,
            RealWatts = realW,
            InternalResistanceMohm = irMohm,
            VoltageSagMv = sagMv,
            MaxTempC = maxTemp > 0 ? maxTemp : null,
            SampleCount = samples.Count,
            DurationSec = dur,
        };
    }
}
