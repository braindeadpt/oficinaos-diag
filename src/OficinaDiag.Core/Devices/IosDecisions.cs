using System.Globalization;

namespace OficinaDiag.Devices;

/// <summary>
/// Decisões do <c>IosCollector</c> sobre o AppleSmartBattery, separadas da
/// leitura por <c>idevicediagnostics</c> para serem testadas sobre a fixture
/// real. O coletor só aplica o <see cref="Reading"/>.
/// </summary>
internal static class IosDecisions
{
    /// <summary>Uma linha do relatório: estado, valor e nota opcional.</summary>
    internal readonly record struct Reading(string Status, string? Value, string? Note);

    /// <summary>
    /// Modelo comercial a partir do `ProductType` (ex.: `iPhone14,2`). O nome
    /// pessoal do cliente (`DeviceName`, «iPhone de Maria») nunca entra aqui:
    /// não há parâmetro para ele, por isso não pode escapar para o relatório.
    /// </summary>
    internal static Reading IdentityModel(string? productType) =>
        ModelNameTable.Lookup(productType) is { } marketing
            ? new Reading("info", $"{marketing} ({productType})", productType)
            : new Reading("info", productType, productType);

    /// <summary>
    /// Capacidade real vs design (as duas vivem em `BatteryData`). Mesmos
    /// limiares do Grader: &lt;60 = D, &lt;80 = C. `null` = falta uma das
    /// leituras, não se escreve capacidade.
    /// </summary>
    internal static Reading? Capacity(int? maxCapacity, int? designCapacity)
    {
        if (maxCapacity is not { } max || designCapacity is not { } design || design <= 0)
            return null;

        var pct = max * 100.0 / design;
        return new Reading(
            pct < 60 ? "fail" : pct < 80 ? "warn" : "pass",
            $"{pct:0}%",
            $"{max}/{design} mAh — carga real vs fábrica");
    }

    /// <summary>
    /// Temperatura em °C: `&gt;150` é a escala antiga em centésimos, e `0` é
    /// ausência de leitura (o `DeadBatteryBootData`), nunca 0 °C. Valores
    /// negativos são medições reais (bancada ao frio) e mantêm-se.
    /// </summary>
    internal static double? Celsius(double? raw)
    {
        if (raw is not { } t)
            return null;
        if (t > 150) t /= 100;
        return t == 0 ? null : t;
    }

    /// <summary>Temperatura em °C a partir do valor cru do plist.</summary>
    internal static double? CelsiusFrom(string? raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? Celsius(t)
            : null;

    /// <summary>
    /// Linha do relatório a partir da temperatura já em °C: só medições
    /// positivas — o relatório do cliente não leva temperaturas abaixo de zero.
    /// </summary>
    internal static Reading? Temperature(double? celsius) => celsius is { } t && t > 0
        ? new Reading(t > 40 ? "warn" : "info", $"{t:0.#} °C",
            t > 40 ? "quente — afeta a saúde a longo prazo" : null)
        : null;

    /// <summary>Linha do relatório a partir do valor cru do plist.</summary>
    internal static Reading? TemperatureFrom(string? raw) => Temperature(CelsiusFrom(raw));

    /// <summary>
    /// Potência do carregador: os watts só existem em `AdapterDetails` e só
    /// interessam com o carregador ligado; `w &gt; 0` descarta o `0` do
    /// `ChargerData`.
    /// </summary>
    internal static Reading? ChargerWatts(bool externalConnected, int? watts, string? description) =>
        externalConnected && watts is { } w && w > 0
            ? new Reading("info", $"{w} W", description)
            : null;

    /// <summary>
    /// Espaço livre do disco (`disk_usage`/`TotalDiskCapacity`, em bytes).
    /// `null` = capacidade total não lida → check ausente. Total lido mas
    /// livre por ler → "skipped": um "0 GB livres" por omissão pareceria
    /// disco cheio e o health score penalizaria um iPhone saudável — a
    /// leitura que falta é neutra, nunca uma medição.
    /// </summary>
    internal static Reading? Storage(string? totalRaw, string? freeRaw)
    {
        if (!ulong.TryParse(totalRaw, out var total) || total == 0)
            return null;
        if (!ulong.TryParse(freeRaw, out var free))
            return new Reading("skipped", "indisponível",
                "espaço livre não exposto por este iOS — utilização do disco por medir");
        return new Reading("info",
            $"{free / 1_073_741_824.0:0.#} GB livres de {total / 1_073_741_824.0:0.#} GB",
            "utilização normal — não é defeito de hardware");
    }
}
