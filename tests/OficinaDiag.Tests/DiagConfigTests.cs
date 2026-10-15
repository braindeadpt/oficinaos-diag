using System.Text.Json;
using OficinaDiag.Cloud;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// U3: o diag só aceita o código de emparelhamento — tokens "full" antigos
/// guardados no config são apagados no Load() e nunca voltam a ser usados.
/// DPAPI (ProtectedData) só existe em Windows — os testes aqui não tocam
/// em Protect/Unprotect, por isso correm em Linux/macOS também.
/// </summary>
public class DiagConfigTests : IDisposable
{
    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "oficinaos-diag.config.json");

    public DiagConfigTests()
    {
        try { File.Delete(ConfigPath); } catch { }
        try { File.Delete(ConfigPath + ".tmp"); } catch { }
    }

    public void Dispose()
    {
        try { File.Delete(ConfigPath); } catch { }
        try { File.Delete(ConfigPath + ".tmp"); } catch { }
    }

    private static void WriteConfig(object cfg) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg));

    [Fact]
    public void Load_WipesLegacyTokenWithoutScope()
    {
        // Config de antes dos scopes: token completo da loja em DPAPI.
        WriteConfig(new { CloudUrl = "https://cloud.oficinaos.app", ShopTokenProtected = "Zm9v" });
        var cfg = DiagConfig.Load();
        Assert.Null(cfg.ShopTokenProtected);
        Assert.Null(cfg.ShopTokenScope);
        Assert.True(cfg.LegacyTokenWiped);
    }

    [Fact]
    public void Load_WipesTokenWithForeignScope()
    {
        // Um scope desconhecido não é "diag" — fora com ele.
        WriteConfig(new { CloudUrl = "https://cloud.oficinaos.app", ShopTokenProtected = "Zm9v", ShopTokenScope = "full" });
        var cfg = DiagConfig.Load();
        Assert.Null(cfg.ShopTokenProtected);
        Assert.True(cfg.LegacyTokenWiped);
    }

    [Fact]
    public void Load_KeepsScopedDiagToken()
    {
        WriteConfig(new { CloudUrl = "https://cloud.oficinaos.app", ShopTokenProtected = "Zm9v", ShopTokenScope = "diag" });
        var cfg = DiagConfig.Load();
        Assert.Equal("Zm9v", cfg.ShopTokenProtected);
        Assert.Equal("diag", cfg.ShopTokenScope);
        Assert.False(cfg.LegacyTokenWiped);
    }

    [Fact]
    public void Load_NoToken_NoWipeFlag()
    {
        WriteConfig(new { CloudUrl = "https://cloud.oficinaos.app" });
        var cfg = DiagConfig.Load();
        Assert.Null(cfg.ShopTokenProtected);
        Assert.False(cfg.LegacyTokenWiped);
    }

    [Theory]
    [InlineData("ABCD-1234", true)]
    [InlineData("5axu-69v4", true)]   // case-insensitive — quem escreve em minúsculas não falha
    [InlineData("abcd -1234", false)]
    [InlineData("ABCD1234", false)]   // sem o hífen
    [InlineData("ABCDE-1234", false)]
    [InlineData("EtvuTth8P2yqRkK9z_RP9orwTbXvcL2WikpVLOpCmKI", false)]  // token completo → recusado
    [InlineData("", false)]
    public void IsPairingCode_OnlyAcceptsCodeShape(string input, bool expected) =>
        Assert.Equal(expected, IntakeRules.IsPairingCode(input));
}
