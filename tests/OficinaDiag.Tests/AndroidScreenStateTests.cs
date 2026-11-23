using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// Estado de ecrã do perfil Android — lido antes de o perfil mexer e
/// reposto no fim. As regras: só se escrevem valores realmente capturados
/// ("null"/lixo do settings get não vira setting inventado) e um stay-on
/// desconhecido cai no defeito em vez de ficar ligado para sempre.
/// </summary>
public sealed class AndroidScreenStateTests
{
    [Fact]
    public void Parse_TrêsValoresNumericos()
    {
        var st = AndroidCollector.ParseScreenState("180\n0\n7\n");
        Assert.NotNull(st);
        Assert.Equal("180", st!.Brightness);
        Assert.Equal("0", st.BrightnessMode);
        Assert.Equal("7", st.StayOn);
    }

    [Fact]
    public void Parse_NullDoSettings_NaoEValor()
    {
        // settings get imprime "null" quando a chave não existe — tratar
        // como valor escreveria "settings put ... null" no telemóvel.
        var st = AndroidCollector.ParseScreenState("null\nnull\nnull\n");
        Assert.NotNull(st);
        Assert.Null(st!.Brightness);
        Assert.Null(st.BrightnessMode);
        Assert.Null(st.StayOn);
    }

    [Fact]
    public void Parse_LixoOuVazio_SaiNull()
    {
        var st = AndroidCollector.ParseScreenState("abc\n\n12x\n");
        Assert.NotNull(st);
        Assert.Null(st!.Brightness);
        Assert.Null(st.BrightnessMode);
        Assert.Null(st.StayOn); // "12x" não é inteiro — rejeitado
    }

    [Fact]
    public void Parse_SemOutput_Null()
    {
        Assert.Null(AndroidCollector.ParseScreenState(null));
    }

    [Fact]
    public void Restore_ComEstadoLido_EscreveOsTresEODorme()
    {
        var st = new AndroidCollector.ScreenState("102", "1", "0");
        var cmds = AndroidCollector.BuildScreenRestoreCommands(st);
        var joined = string.Join("; ", cmds);

        Assert.Contains("settings put system screen_brightness 102", joined);
        Assert.Contains("settings put system screen_brightness_mode 1", joined);
        Assert.Contains("settings put global stay_on_while_plugged_in 0", joined);
        Assert.Contains("input keyevent KEYCODE_SLEEP", joined);
        Assert.DoesNotContain("svc power stayon false", joined); // valor conhecido não precisa do fallback
    }

    [Fact]
    public void Restore_SemStayOnConhecido_CaiNoDefeito()
    {
        // Se a leitura do stay-on falhou, o perfil pode tê-lo ligado — o
        // fallback desligado é melhor que deixar o ecrã sempre acordado.
        var st = new AndroidCollector.ScreenState("200", "0", null);
        var joined = string.Join("; ", AndroidCollector.BuildScreenRestoreCommands(st));

        Assert.Contains("svc power stayon false", joined);
        Assert.Contains("settings put system screen_brightness 200", joined);
        Assert.DoesNotContain("stay_on_while_plugged_in", joined);
    }

    [Fact]
    public void Restore_TudoDesconhecido_AindaDormeEOApagaStayOn()
    {
        var st = new AndroidCollector.ScreenState(null, null, null);
        var joined = string.Join("; ", AndroidCollector.BuildScreenRestoreCommands(st));

        Assert.Equal("svc power stayon false; input keyevent KEYCODE_SLEEP", joined);
    }
}
