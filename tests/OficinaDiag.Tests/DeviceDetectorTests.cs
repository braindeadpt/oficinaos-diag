using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// T-09: o mesmo aparelho pode ser enumerado por dois transportes na mesma
/// ronda (iPhone por USB e por Wi-Fi). O `Poll` antigo fazia
/// `ToDictionary(d => d.Id)` e rebentava com duplicate key, perdendo a ronda
/// inteira — nunca chegava a dizer que o aparelho estava ligado.
/// </summary>
public sealed class DeviceDetectorTests
{
    private static DetectedDevice Ios(string id, string label = "iPhone", string state = "ready") =>
        new(DeviceKind.Ios, id, label, state);

    [Fact]
    public void MesmoUdidPorDoisTransportes_FicaUmaEntrada()
    {
        var por = DeviceDetector.DedupeById(new[]
        {
            Ios("00008110-001A", "iPhone (USB)"),
            Ios("00008110-001A", "iPhone (Wi-Fi)"),
        });

        var unico = Assert.Single(por);
        Assert.Equal("00008110-001A", unico.Key);
        // Último enumerado vence: é o mais fresco da ronda.
        Assert.Equal("iPhone (Wi-Fi)", unico.Value.Label);
    }

    [Fact]
    public void AparelhosDiferentes_SobrevivemTodos()
    {
        var por = DeviceDetector.DedupeById(new[]
        {
            Ios("00008110-001A"),
            new DetectedDevice(DeviceKind.Android, "R58M1234", "SM-S918B"),
            Ios("00008110-002B", "iPhone", "unauthorized"),
        });

        Assert.Equal(3, por.Count);
        Assert.Equal("unauthorized", por["00008110-002B"].State);
    }

    [Fact]
    public void RondaVazia_NaoRebenta()
    {
        Assert.Empty(DeviceDetector.DedupeById(Array.Empty<DetectedDevice>()));
    }

    [Fact]
    public void OrdemDaEnumeracao_NaoMudaOConjuntoDeIds()
    {
        var a = Ios("1"); var b = Ios("2");
        var direta = DeviceDetector.DedupeById(new[] { a, b });
        var inversa = DeviceDetector.DedupeById(new[] { b, a });

        Assert.Equal(direta.Keys.OrderBy(k => k), inversa.Keys.OrderBy(k => k));
    }
}
