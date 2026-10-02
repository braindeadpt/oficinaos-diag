namespace OficinaDiag.Devices;

/// <summary>Uma amostra da sessão de potência [SCAN+]. Phase: 1=repouso, 2=à espera do carregador, 3=carga.</summary>
public sealed record PowerSample(double T, int Phase, int? Milliamps, double? Volts,
    double? TempC, int? Percent, bool? Charging, double? NegotiatedWatts);

/// <summary>Resultado analisado da sessão — transforma-se em checks do relatório.</summary>
public sealed class PowerSessionResult
{
    public required string ChargeStatus { get; init; }
    public required string ChargeValue { get; init; }
    public required string ChargeDetail { get; init; }
    public double? RealWatts { get; init; }
    public double? NegotiatedWatts { get; init; }
    public int? InternalResistanceMohm { get; init; }
    public double? MaxTempC { get; init; }
    public int SampleCount { get; init; }
    public int DurationSec { get; init; }

    /// <summary>Escreve os checks da sessão no relatório — viajam no intake/IA como os outros.</summary>
    public void ApplyTo(DeviceReport r)
    {
        r.Set("power.charge", ChargeStatus, ChargeValue, ChargeDetail);
        if (NegotiatedWatts is { } neg && RealWatts is { } real)
        {
            var limited = real < neg * 0.5;
            r.Set("power.charger", limited ? "warn" : "info",
                $"{neg:0} W negociados → {real:0.#} W reais",
                limited ? "o cabo/porta está a limitar o carregamento" : "o carregador entrega o que negocia");
        }
        if (InternalResistanceMohm is { } ir)
            r.Set("power.resistance", ir > 400 ? "warn" : "info", $"{ir} mΩ",
                ir > 400
                    ? "célula envelhecida — previsível desligar antes dos 10–15%"
                    : "estimativa ΔV/ΔI na transição repouso→carga");
        if (MaxTempC is { } t)
            r.Set("power.temp", t > 42 ? "warn" : "info", $"{t:0.#} °C",
                t > 42 ? "aqueceu durante o teste de carga" : null);
        r.Set("power.session", "info", $"{SampleCount} amostras · {DurationSec}s",
            "sessão SCAN+ — repouso + carga guiada");
    }
}

public static class PowerAnalyzer
{
    /// <summary>Analisa as amostras: fase 1 = repouso, fase 3 = carga.</summary>
    public static PowerSessionResult Analyze(List<PowerSample> samples)
    {
        var rest = samples.Where(s => s.Phase == 1).ToList();
        var charge = samples.Where(s => s.Phase == 3 && s.Milliamps is > 0).ToList();
        var maxTemp = samples.Where(s => s.TempC is { }).Select(s => s.TempC!.Value)
            .DefaultIfEmpty().Max();
        var dur = (int)(samples.LastOrDefault()?.T ?? 0);

        if (charge.Count < 3)
            return new PowerSessionResult
            {
                ChargeStatus = "fail",
                ChargeValue = "sem carga",
                ChargeDetail = "o telemóvel não aceitou carga durante o teste — porta, cabo ou carregador",
                MaxTempC = maxTemp > 0 ? maxTemp : null,
                SampleCount = samples.Count,
                DurationSec = dur,
            };

        // Watts reais = média de V·I durante a carga
        var withW = charge.Where(s => s.Volts is { } && s.Milliamps is { }).ToList();
        double? realW = withW.Count > 0
            ? withW.Average(s => s.Volts!.Value * s.Milliamps!.Value / 1000.0) : null;
        var negW = charge.Where(s => s.NegotiatedWatts is > 0)
            .Select(s => s.NegotiatedWatts!.Value).DefaultIfEmpty().Max();
        var avgMa = charge.Average(s => s.Milliamps ?? 0);
        var peakMa = charge.Max(s => s.Milliamps ?? 0);

        // Resistência interna ≈ salto de tensão / corrente na transição repouso→carga
        int? irMohm = null;
        var restV = rest.Where(s => s.Volts is { }).TakeLast(5).ToList();
        var earlyCharge = charge.Take(5).Where(s => s.Volts is { } && s.Milliamps is > 100).ToList();
        if (restV.Count >= 3 && earlyCharge.Count >= 2)
        {
            var vRest = restV.Average(s => s.Volts!.Value);
            var vChg = earlyCharge.Average(s => s.Volts!.Value);
            var iChg = earlyCharge.Average(s => s.Milliamps!.Value) / 1000.0;
            var ir = (vChg - vRest) / iChg * 1000;
            if (ir is > 20 and < 2000) irMohm = (int)ir;
        }

        // Veredicto do caminho de carga
        string status, detail;
        if (realW is null)
        {
            status = "info";
            detail = $"carga detetada a ~{avgMa:0} mA — sem tensão para calcular watts";
        }
        else if (maxTemp > 40 && realW < 8)
        {
            status = "warn";
            detail = $"só {realW:0.#} W e bateria a {maxTemp:0.#} °C — provável recusa de carga rápida por temperatura";
        }
        else if (negW > 0 && realW < negW * 0.5)
        {
            status = "warn";
            detail = $"negocia {negW:0} W mas entrega {realW:0.#} W — cabo, porta ou carregador limitam";
        }
        else if (realW < 4)
        {
            status = "warn";
            detail = $"carga fraca ({realW:0.#} W · pico {peakMa} mA) — porta suja, cabo mau ou carregador fraco";
        }
        else
        {
            status = "pass";
            detail = $"{realW:0.#} W reais a entrar (pico {peakMa} mA)";
        }

        return new PowerSessionResult
        {
            ChargeStatus = status,
            ChargeValue = realW is { } rw ? $"{rw:0.#} W" : $"{avgMa:0} mA",
            ChargeDetail = detail,
            RealWatts = realW,
            NegotiatedWatts = negW > 0 ? negW : null,
            InternalResistanceMohm = irMohm,
            MaxTempC = maxTemp > 0 ? maxTemp : null,
            SampleCount = samples.Count,
            DurationSec = dur,
        };
    }
}
