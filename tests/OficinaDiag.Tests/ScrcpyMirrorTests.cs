using OficinaDiag.Devices;
using Xunit;

namespace OficinaDiag.Tests;

/// <summary>
/// O scrcpy é lançado com argumentos montados a partir do serial que o
/// detetor viu no <c>adb devices</c> — um serial malicioso ou truncado
/// virava flag do scrcpy. O path do exe e a env ADB são o contrato com o
/// pacote (fetch-tools.ps1 + csproj).
/// </summary>
public sealed class ScrcpyMirrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scrcpy-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Cria tools/scrcpy/scrcpy.exe debaixo de um dir base falso.</summary>
    private string ComScrcpy()
    {
        var pasta = Path.Combine(_dir, "tools", "scrcpy");
        Directory.CreateDirectory(pasta);
        File.WriteAllBytes(Path.Combine(pasta, "scrcpy.exe"), [0]);
        return _dir;
    }

    [Fact]
    public void FindExe_SemPastaDevolveNull()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(ScrcpyMirror.FindExe(_dir));
    }

    [Fact]
    public void FindExe_ComExeDevolveOCaminho()
    {
        var baseDir = ComScrcpy();
        var exe = ScrcpyMirror.FindExe(baseDir);
        Assert.NotNull(exe);
        Assert.EndsWith("scrcpy.exe", exe);
    }

    [Theory]
    [InlineData("emulator-5554")]
    [InlineData("0A1B2C3D4E5F")]
    [InlineData("192.168.1.10:5555")]
    [InlineData("usb:1-2")]
    public void BuildArguments_SerialAdbValido(string serial)
    {
        Assert.Equal($"-s {serial}", ScrcpyMirror.BuildArguments(serial));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-s")]
    [InlineData("--help")]
    [InlineData("a b")]
    [InlineData("a\"b")]
    [InlineData("a;b")]
    [InlineData("a$b")]
    public void BuildArguments_SerialInvalidoDevolveNull(string? serial)
    {
        Assert.Null(ScrcpyMirror.BuildArguments(serial));
    }

    [Fact]
    public void BuildArguments_TituloViraArgumentoUnicoSemAspasInternas()
    {
        var args = ScrcpyMirror.BuildArguments("ABC123", "OFICINA-OS // DIAG — \"ABC123\"");
        Assert.Equal("-s ABC123 --window-title=\"OFICINA-OS // DIAG — ABC123\"", args);
    }

    [Fact]
    public void BuildArguments_SemTituloNaoEmiteWindowTitle()
    {
        Assert.Equal("-s ABC123", ScrcpyMirror.BuildArguments("ABC123", "  "));
    }

    [Fact]
    public void CreateProcessInfo_SemExeDevolveNull()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(ScrcpyMirror.CreateProcessInfo(_dir, "adb.exe", "ABC123"));
    }

    [Fact]
    public void CreateProcessInfo_SemSerialValidoDevolveNull()
    {
        var baseDir = ComScrcpy();
        Assert.Null(ScrcpyMirror.CreateProcessInfo(baseDir, "adb.exe", "-x"));
    }

    [Fact]
    public void CreateProcessInfo_MontaLauncherSemConsolaComAdbBundled()
    {
        var baseDir = ComScrcpy();
        var adb = Path.Combine(_dir, "tools", "platform-tools", "adb.exe");
        var psi = ScrcpyMirror.CreateProcessInfo(baseDir, adb, "ABC123", "titulo");

        Assert.NotNull(psi);
        Assert.EndsWith("scrcpy.exe", psi!.FileName);
        Assert.Equal("-s ABC123 --window-title=\"titulo\"", psi.Arguments);
        // Sem consola nem espera: o scrcpy desenha a janela SDL ele próprio.
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        // O ADB env é o que evita a guerra de versões do servidor adb com
        // o detetor — o scrcpy tem de usar o adb do platform-tools.
        Assert.Equal(adb, psi.Environment["ADB"]);
    }
}
