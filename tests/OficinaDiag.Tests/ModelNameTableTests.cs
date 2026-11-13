using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

public class ModelNameTableTests
{
    // iPhone ProductType → nome comercial: o collector iOS resolve pela
    // tabela partilhada — nunca pelo DeviceName, que é o nome pessoal do
    // dono e não pode ir para o relatório.
    [Theory]
    [InlineData("iPhone13,3", "iPhone 12 Pro")]
    [InlineData("iPhone14,5", "iPhone 13")]
    [InlineData("iPhone14,6", "iPhone SE (3ª geração)")]
    [InlineData("iPhone16,2", "iPhone 15 Pro Max")]
    [InlineData("iPhone17,5", "iPhone 16e")]
    [InlineData("iPhone18,4", "iPhone Air")]
    public void ResolvesIphoneProductTypes(string code, string expected) =>
        Assert.Equal(expected, ModelNameTable.Lookup(code));

    [Theory]
    [InlineData("iPhone99,9")]      // não catalogado → caller fica com o código cru
    [InlineData("SM-S918B-extra")]
    public void UnknownCodeReturnsNull(string code) =>
        Assert.Null(ModelNameTable.Lookup(code));
}
