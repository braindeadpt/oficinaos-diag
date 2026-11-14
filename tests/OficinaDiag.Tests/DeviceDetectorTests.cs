using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// T-09/T-06: o `Poll` é o caminho que mantém a lista de aparelhos viva.
/// Estes testes usam a fonte falsa (sem adb, sem timer) e chamam a ronda à
/// mão: dedupe por UDID, anexar/refrescar/desanexar, a guarda de reentrância
/// e as exceções que não podem escapar de um callback de timer.
/// </summary>
/// O `Poll` regista exceções em `AppLog`, cujo destino é estático — correr
/// ao lado do `AppLogTests`, que o redireciona, fazia-as escrever no ficheiro
/// errado. Na coleção serial, cada um tem o `_sink` só para si.
[Collection(SerialCollection.Name)]
public sealed class DeviceDetectorTests
{
    private static DetectedDevice Ios(string id, string label = "iPhone", string state = "ready") =>
        new(DeviceKind.Ios, id, label, state);

    // --- dedupe (extraída do Poll no PR do T-09) --------------------------

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

    // --- uma ronda (PollOnce, via Poll) ----------------------------------

    [Fact]
    public void RondaAnexa_UmaVezPorAparelho()
    {
        var fonte = new List<DetectedDevice> { Ios("A") };
        using var det = new DeviceDetector(() => fonte);
        var anexados = new List<DetectedDevice>();
        det.Attached += anexados.Add;

        det.Poll();
        det.Poll();   // mesma fonte: não pode voltar a anunciar

        var unico = Assert.Single(anexados);
        Assert.Equal("A", unico.Id);
        Assert.Equal(1, det.SeenCount);
    }

    [Fact]
    public void EstadoMuda_RefrescaSemSegundoAnexo()
    {
        var atual = Ios("A", "iPhone", "unauthorized");
        using var det = new DeviceDetector(() => new[] { atual });
        var anexados = 0;
        det.Attached += _ => anexados++;

        det.Poll();
        // O utilizador autorizou no telefone: mesmo UDID, estado novo.
        atual = Ios("A", "iPhone", "ready");
        det.Poll();

        Assert.Equal(1, anexados);
        Assert.Equal("ready", det.SeenById("A")!.State);
    }

    [Fact]
    public void AparelhoDesaparece_Desanexa()
    {
        var fonte = new List<DetectedDevice> { Ios("A") };
        using var det = new DeviceDetector(() => fonte);
        var desanexados = new List<DetectedDevice>();
        det.Detached += desanexados.Add;

        det.Poll();
        fonte.Clear();
        det.Poll();

        var unico = Assert.Single(desanexados);
        Assert.Equal("A", unico.Id);
        Assert.Equal(0, det.SeenCount);
    }

    // --- o que não pode derrubar a app -----------------------------------

    [Fact]
    public void HandlerQueRebenta_NaoEscapaNemTravaARondaSeguinte()
    {
        var fonte = new List<DetectedDevice> { Ios("A") };
        using var det = new DeviceDetector(() => fonte);
        Action<DetectedDevice> mau = _ => throw new InvalidOperationException("dispatcher fechado");
        det.Attached += mau;

        // Exceção que escapa a um callback de timer termina o processo.
        det.Poll();
        det.Attached -= mau;   // o dispatcher voltou: a ronda seguinte tem de passar

        var anexados = new List<DetectedDevice>();
        det.Attached += anexados.Add;
        fonte.Add(Ios("B"));
        det.Poll();

        var unico = Assert.Single(anexados);
        Assert.Equal("B", unico.Id);
        Assert.Equal(2, det.SeenCount);
    }

    [Fact]
    public void FonteQueRebenta_NaoEscapaNemTravaARondaSeguinte()
    {
        var falhar = true;
        using var det = new DeviceDetector(() =>
            falhar ? throw new IOException("adb morreu") : new[] { Ios("A") });

        det.Poll();
        falhar = false;
        det.Poll();

        Assert.Equal(1, det.SeenCount);
    }

    [Fact]
    public void RondaSobreposta_EhSaltada()
    {
        using var det = new DeviceDetector(() => Array.Empty<DetectedDevice>());

        Assert.True(det.BeginPoll());
        Assert.False(det.BeginPoll());   // a ronda anterior ainda não acabou
        det.EndPoll();
        Assert.True(det.BeginPoll());
        det.EndPoll();
    }
}
