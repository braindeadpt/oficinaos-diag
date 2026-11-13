using System.Globalization;
using OficinaDiag.Devices;
using OficinaDiag.Grading;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// B-02/B-03: o plist do `ioreg AppleSmartBattery` tem a célula no dict
/// principal e a telemetria de carga em sub-dicts (AdapterDetails,
/// ChargerData). Ler o sub-dict por cima da célula trocava 4.15 V por 9 V e
/// `IsCharging` por 0 — e a capacidade só existe em `BatteryData`.
/// </summary>
public sealed class IosCollectorParsingTests
{
    // Estrutura real: muitos pares no topo (sem o embrulho key/dict do
    // ioregentry) e os sub-dicts que outrora achatavam por cima.
    private const string Plist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Voltage</key><integer>4152</integer>
            <key>IsCharging</key><true/>
            <key>NominalChargeCapacity</key><integer>2450</integer>
            <key>DesignCapacity</key><integer>4323</integer>
            <key>AverageBattSkinTemp</key><real>31.456</real>
            <key>Serial</key><string>F8Y1234567</string>
            <key>AdapterDetails</key>
            <dict>
                <key>Voltage</key><integer>9000</integer>
                <key>Watts</key><integer>20</integer>
                <key>Description</key><string>usb host</string>
                <key>Serial</key><string>ADP999</string>
            </dict>
            <key>ChargerData</key>
            <dict>
                <key>IsCharging</key><integer>0</integer>
            </dict>
        </dict>
        </plist>
        """;

    private static void WithCulture(string name, Action body)
    {
        var old = CultureInfo.CurrentCulture;
        var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
            CultureInfo.CurrentUICulture = oldUi;
        }
    }

    [Fact]
    public void SubDictsNaoPisamACelula() => WithCulture("pt-PT", () =>
    {
        var io = IosCollector.ParsePlistDict(Plist);

        // 4.152 V da célula, nunca os 9 V do carregador.
        Assert.Equal("4152", io["Voltage"]);
        Assert.NotEqual("9000", io["Voltage"]);
        // `ChargeData.IsCharging = 0` não pode ganhar ao `true` do topo.
        Assert.Equal("true", io["IsCharging"]);
        Assert.Equal("F8Y1234567", io["Serial"]);
        // O sub-dict não desaparece: os watts só existem lá.
        Assert.Equal("20", io["AdapterDetails.Watts"]);
        // ChargerData não entra de todo — é exactamente o valor que pisava a célula.
        Assert.False(io.ContainsKey("ChargerData.IsCharging"),
            "ChargerData.IsCharging jamais pode ser lido");
        // Real com ponto decimal lido independentemente da cultura.
        Assert.Equal("31.456", io["AverageBattSkinTemp"]);
    });

    [Fact]
    public void UsoDoIdevicediagnostics_NaoEViraPlist()
    {
        // exit 0 + usage no stdout: sem a guarda `<plist` isto passava por
        // plist vazio e a bateria era saltada em silêncio.
        var io = IosCollector.ParsePlistDict("Usage: idevicediagnostics [OPTIONS] COMMAND");
        Assert.Empty(io);
    }

    [Fact]
    public void FixtureRealDoIphone13_3_TemCapacidadeDoBatteryData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "apple-smart-battery-iphone13_3.plist");
        Assert.True(File.Exists(path), $"fixture ausente: {path}");

        var io = IosCollector.ParsePlistDict(File.ReadAllText(path));
        Assert.NotEmpty(io);
        Assert.True(io.ContainsKey("BatteryData.DesignCapacity"),
            "a capacidade de design vive em BatteryData");
        Assert.True(io.ContainsKey("AdapterDetails.Watts"),
            "os watts do carregador vivem em AdapterDetails");
    }

    /// <summary>Fixture real do iPhone 12 Pro (iPhone13,3), serial da célula trocado.</summary>
    private static Dictionary<string, string> Fixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures",
            "apple-smart-battery-iphone13_3.plist");
        Assert.True(File.Exists(path), $"fixture ausente: {path}");
        return IosCollector.ParsePlistDict(File.ReadAllText(path));
    }

    [Fact]
    public void FixtureRealDoIphone13_3_WattsDoCarregadorResolvem13W()
    {
        var io = Fixture();
        // O `ChargerData` traz Watts 0; quem vale é o `AdapterDetails`.
        var watts = int.Parse(io["AdapterDetails.Watts"], CultureInfo.InvariantCulture);

        Assert.Equal(13, watts);
        var charger = IosDecisions.ChargerWatts(true, watts, "usb");
        Assert.NotNull(charger);
        Assert.Equal("13 W", charger!.Value.Value);
    }

    [Fact]
    public void WattsSemCarregadorLigadoNaoViramPotencia() =>
        Assert.Null(IosDecisions.ChargerWatts(false, 13, null));

    [Fact]
    public void FixtureRealDoIphone13_3_NaoEmiteTemperaturaZero()
    {
        var io = Fixture();
        // `AverageBattSkinTemp: 0` é ausência de leitura — 0 °C não é medível.
        Assert.False(io.TryGetValue("AverageBattSkinTemp", out var raw));
        Assert.Null(IosDecisions.Temperature(raw is null
            ? null
            : double.Parse(raw, CultureInfo.InvariantCulture)));
        Assert.Null(IosDecisions.Temperature(0));
        // Escala antiga em centésimos continua a converter, e um 0 real não.
        Assert.Equal(31.5, IosDecisions.Celsius(3150));
        Assert.Null(IosDecisions.Celsius(0));
    }

    [Fact]
    public void FixtureRealDoIphone13_3_CapacidadeE100PorCento()
    {
        var io = Fixture();
        // As duas capacidades vivem em BatteryData (daí o prefixo).
        var max = int.Parse(io["BatteryData.NominalChargeCapacity"], CultureInfo.InvariantCulture);
        var design = int.Parse(io["BatteryData.DesignCapacity"], CultureInfo.InvariantCulture);

        Assert.Equal(2790, max);
        Assert.Equal(2798, design);
        var cap = IosDecisions.Capacity(max, design);
        Assert.NotNull(cap);
        Assert.Equal("pass", cap!.Value.Status);
        Assert.Equal("100%", cap.Value.Value);
        Assert.Contains("2790/2798", cap.Value.Note);
    }

    [Fact]
    public void BateriaA57PorCento_EDGrauDSugereBateria() => WithCulture("pt-PT", () =>
    {
        var report = new DeviceReport { Platform = "ios" };
        report.Device.Serial = "00008101-0002390A3600801E";
        report.Set("battery.capacity", "fail", "57%", "2450 / 4323 mAh");
        report.Set("scan", "pass", "completo");

        var grade = Grader.Apply(report);

        Assert.Equal("D", grade.Grade);
        Assert.Contains("bateria", grade.Parts);
    });
}
